using System.Net;
using System.Net.Http.Json;
using MetroOps.Contracts;
using MetroOps.Domain;
using MetroOps.OperationsApi;
using MetroOps.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MetroOps.Runtime.Tests;

public sealed class AuthorityTests
{
    [Theory]
    [InlineData("advise")]
    [InlineData("approval")]
    public void AgentCannotExecuteInReadOrApprovalModes(string mode)
    {
        var world = new WorldState { AuthorityMode = mode };
        var command = Command(world, new ServiceAction("hold-train", "blue-1", Amount: 60));
        var result = CommandPolicy.Apply(world, command, agent: true);
        Assert.False(result.Accepted);
        Assert.Equal("denied", result.Status);
        Assert.Empty(world.Commands);
    }

    [Theory]
    [InlineData("incident", "central-exchange", "elevator-outage", 600)]
    [InlineData("withdraw-train", "blue-1", null, 0)]
    [InlineData("hold-train", "blue-1", null, 181)]
    [InlineData("set-headway", "Blue", null, 30)]
    public void BoundedAgentCannotExceedItsApplicationPermissions(string kind, string target, string? value, double amount)
    {
        var world = new WorldState { AuthorityMode = "bounded" };
        var result = CommandPolicy.Apply(world, Command(world, new ServiceAction(kind, target, value, amount)), agent: true);
        Assert.False(result.Accepted);
        Assert.Equal("denied", result.Status);
    }

    [Fact]
    public void RecentAgentActionEnforcesWallClockBudget()
    {
        var world = new WorldState { AuthorityMode = "bounded", LastAgentActionAt = DateTimeOffset.UtcNow };
        var result = CommandPolicy.Apply(world, Command(world, new ServiceAction("release-train", "blue-1")), agent: true);
        Assert.False(result.Accepted);
        Assert.Contains("30 wall-clock", result.Message);
    }

    [Fact]
    public void ApprovalCannotApplyAcrossAReset()
    {
        var world = new WorldState { Generation = 2, AuthorityMode = "approval" };
        var command = Command(world, new ServiceAction("deploy-reserve", "reserve-ba", "Blue")) with { Generation = 1 };
        var result = CommandPolicy.Apply(world, command, agent: false, approved: true);
        Assert.False(result.Accepted);
        Assert.Equal("stale", result.Status);
    }

    [Fact]
    public void ApprovalRevalidatesAgainstLiveStateAfterOrdinaryVersionChanges()
    {
        var world = Kernel.Create();
        world.AuthorityMode = "approval";
        var command = Command(world, new ServiceAction("hold-train", "blue-1", Amount: 60));
        world.Version += 20;

        var result = CommandPolicy.Apply(world, command, agent: false, approved: true);

        Assert.True(result.Accepted, result.Message);
        Assert.True(world.Trains.Single(t => t.Id == "blue-1").ReadyAt >= world.SimTime.AddSeconds(60));
    }

    [Fact]
    public void BoundedExecutionRecordsItsBudgetOnlyAfterAnAcceptedAction()
    {
        var world = Kernel.Create();
        world.AuthorityMode = "bounded";

        var result = CommandPolicy.Apply(world, Command(world, new ServiceAction("hold-train", "blue-1", Amount: 60)), agent: true);

        Assert.True(result.Accepted, result.Message);
        Assert.NotNull(world.LastAgentActionAt);
    }

    [Fact]
    public void MissingCredentialIsNotAnEmptySharedSecret()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["AGENT_KEY"] = "agent-only" }).Build();
        var context = new DefaultHttpContext();
        Assert.False(ServiceKey.Matches(context, configuration, "SERVICE_KEY"));
        context.Request.Headers["X-Service-Key"] = "agent-only";
        Assert.False(ServiceKey.Matches(context, configuration, "SERVICE_KEY"));
        context.Request.Headers["X-Agent-Key"] = "agent-only";
        Assert.True(ServiceKey.Matches(context, configuration, "AGENT_KEY", "X-Agent-Key"));
    }

    [Fact]
    public async Task RandomServiceAlertRequiresOperatorKeyAndPreservesGenerationFence()
    {
        CommandEnvelope? forwarded = null;
        var forwards = 0;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["OPERATOR_KEY"] = "operator-test-key";
        builder.Configuration["SERVICE_KEY"] = "service-test-key";
        builder.Services.AddHttpClient("domain").ConfigurePrimaryHttpMessageHandler(() => new Handler(async (request, token) =>
        {
            forwards++;
            Assert.Equal("incidents", request.RequestUri!.Host);
            Assert.Equal("/actions", request.RequestUri.AbsolutePath);
            Assert.Equal("service-test-key", Assert.Single(request.Headers.GetValues("X-Service-Key")));
            forwarded = await request.Content!.ReadFromJsonAsync<CommandEnvelope>(Wire.Json, token);
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new CommandResult(true, "accepted",
                    "Elevator outage at Central Exchange; estimated restoration in about 4 hours.", 18, "incident-1"))
            };
        }));
        builder.Services.AddSingleton<ServiceClients>();
        await using var app = builder.Build();
        OperationsRoutes.Map(app);
        await app.StartAsync();
        using var client = app.GetTestClient();
        var request = new ActionRequest(new("random-incident"), "scenario-1", 7, 17, "dashboard-random-1",
            "Aspire dashboard random service alert");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/actions", request)).StatusCode);
        Assert.Equal(0, forwards);
        client.DefaultRequestHeaders.Add("X-Operator-Key", "operator-test-key");
        using var response = await client.PostAsJsonAsync("/api/actions", request);
        var result = await response.Content.ReadFromJsonAsync<CommandResult>(Wire.Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, forwards);
        Assert.NotNull(forwarded);
        Assert.Equal("scenario-1", forwarded.ScenarioId);
        Assert.Equal(7, forwarded.Generation);
        Assert.Equal(17, forwarded.ExpectedVersion);
        Assert.Equal("dashboard-random-1", forwarded.CommandId);
        Assert.Equal("random-incident", forwarded.Action.Kind);
        Assert.Equal("Elevator outage at Central Exchange; estimated restoration in about 4 hours.", result!.Message);
        Assert.Equal("incident-1", result.EntityId);
    }

    private static CommandEnvelope Command(WorldState world, ServiceAction action) =>
        new(Guid.NewGuid().ToString("N"), world.ScenarioId, world.Generation, world.Version, "agent", "Test authorization boundary", action);

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
