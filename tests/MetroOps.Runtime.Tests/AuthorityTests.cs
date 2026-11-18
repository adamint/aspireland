using MetroOps.Contracts;
using MetroOps.Domain;
using MetroOps.ServiceDefaults;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

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

    private static CommandEnvelope Command(WorldState world, ServiceAction action) =>
        new(Guid.NewGuid().ToString("N"), world.ScenarioId, world.Generation, world.Version, "agent", "Test authorization boundary", action);
}
