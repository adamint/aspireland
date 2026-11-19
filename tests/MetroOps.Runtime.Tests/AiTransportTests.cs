using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
            Assert.Equal("/openai/v1/responses", request.RequestUri!.AbsolutePath);
            requests.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.Clone());
            return JsonResponse(requests.Count == 1 ? ToolResponse : TextResponse);
        }));
        var client = Client(http);

        var result = await client.CompleteAsync(new AssistantRequest("Resolve Airport"), "Use transit tools.",
            new PassengerToolRegistry(new ProposalStore()), CancellationToken.None);

        Assert.Equal("Done.", result.Text);
        Assert.Equal(["resolve_station"], result.Tools);
        Assert.Equal(2, requests.Count);
        Assert.All(requests, request =>
        {
            Assert.False(request.GetProperty("parallel_tool_calls").GetBoolean());
            Assert.False(request.GetProperty("store").GetBoolean());
            Assert.True(request.TryGetProperty("reasoning", out var reasoning), "Use bounded reasoning for GPT-5 mini.");
            Assert.Equal("minimal", reasoning.GetProperty("effort").GetString());
            Assert.Contains(request.GetProperty("include").EnumerateArray(),
                value => value.GetString() == "reasoning.encrypted_content");
            Assert.False(request.TryGetProperty("previous_response_id", out _));
        });
        var input = requests[1].GetProperty("input").EnumerateArray().ToArray();
        using var firstResponse = JsonDocument.Parse(ToolResponse);
        foreach (var output in firstResponse.RootElement.GetProperty("output").EnumerateArray())
            Assert.Contains(input, item => JsonElement.DeepEquals(item, output));
        var toolOutput = Assert.Single(input, item => item.GetProperty("type").GetString() == "function_call_output");
        Assert.Equal("call1", toolOutput.GetProperty("call_id").GetString());
        Assert.Contains("airport", toolOutput.GetProperty("output").GetString());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task SignalPlansBothRoundTripDirectionsOrAsksForReturnTime(bool returnTimeKnown, bool hasPreviousTrip)
    {
        const string question = "can u helpme plan round trip journey from airport to centtral exchange";
        const string departure = "2026-11-18T16:30:00+00:00";
        static string PlanCall(string id, object args) => JsonSerializer.Serialize(new
        {
            id = $"resp-{id}", @object = "response", created_at = 1, model = "gpt-5-mini", status = "completed",
            output = new[] { new { id = $"fc-{id}", type = "function_call", status = "completed",
                call_id = id, name = "plan_journey", arguments = JsonSerializer.Serialize(args) } }
        });
        var modelResponses = new Queue<string>();
        modelResponses.Enqueue(PlanCall("outbound", new { origin = "airport", destination = "centtral exchange",
            askReturnTime = !returnTimeKnown }));
        if (returnTimeKnown)
            modelResponses.Enqueue(PlanCall("return", new { origin = "centtral exchange", destination = "airport",
                departureTime = departure }));
        modelResponses.Enqueue(TextResponse.Replace("Done.", "Take a fictional line."));
        using var modelHttp = new HttpClient(new Handler(async (request, _) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            if (modelResponses.Peek().Contains("resp-outbound"))
            {
                Assert.Contains(question, json.RootElement.GetProperty("input").GetRawText());
                var planner = Assert.Single(json.RootElement.GetProperty("tools").EnumerateArray(),
                    tool => tool.GetProperty("name").GetString() == "plan_journey");
                Assert.Equal("boolean", planner.GetProperty("parameters").GetProperty("properties")
                    .GetProperty("askReturnTime").GetProperty("type").GetString());
                Assert.Equal("string", planner.GetProperty("parameters").GetProperty("properties")
                    .GetProperty("returnTime").GetProperty("type").GetString());
                var instructions = json.RootElement.GetProperty("instructions").GetString();
                Assert.Contains("Call plan_journey first with those station names as written", instructions);
                Assert.Contains("Only ask about supplied names if plan_journey reports ambiguity or no match", instructions);
                Assert.Contains("omit departureTime to leave now", instructions);
                Assert.Contains("Step-free travel is optional unless requested", instructions);
                Assert.Contains("call it once for the outbound with askReturnTime set to true", instructions);
                Assert.Contains("call plan_journey once with departureTime and returnTime", instructions);
                Assert.Contains("do not recompute the outbound", instructions);
                Assert.Contains("Do not invent airport or security buffers", instructions);
                Assert.Contains("Never guess a return time", instructions);
            }
            return JsonResponse(modelResponses.Dequeue());
        }));

        var requests = new List<JourneyRequest>();
        var now = new DateTimeOffset(2026, 11, 18, 12, 30, 0, TimeSpan.Zero);
        using var serviceHttp = new HttpClient(new Handler(async (request, _) =>
        {
            Assert.Equal("/journeys", request.RequestUri!.AbsolutePath);
            var trip = JsonSerializer.Deserialize<JourneyRequest>(await request.Content!.ReadAsStringAsync(), Wire.Json)!;
            requests.Add(trip);
            var start = trip.DepartureTime ?? now;
            var duration = trip.Origin == "airport" ? 1200 : 1320;
            var journey = new JourneyResult("test-scenario", 1, 1, now, trip,
                [new() { TotalSeconds = duration, ArrivesAt = start.AddSeconds(duration),
                    Legs = [new() { Line = "AX", From = trip.Origin, To = trip.Destination, Destination = trip.Destination }] }]);
            return JsonResponse(JsonSerializer.Serialize(journey, Wire.Json));
        }));

        var prompt = AiEndpoints.BuildSystemPrompt("passenger", null, TimeZoneInfo.Utc);
        var tools = new PassengerToolRegistry(new ProposalStore(), new ServiceJsonClient(serviceHttp));
        var result = await Client(modelHttp).CompleteAsync(
            new AssistantRequest(question, History: returnTimeKnown ? [new("user", $"I'd like to return at {departure}.")] : null,
                JourneyContext: hasPreviousTrip
                    ? new("university", "southbank", Accessible: true, DepartureTime: DateTimeOffset.Parse(departure))
                    : null),
            prompt, tools, CancellationToken.None);

        Assert.NotEmpty(requests);
        Assert.Equal(new JourneyRequest("airport", "central-exchange"), requests[0]);
        Assert.StartsWith("**Outbound**", result.Text);
        Assert.Contains("Approx. arrival at Central Exchange", result.Text);
        Assert.Contains("20 minutes", result.Text);
        Assert.DoesNotContain("fictional", result.Text);
        if (returnTimeKnown)
        {
            Assert.Equal(new JourneyRequest("central-exchange", "airport", DepartureTime: DateTimeOffset.Parse(departure)), requests[1]);
            Assert.Equal(2, requests.Count);
            Assert.Contains("**Return**", result.Text);
            Assert.Contains("Approx. arrival at Airport", result.Text);
            Assert.Contains("22 minutes", result.Text);
            Assert.DoesNotContain("What time", result.Text);
        }
        else
        {
            Assert.Single(requests);
            Assert.Contains("What time would you like to head back?", result.Text);
            Assert.DoesNotContain("The route from Central Exchange to Airport", result.Text);
        }
    }

    [Theory]
    [InlineData("Europe/Lisbon", "2026-11-18T10:30:00+00:00", "2026-11-18T15:00:00+00:00")]
    [InlineData("America/New_York", "2026-11-18T10:30:00-05:00", "2026-11-18T15:00:00-05:00")]
    [InlineData("America/New_York", "2026-07-18T10:30:00-04:00", "2026-07-18T15:00:00-04:00")]
    public async Task SignalHonorsBothExplicitLocalRoundTripTimesFromTheExactRiderTurn(
        string viewerTimeZone,
        string expectedOutbound,
        string expectedReturn)
    {
        const string question = "Can you plan a round trip from airport to medical center for me, please? I'm going to fly in and arrive at 10:30 a.m. local time, and I will be leaving the medical center around 3 p.m.";
        var toolCall = JsonSerializer.Serialize(new
        {
            id = "resp-round-trip", @object = "response", created_at = 1, model = "gpt-5-mini", status = "completed",
            output = new[] { new { id = "fc-round-trip", type = "function_call", status = "completed",
                call_id = "round-trip", name = "plan_journey",
                arguments = """{"origin":"airport","destination":"medical center","askReturnTime":true}""" } }
        });
        var modelResponses = new Queue<string>([toolCall, TextResponse]);
        using var modelHttp = new HttpClient(new Handler((_, _) =>
            Task.FromResult(JsonResponse(modelResponses.Dequeue()))));
        var requests = new List<JourneyRequest>();
        var serviceDate = DateTimeOffset.Parse(expectedOutbound).Date;
        using var serviceHttp = new HttpClient(new Handler(async (request, _) =>
        {
            var trip = JsonSerializer.Deserialize<JourneyRequest>(await request.Content!.ReadAsStringAsync(), Wire.Json)!;
            requests.Add(trip);
            var start = trip.DepartureTime ?? new DateTimeOffset(serviceDate, TimeSpan.Zero);
            return JsonResponse(JsonSerializer.Serialize(
                new JourneyResult("test", 1, 1, start, trip,
                    [new() { TotalSeconds = 1800, ArrivesAt = start.AddMinutes(30),
                        Legs = [new() { Line = trip.Origin == "airport" ? "AX" : "Green",
                            From = trip.Origin, To = trip.Destination, Destination = trip.Destination }] }]),
                Wire.Json));
        }));
        var localDate = serviceDate == new DateTime(2026, 7, 18)
            ? new DateTimeOffset(2026, 7, 18, 8, 0, 0, TimeSpan.Zero)
            : new DateTimeOffset(2026, 11, 18, 8, 0, 0, TimeSpan.Zero);

        var result = await Client(modelHttp).CompleteAsync(
            new AssistantRequest(question, ViewerTimeZone: viewerTimeZone),
            AiEndpoints.BuildSystemPrompt("passenger", null, AiEndpoints.ResolveViewerTimeZone(viewerTimeZone)),
            new PassengerToolRegistry(new ProposalStore(), new ServiceJsonClient(serviceHttp)),
            CancellationToken.None,
            localDate);

        Assert.Equal(2, requests.Count);
        Assert.Equal(DateTimeOffset.Parse(expectedOutbound), requests[0].DepartureTime);
        Assert.Equal(new JourneyRequest("airport", "medical-center", DepartureTime: DateTimeOffset.Parse(expectedOutbound)), requests[0]);
        Assert.Equal(new JourneyRequest("medical-center", "airport", DepartureTime: DateTimeOffset.Parse(expectedReturn)), requests[1]);
        Assert.Contains("Outbound", result.Text);
        Assert.Contains("Return", result.Text);
        Assert.DoesNotContain("What time", result.Text);
        Assert.DoesNotContain("simulat", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("medical-center", result.Text);
    }

    [Fact]
    public async Task SignalReusesSavedOutboundWhenAGenuinelyMissingReturnGetsATimeOnlyReply()
    {
        const string firstQuestion = "Plan a round trip from airport to medical center. I arrive at 10:30 a.m. local time.";
        static string ToolCall(string id, string arguments) => JsonSerializer.Serialize(new
        {
            id = $"resp-{id}", @object = "response", created_at = 1, model = "gpt-5-mini", status = "completed",
            output = new[] { new { id = $"fc-{id}", type = "function_call", status = "completed",
                call_id = id, name = "plan_journey", arguments } }
        });
        var modelResponses = new Queue<string>([
            ToolCall("outbound", """{"origin":"airport","destination":"medical center","askReturnTime":true}"""),
            TextResponse,
            ToolCall("return", """{}"""),
            TextResponse,
        ]);
        var modelRequests = 0;
        using var modelHttp = new HttpClient(new Handler((_, _) =>
        {
            modelRequests++;
            return Task.FromResult(JsonResponse(modelResponses.Dequeue()));
        }));
        var requests = new List<JourneyRequest>();
        using var serviceHttp = new HttpClient(new Handler(async (request, _) =>
        {
            var trip = JsonSerializer.Deserialize<JourneyRequest>(await request.Content!.ReadAsStringAsync(), Wire.Json)!;
            requests.Add(trip);
            var start = trip.DepartureTime ?? DateTimeOffset.Parse("2026-11-18T08:00:00+00:00");
            return JsonResponse(JsonSerializer.Serialize(
                new JourneyResult("test", 1, 1, start, trip,
                    [new() { TotalSeconds = 1800, ArrivesAt = start.AddMinutes(30),
                        Legs = [new() { Line = trip.Origin == "airport" ? "AX" : "Green",
                            From = trip.Origin, To = trip.Destination, Destination = trip.Destination }] }]),
                Wire.Json));
        }));
        var client = Client(modelHttp);
        var tools = new PassengerToolRegistry(new ProposalStore(), new ServiceJsonClient(serviceHttp));
        var zone = AiEndpoints.ResolveViewerTimeZone("Europe/Lisbon");
        var prompt = AiEndpoints.BuildSystemPrompt("passenger", null, zone);
        var serviceTime = DateTimeOffset.Parse("2026-11-18T08:00:00+00:00");

        var outbound = await client.CompleteAsync(
            new AssistantRequest(firstQuestion, ViewerTimeZone: zone.Id),
            prompt, tools, CancellationToken.None, serviceTime);
        var roundTrip = await client.CompleteAsync(
            new AssistantRequest("Around 3 p.m.", JourneyContext: outbound.JourneyContext, ViewerTimeZone: zone.Id),
            prompt, tools, CancellationToken.None, serviceTime);

        Assert.Equal(2, requests.Count);
        Assert.Equal(DateTimeOffset.Parse("2026-11-18T10:30:00+00:00"), requests[0].DepartureTime);
        Assert.Equal(new JourneyRequest("medical-center", "airport",
            DepartureTime: DateTimeOffset.Parse("2026-11-18T15:00:00+00:00")), requests[1]);
        Assert.Contains("What time would you like to head back?", outbound.Text);
        Assert.Contains("Outbound", roundTrip.Text);
        Assert.Contains("Return", roundTrip.Text);
        Assert.DoesNotContain("What time", roundTrip.Text);
        Assert.Equal(2, modelRequests);
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
            {"id":"resp-diagnosis","object":"response","created_at":1,"model":"gpt-5-mini","status":"completed","output":[{"id":"fc-diagnosis","type":"function_call","status":"completed","call_id":"diagnosis","name":"diagnose_station","arguments":"{\"station\":\"Central Exchange\"}"}]}
            """;
        var arrivalsCall = stationToolCall.Replace("diagnose_station", "get_arrivals").Replace("diagnosis", "arrivals");
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

    [Theory]
    [InlineData("incomplete")]
    [InlineData("failed")]
    public async Task UnfinishedResponsesCannotExecuteToolsOrBecomeSuccessfulAnswers(string status)
    {
        var count = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            count++;
            var response = JsonNode.Parse(ToolResponse)!;
            response["status"] = status;
            if (status == "incomplete")
                response["incomplete_details"] = JsonNode.Parse("""{"reason":"max_output_tokens"}""");
            return Task.FromResult(JsonResponse(response.ToJsonString()));
        }));

        var error = await Assert.ThrowsAsync<OpenAiProviderException>(() =>
            Client(http).CompleteAsync(new AssistantRequest("Resolve Airport"), "Use transit tools.",
                new PassengerToolRegistry(new ProposalStore()), CancellationToken.None));

        Assert.Contains("did not complete", error.Message);
        if (status == "incomplete") Assert.Contains("token", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task TranscriptionUsesTheDeploymentApiWhileSpeechKeepsV1()
    {
        var requests = 0;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.Equal("example.cognitiveservices.azure.com", request.RequestUri.Host);
            if (requests++ == 0)
            {
                Assert.Equal("/openai/deployments/transcription/audio/transcriptions", request.RequestUri.AbsolutePath);
                Assert.Equal("?api-version=2025-03-01-preview", request.RequestUri.Query);
                Assert.Equal("multipart/form-data", request.Content!.Headers.ContentType?.MediaType);
                Assert.Contains("recording.wav", await request.Content.ReadAsStringAsync(token));
                return JsonResponse("""{"text":" Airport to Museum Quarter. "}""");
            }
            Assert.Equal("/openai/v1/audio/speech", request.RequestUri.AbsolutePath);
            Assert.Equal("", request.RequestUri.Query);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([73, 68, 51]) };
        }));
        var client = Client(http, new OpenAiSettings
        {
            ConnectionName = "chat", TextModel = "chat", TranscriptionModel = "transcription", SpeechModel = "speech"
        });
        using var source = new MemoryStream([1, 2, 3]);

        var transcription = await client.TranscribeAsync(source, "audio/wav", CancellationToken.None);
        var speech = await client.SpeakAsync("Hello.", CancellationToken.None);

        Assert.Equal("Airport to Museum Quarter.", transcription.Text);
        Assert.Equal(new byte[] { 73, 68, 51 }, speech);
        Assert.Equal(2, requests);
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

    private static OpenAiClient Client(HttpClient http, OpenAiSettings? settings = null)
    {
        settings ??= new OpenAiSettings { ConnectionName = "chat", TextModel = "chat", TranscriptionModel = "transcription" };
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri("https://example.cognitiveservices.azure.com/openai/v1/"),
            Transport = new HttpClientPipelineTransport(http)
        };
        if (settings.TranscriptionEnabled)
            options.AddPolicy(new DeploymentTranscriptionPolicy(settings.TranscriptionModel), PipelinePosition.PerCall);
        return new OpenAiClient(settings, new OpenAIClient(new ApiKeyCredential("test"), options));
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private const string ToolResponse = """
        {"id":"resp-tool","object":"response","created_at":1,"model":"gpt-5-mini","status":"completed","output":[{"id":"rs-1","type":"reasoning","summary":[],"encrypted_content":"opaque-test-reasoning"},{"id":"fc-1","type":"function_call","status":"completed","call_id":"call1","name":"resolve_station","arguments":"{\"name\":\"Airport\"}"}]}
        """;
    private const string TextResponse = """
        {"id":"resp-text","object":"response","created_at":1,"model":"gpt-5-mini","status":"completed","output":[{"id":"msg-1","type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":"Done.","annotations":[]}]}]}
        """;
}
