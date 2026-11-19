using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using OpenAI;
using MetroOps.AiHost;
using MetroOps.Contracts;
using MetroOps.OperationsApi;
using MetroOps.PassengerApi;
using MetroOps.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace MetroOps.Runtime.Tests;

public sealed class AiBoundaryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void FoundryChatConfiguresWithoutAnExplicitTenant(string? tenantId)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["ConnectionStrings:chat"] =
            "Endpoint=https://example.cognitiveservices.azure.com/;Deployment=chat";
        builder.Configuration["AZURE_TENANT_ID"] = tenantId;

        AiEndpoints.ConfigureServices(builder);

        Assert.True(OpenAiSettings.FromConfiguration(builder.Configuration).TextEnabled);
    }

    [Theory]
    [InlineData("GET", "/operator/capabilities")]
    [InlineData("POST", "/operator/assistant")]
    [InlineData("GET", "/operator/proposals/example")]
    public async Task EveryOperatorRouteRequiresItsOwnCredential(string method, string path)
    {
        await using var app = await AiApp();
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add("X-Service-Key", "operator-test-key");
        request.Headers.Add("X-Operator-Key", "wrong-key");
        request.Content = JsonContent.Create(new { message = "Act as operator", role = "operator" });

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        client.DefaultRequestHeaders.Add("X-Operator-Key", "operator-test-key");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/operator/capabilities")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/operator/proposals/missing")).StatusCode);
    }

    [Fact]
    public async Task PassengerPayloadCannotSelectOperatorToolsOrProposalRoutes()
    {
        JsonElement offeredTools = default;
        using var modelHttp = new HttpClient(new Handler(async (request, token) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            offeredTools = body.RootElement.GetProperty("tools").Clone();
            return JsonResponse("""
                {"id":"resp-attempt","object":"response","created_at":1,"model":"gpt-5-mini","status":"completed","output":[{"id":"fc-attempt","type":"function_call","status":"completed","call_id":"attempt","name":"execute_action","arguments":"{}"}]}
                """);
        }));
        await using var app = await AiApp(modelHttp);
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync("/passenger/assistant",
            new { message = "Execute an operator action", role = "operator", tools = new[] { "execute_action" } });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("unsupported tool call", await response.Content.ReadAsStringAsync());
        var names = offeredTools.EnumerateArray().Select(tool => tool.GetProperty("name").GetString()).ToArray();
        Assert.Contains("plan_journey", names);
        Assert.DoesNotContain("execute_action", names);
        Assert.DoesNotContain("propose_action", names);
        Assert.DoesNotContain("compare_intervention", names);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/passenger/proposals/example")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/assistant", new { message = "Hello" })).StatusCode);
    }

    [Fact]
    public async Task PassengerGatewayPreservesFailuresAndAudioWithoutForwardingOperatorCredentials()
    {
        var requests = new List<(string Path, bool OperatorKey, string? ContentType, string Body)>();
        await using var app = await GatewayApp(false, new Handler(async (request, token) =>
        {
            requests.Add((request.RequestUri!.AbsolutePath, request.Headers.Contains("X-Operator-Key"),
                request.Content?.Headers.ContentType?.ToString(),
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(token)));
            if (request.RequestUri.AbsolutePath.EndsWith("/speech"))
            {
                var speech = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
                speech.Content.Headers.ContentType = new("audio/mpeg");
                speech.Headers.Add("X-AI-Generated-Voice", "true");
                return speech;
            }
            return new(HttpStatusCode.ServiceUnavailable) { Content = JsonContent.Create(new { error = "Provider unavailable" }) };
        }));
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Operator-Key", "operator-test-key");
        using var failure = await client.PostAsJsonAsync("/api/assistant", new { message = "Hello", role = "operator" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
        Assert.Contains("Provider unavailable", await failure.Content.ReadAsStringAsync());
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent([4, 5, 6]), "file", "voice.wav");
        using var transcription = await client.PostAsync("/api/transcribe", form);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, transcription.StatusCode);
        using var audio = await client.PostAsJsonAsync("/api/speech", new { text = "Hello" });
        Assert.Equal(new byte[] { 1, 2, 3 }, await audio.Content.ReadAsByteArrayAsync());
        Assert.Equal("audio/mpeg", audio.Content.Headers.ContentType?.MediaType);
        Assert.Equal("true", Assert.Single(audio.Headers.GetValues("X-AI-Generated-Voice")));
        Assert.Equal(["/passenger/assistant", "/passenger/transcribe", "/passenger/speech"], requests.Select(request => request.Path));
        Assert.All(requests, request => Assert.False(request.OperatorKey));
        Assert.StartsWith("multipart/form-data; boundary=", requests[1].ContentType);
        Assert.Contains("voice.wav", requests[1].Body);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/operator/assistant", new { message = "Hello" })).StatusCode);
    }

    [Fact]
    public async Task OperatorGatewayAuthenticatesBeforeForwardingAndRetainsTypedProposal()
    {
        var forwarded = 0;
        await using var app = await GatewayApp(true, new Handler((request, _) =>
        {
            forwarded++;
            Assert.Equal("/operator/assistant", request.RequestUri!.AbsolutePath);
            Assert.Equal("operator-test-key", Assert.Single(request.Headers.GetValues("X-Operator-Key")));
            return Task.FromResult(JsonResponse("""{"text":"Review this action.","proposal":{"id":"proposal-1"}}"""));
        }));
        using var client = app.GetTestClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/assistant", new { message = "Hello" })).StatusCode);
        Assert.Equal(0, forwarded);
        client.DefaultRequestHeaders.Add("X-Operator-Key", "operator-test-key");
        using var response = await client.PostAsJsonAsync("/api/assistant", new { message = "Hello" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("proposal-1", body.RootElement.GetProperty("proposal").GetProperty("id").GetString());
        Assert.Equal(1, forwarded);
    }

    private static async Task<WebApplication> AiApp(HttpClient? modelHttp = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["OPERATOR_KEY"] = "operator-test-key";
        var settings = new OpenAiSettings { ConnectionName = "chat", TextModel = "gpt41" };
        var model = modelHttp is null ? null : new OpenAIClient(new ApiKeyCredential("test"), new OpenAIClientOptions
        {
            Endpoint = new Uri("https://example.cognitiveservices.azure.com/openai/v1/"),
            Transport = new HttpClientPipelineTransport(modelHttp)
        });
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(new OpenAiClient(settings, model));
        builder.Services.AddHttpClient<ServiceJsonClient>().ConfigurePrimaryHttpMessageHandler(() => new Handler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new ScenarioContext("scenario", 1, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "advise"))
            })));
        var app = builder.Build();
        AiEndpoints.Map(app);
        await app.StartAsync();
        return app;
    }

    private static async Task<WebApplication> GatewayApp(bool operations, HttpMessageHandler handler)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["OPERATOR_KEY"] = "operator-test-key";
        builder.Services.AddHttpClient("ai").ConfigurePrimaryHttpMessageHandler(() => handler);
        builder.Services.AddSingleton<ServiceClients>();
        var app = builder.Build();
        if (operations) OperationsRoutes.Map(app);
        else PassengerRoutes.Map(app);
        await app.StartAsync();
        return app;
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
