using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using OpenAI;
using MetroOps.Contracts;
using MetroOps.AiHost;
using MetroOps.Dispatch;
using Microsoft.Extensions.Configuration;

namespace MetroOps.Runtime.Tests;

public sealed class AiTransportTests
{
    [Fact]
    public async Task SdkCompletionRunsTheSameBoundedApplicationTool()
    {
        var requests = new List<JsonElement>();
        using var http = new HttpClient(new Handler(async (request, _) =>
        {
            Assert.Equal("/openai/v1/chat/completions", request.RequestUri!.AbsolutePath);
            requests.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.Clone());
            return JsonResponse(requests.Count == 1 ? ToolResponse : TextResponse);
        }));
        var client = Client(http);

        var result = await client.CompleteAsync(new AssistantRequest("Resolve Airport"), "Use transit tools.",
            new PassengerToolRegistry(new ProposalStore()), CancellationToken.None);

        Assert.Equal("Done.", result.Text);
        Assert.Equal(["resolve_station"], result.Tools);
        Assert.Equal(2, requests.Count);
        Assert.False(requests[0].GetProperty("parallel_tool_calls").GetBoolean());
        Assert.False(requests[0].GetProperty("store").GetBoolean());
        Assert.Equal("tool", requests[1].GetProperty("messages")[3].GetProperty("role").GetString());
    }

    [Theory]
    [InlineData("fresh")]
    [InlineData("insufficient")]
    [InlineData("stale")]
    [InlineData("unavailable")]
    public async Task CentralExchangeBackupAnswerIsNotReplacedByAnArrivalsCard(string condition)
    {
        const string question = "why is central exchange backing up";
        const string stationToolCall = """
            {"id":"test","object":"chat.completion","created":1,"model":"gpt41","choices":[{"index":0,"finish_reason":"tool_calls","message":{"role":"assistant","content":null,"tool_calls":[{"id":"diagnosis","type":"function","function":{"name":"diagnose_station","arguments":"{\"station\":\"Central Exchange\"}"}}]}}]}
            """;
        var arrivalsCall = stationToolCall.Replace("diagnose_station", "get_arrivals").Replace("\"diagnosis\"", "\"arrivals\"");
        var modelResponses = new Queue<string>([stationToolCall, arrivalsCall, TextResponse]);
        using var modelHttp = new HttpClient(new Handler((_, _) => Task.FromResult(JsonResponse(modelResponses.Dequeue()))));
        var station = Network.Station("central-exchange");
        var now = new DateTimeOffset(2026, 11, 18, 12, 30, 0, TimeSpan.Zero);
        var localBlock = Network.Block("Blue", "civic-square", station.Id);
        var world = new WorldState
        {
            SimTime = now, UpdatedAt = condition == "stale" ? now.AddMinutes(-1) : now,
            Cohorts =
            [
                new() { StationId = station.Id, State = "platform", Count = 35, ReadyAt = now },
                new() { StationId = station.Id, State = "outside", Count = 7, ReadyAt = now },
                new() { StationId = station.Id, State = "transfer", Count = 3, ReadyAt = now },
                new() { StationId = station.Id, State = "outside", Count = 800, ReadyAt = now.AddMinutes(1) },
                new() { StationId = "airport", State = "platform", Count = 900, ReadyAt = now }
            ],
            Trains = condition == "insufficient" ? [] :
            [
                new() { Id = "blue-1", Line = "Blue", StationId = station.Id, Load = 150,
                    State = "held", HoldReason = "Operator hold." },
                new() { Id = "amber-2", Line = "Amber", StationId = "airport", Load = 999, State = "held" }
            ],
            Incidents =
            [
                new() { Kind = "platform-closure", Target = "airport" },
                new() { Kind = "segment-closure", Target = $"civic-square>{station.Id}", State = "cleared" }
            ],
            BlockOccupancy = new()
            {
                [localBlock] = "blue-2",
                [Network.Block("Amber", "airport", "aerocity")] = "amber-2"
            }
        };
        var evidence = StationDiagnostics.Observe(world, station, now);
        using var serviceHttp = new HttpClient(new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/arrivals", StringComparison.Ordinal))
                return Task.FromResult(JsonResponse(JsonSerializer.Serialize(new StationBoard("central-exchange", "test", 1,
                    now, now, false, [], []), Wire.Json)));

            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("dispatch", request.RequestUri.Host);
            Assert.Equal("/stations/central-exchange/diagnosis", request.RequestUri.AbsolutePath);
            Assert.Equal("test-agent-key", Assert.Single(request.Headers.GetValues("X-Agent-Key")));
            return Task.FromResult(condition == "unavailable"
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : JsonResponse(JsonSerializer.Serialize(evidence, Wire.Json)));
        }));
        var tools = new OperatorToolRegistry(new ProposalStore(), new ServiceJsonClient(serviceHttp), "test-agent-key");

        var prompt = AiEndpoints.BuildSystemPrompt("operator", null, TimeZoneInfo.Utc);
        var result = await Client(modelHttp).CompleteAsync(new AssistantRequest(question), prompt, tools, CancellationToken.None);

        Assert.Equal("central-exchange", result.Arrivals?.StationId);
        Assert.Contains("diagnose_station", prompt);
        Assert.DoesNotContain(new PassengerToolRegistry(new ProposalStore()).Definitions, tool => tool.Name == "diagnose_station");
        if (condition == "fresh")
        {
            Assert.StartsWith("Central Exchange: 42", result.Text);
            Assert.Contains("Operator hold", result.Text);
            Assert.Contains("possible contributors", result.Text);
            Assert.Equal(150, evidence.TrainLoad);
            Assert.Equal(3, evidence.Queue?.ReadyTransfers);
            Assert.Empty(evidence.Restrictions);
            Assert.Equal(localBlock, Assert.Single(evidence.ReservedBlocks).Block);
        }
        else if (condition == "insufficient")
        {
            Assert.Contains("42 waiting", result.Text);
            Assert.Contains("doesn't establish a cause", result.Text);
            Assert.DoesNotContain("possible contributors", result.Text);
        }
        else
        {
            Assert.Contains("can't determine", result.Text);
            Assert.DoesNotContain("42 waiting", result.Text);
            Assert.Contains(condition, result.Text);
        }
    }

    [Fact]
    public async Task RepeatedSdkToolCallsCannotEscapeTheIterationLimit()
    {
        var count = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            count++;
            return Task.FromResult(JsonResponse(ToolResponse));
        }));

        var error = await Assert.ThrowsAsync<OpenAiProviderException>(() =>
            Client(http).CompleteAsync(new AssistantRequest("Resolve Airport"), "Use transit tools.",
                new PassengerToolRegistry(new ProposalStore()), CancellationToken.None));

        Assert.Contains("tool-call limit", error.Message);
        Assert.Equal(7, count);
    }

    [Fact]
    public async Task CallerCancellationIsNotConvertedToProviderSuccessOrFailure()
    {
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new Handler(async (_, token) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Unreachable");
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Client(http).CompleteAsync(new AssistantRequest("Hello"), "Use transit tools.",
                new PassengerToolRegistry(new ProposalStore()), cancellation.Token));
    }

    [Fact]
    public void FoundryReferencesSupplyDeploymentNamesWithoutPublicFallback()
    {
        var settings = OpenAiSettings.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:chat"] = "Endpoint=https://example.cognitiveservices.azure.com/;EndpointAIInference=https://example.services.ai.azure.com/models;Deployment=gpt41",
                ["ConnectionStrings:speech"] = "Endpoint=https://example.cognitiveservices.azure.com/;Deployment=metroops-speech"
            }).Build());

        Assert.Equal("gpt41", settings.TextModel);
        Assert.True(settings.TextEnabled);
        Assert.True(settings.SpeechEnabled);
        Assert.False(settings.TranscriptionEnabled);
        Assert.False(OpenAiSettings.FromConfiguration(new ConfigurationBuilder().Build()).TextEnabled);
    }

    [Fact]
    public void PublicEndpointCannotBecomeAHiddenFallback()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:chat"] = "Endpoint=https://api.openai.com/;Deployment=gpt41" }).Build();

        Assert.Throws<InvalidOperationException>(() => OpenAiSettings.FromConfiguration(configuration));
    }

    private static OpenAiClient Client(HttpClient http) => new(
        new OpenAiSettings { ConnectionName = "chat", TextModel = "gpt41" },
        new OpenAIClient(new ApiKeyCredential("test"), new OpenAIClientOptions
        {
            Endpoint = new Uri("https://example.cognitiveservices.azure.com/openai/v1/"),
            Transport = new HttpClientPipelineTransport(http)
        }));

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private const string ToolResponse = """
        {"id":"test","object":"chat.completion","created":1,"model":"gpt41","choices":[{"index":0,"finish_reason":"tool_calls","message":{"role":"assistant","content":null,"tool_calls":[{"id":"call1","type":"function","function":{"name":"resolve_station","arguments":"{\"name\":\"Airport\"}"}}]}}]}
        """;
    private const string TextResponse = """
        {"id":"test","object":"chat.completion","created":1,"model":"gpt41","choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"Done."}}]}
        """;
}
