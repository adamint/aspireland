using System.Text.Json;
using MetroOps.Contracts;
using MetroOps.AiHost;

namespace MetroOps.Runtime.Tests;

public sealed class AiToolTests
{
    [Theory]
    [InlineData("centtral exchange", "central-exchange")]
    [InlineData("airprot", "airport")]
    [InlineData("city hall", "civic-square")]
    public async Task PassengerResolvesUniqueStationTyposAndExistingAliases(string name, string expected)
    {
        var passenger = new PassengerToolRegistry(new ProposalStore());
        var capture = new AssistantCapture(null);
        var arguments = JsonSerializer.Serialize(new { name });
        var result = await passenger.ExecuteAsync("resolve_station", arguments, capture, CancellationToken.None);
        using var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.GetProperty("resolved").GetBoolean());
        Assert.Equal(expected, json.RootElement.GetProperty("station").GetProperty("id").GetString());
        Assert.Null(capture.Clarification);
    }

    [Fact]
    public async Task OperatorDoesNotResolveApproximateStationNames()
    {
        var capture = new AssistantCapture(null);
        var result = await new OperatorToolRegistry(new ProposalStore()).ExecuteAsync(
            "resolve_station", """{"name":"centtral exchange"}""", capture, CancellationToken.None);
        using var json = JsonDocument.Parse(result);

        Assert.False(json.RootElement.GetProperty("resolved").GetBoolean());
        Assert.NotNull(capture.Clarification);
        Assert.Null(Network.Resolve("centtral exchange"));
    }

    [Fact]
    public async Task UnknownStationReturnsOnlyRealClarificationChoices()
    {
        var tools = new PassengerToolRegistry(new ProposalStore());
        var capture = new AssistantCapture(null);

        await tools.ExecuteAsync("resolve_station", """{"name":"gate"}""", capture, CancellationToken.None);
        var response = capture.BuildResponse("Invented Metro Airport Terminal 1 and Terminal 2");

        Assert.Contains("Northgate", response.Text);
        Assert.Contains("Eastgate", response.Text);
        Assert.DoesNotContain("Terminal", response.Text);
        Assert.Null(response.Arrivals);
    }

    [Fact]
    public async Task UnknownAirportTerminalDoesNotBecomeARealStation()
    {
        var capture = new AssistantCapture(null);
        await new PassengerToolRegistry(new ProposalStore()).ExecuteAsync(
            "resolve_station", """{"name":"Airport Terminal 2"}""", capture, CancellationToken.None);

        var response = capture.BuildResponse("Go to Metro Airport Terminal 2.");

        Assert.Contains("couldn't match", response.Text);
        Assert.DoesNotContain("Metro Airport Terminal", response.Text);
        Assert.Null(response.Journey);
    }

    [Fact]
    public void ArrivalPayloadAndNarrationStayAuthoritative()
    {
        var now = new DateTimeOffset(2026, 11, 17, 11, 34, 24, TimeSpan.Zero);
        var board = new StationBoard("airport", "scenario", 3, now, now, false,
            [new("ax-1", "AX", "express", "airport:AX:+", "union-terminal", 300, "live", "approaching", 10, 150)], []);
        var capture = new AssistantCapture(null) { Arrivals = board };

        var response = capture.BuildResponse("**Terminal 2:** - Fictional train arriving now");

        Assert.Same(board, response.Arrivals);
        Assert.Contains("Airport", response.Text);
        Assert.Contains("Union Terminal", response.Text);
        Assert.Contains("5 minutes", response.Text);
        Assert.DoesNotContain("simulation", response.Text);
        Assert.DoesNotContain("Terminal 2", response.Text);
        Assert.DoesNotContain("**", response.Text);
        capture.Arrivals = board with { Stale = true };
        var stale = capture.BuildResponse("A train is arriving now.");
        Assert.Contains("out of date", stale.Text);
        Assert.DoesNotContain("5 minutes", stale.Text);
    }

    [Fact]
    public void AssistantContextUsesValidatedViewerLocalSimulationTime()
    {
        var zone = AiEndpoints.ResolveViewerTimeZone("America/New_York");
        var world = new ScenarioContext("private-scenario-marker", 1, 2,
            new DateTimeOffset(2026, 11, 17, 11, 34, 24, TimeSpan.Zero), DateTimeOffset.UtcNow, "advise");

        var prompt = AiEndpoints.BuildSystemPrompt("passenger", world, zone);

        Assert.Contains("06:34:24 -05:00", prompt);
        Assert.Contains("America/New_York", prompt);
        Assert.Contains("never invent station", prompt);
        Assert.DoesNotContain("private-scenario-marker", prompt);
        Assert.DoesNotContain("generation 1", prompt);
        Assert.DoesNotContain("version 2", prompt);
        Assert.Contains("policy notes or checklists", prompt);
        var dispatch = AiEndpoints.BuildSystemPrompt("operator", world, zone);
        Assert.Contains("private-scenario-marker, generation 1, version 2", dispatch);
    }

    [Fact]
    public void SignalPersonaAppliesOnlyToTheReadOnlyPassengerPrompt()
    {
        var passenger = AiEndpoints.BuildSystemPrompt("passenger", null, TimeZoneInfo.Utc);
        var dispatch = AiEndpoints.BuildSystemPrompt("operator", null, TimeZoneInfo.Utc);

        Assert.Contains("Signal", passenger);
        Assert.Contains("read-only", passenger);
        Assert.Contains("rider's language", passenger);
        Assert.Contains("Do not imply live status without fresh tool evidence", passenger);
        Assert.Contains("You cannot dispatch trains", passenger);
        Assert.Contains("answer itself", passenger);
        Assert.Contains("skip a routine introduction", passenger);
        Assert.DoesNotContain("journey cards are rendered separately", passenger);
        Assert.DoesNotContain("explain that Aspireland data is simulated", passenger);
        Assert.DoesNotContain("Signal", dispatch);
        Assert.Contains("Arrival boards and journey cards are rendered separately", dispatch);
        Assert.Contains("explain that Aspireland data is simulated", dispatch);
        Assert.Contains("Approval mode requires the separate human approval API", dispatch);
    }

    [Fact]
    public void JourneyNarrationUsesActualLocalArrivalInsteadOfModelTimetable()
    {
        var departure = new DateTimeOffset(2026, 11, 17, 11, 34, 24, TimeSpan.Zero);
        var capture = new AssistantCapture(null, AiEndpoints.ResolveViewerTimeZone("America/New_York"))
        {
            Journey = new("scenario", 1, 2, departure, new("university", "airport", true),
                [new()
                {
                    TotalSeconds = 1800, ArrivesAt = departure.AddMinutes(30), Accessible = true,
                    Legs = [new() { Line = "AX", From = "union-terminal", To = "airport", Destination = "airport", WaitSeconds = 120 }],
                    Warnings = ["Allow extra time for the lift."]
                }])
        };

        var response = capture.BuildResponse("Take a fictional train at 11:34 AM.");

        Assert.Contains("step-free trip", response.Text);
        Assert.Contains("30 minutes", response.Text);
        Assert.Contains("7:04 AM", response.Text);
        Assert.DoesNotContain("America/New_York", response.Text);
        Assert.Contains("Airport Express", response.Text);
        Assert.Contains("Union Terminal", response.Text);
        Assert.Contains("wait about 2 minutes", response.Text);
        Assert.Contains("Allow extra time for the lift.", response.Text);
        Assert.DoesNotContain("card", response.Text);
        Assert.DoesNotContain("simulated", response.Text);
        Assert.DoesNotContain("fictional", response.Text);
        Assert.Same(capture.Journey, response.Journey);
    }

    [Theory]
    [InlineData("not/a-zone")]
    [InlineData("Eastern Standard Time")]
    [InlineData("")]
    public void InvalidOrNonIanaViewerZoneIsRejected(string zone) =>
        Assert.Throws<AiEndpoints.RequestValidationException>(() => AiEndpoints.ResolveViewerTimeZone(zone));

    [Fact]
    public void ExplicitTwentyFourHourRoundTripTimesUseTheViewerServiceDayAndOffset()
    {
        var timing = AiEndpoints.ResolvePassengerTripTiming(
            new AssistantRequest("Round trip from airport to medical center, ready at 10:30 local and returning around 15:00."),
            new DateTimeOffset(2026, 11, 18, 14, 0, 0, TimeSpan.Zero),
            AiEndpoints.ResolveViewerTimeZone(null));

        Assert.NotNull(timing);
        Assert.Equal(DateTimeOffset.Parse("2026-11-19T10:30:00+00:00"), timing.OutboundTime);
        Assert.Equal(DateTimeOffset.Parse("2026-11-19T15:00:00+00:00"), timing.ReturnTime);
    }

    [Fact]
    public void OvernightReturnUsesTheNextViewerLocalDay()
    {
        var timing = AiEndpoints.ResolvePassengerTripTiming(
            new AssistantRequest("Round trip from airport to medical center, leaving at 11:30 p.m. and returning at 12:15 a.m."),
            new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero),
            AiEndpoints.ResolveViewerTimeZone("America/New_York"));

        Assert.NotNull(timing);
        Assert.Equal(DateTimeOffset.Parse("2026-07-18T23:30:00-04:00"), timing.OutboundTime);
        Assert.Equal(DateTimeOffset.Parse("2026-07-19T00:15:00-04:00"), timing.ReturnTime);
    }

    [Fact]
    public void ExplicitRiderDateOverridesTheApplicableServiceDay()
    {
        var timing = AiEndpoints.ResolvePassengerTripTiming(
            new AssistantRequest("Round trip from airport to medical center on November 20, 2026 at 10:30 a.m., returning at 3 p.m."),
            new DateTimeOffset(2026, 11, 18, 8, 0, 0, TimeSpan.Zero),
            AiEndpoints.ResolveViewerTimeZone("Europe/Lisbon"));

        Assert.NotNull(timing);
        Assert.Equal(DateTimeOffset.Parse("2026-11-20T10:30:00+00:00"), timing.OutboundTime);
        Assert.Equal(DateTimeOffset.Parse("2026-11-20T15:00:00+00:00"), timing.ReturnTime);
    }

    [Fact]
    public void SeparatelyDatedRoundTripUsesBothExplicitDates()
    {
        var timing = AiEndpoints.ResolvePassengerTripTiming(
            new AssistantRequest("Round trip from airport to medical center on November 20, 2026 at 10:30 a.m., returning on November 21, 2026 at 3 p.m."),
            new DateTimeOffset(2026, 11, 18, 8, 0, 0, TimeSpan.Zero),
            AiEndpoints.ResolveViewerTimeZone("Europe/Lisbon"));

        Assert.NotNull(timing);
        Assert.Equal(DateTimeOffset.Parse("2026-11-20T10:30:00+00:00"), timing.OutboundTime);
        Assert.Equal(DateTimeOffset.Parse("2026-11-21T15:00:00+00:00"), timing.ReturnTime);
    }

    [Fact]
    public void ReturnOnlyDateDoesNotMoveTheOutboundToThatDate()
    {
        var timing = AiEndpoints.ResolvePassengerTripTiming(
            new AssistantRequest("Round trip from airport to medical center at 10:30 a.m., returning on November 21 at 3 p.m."),
            new DateTimeOffset(2026, 11, 18, 8, 0, 0, TimeSpan.Zero),
            AiEndpoints.ResolveViewerTimeZone("Europe/Lisbon"));

        Assert.NotNull(timing);
        Assert.Equal(DateTimeOffset.Parse("2026-11-18T10:30:00Z"), timing.OutboundTime);
        Assert.Equal(DateTimeOffset.Parse("2026-11-21T15:00:00Z"), timing.ReturnTime);
    }

    [Fact]
    public void HoldAmountIsRetainedInStoredProposal()
    {
        using var arguments = JsonDocument.Parse(
            """{"kind":"hold-train","target":"blue-1","amount":60}""");

        var action = ServiceActionParser.Parse(arguments.RootElement);
        var proposal = new ProposalStore().Add(
            action,
            new ScenarioContext(
                "scenario-test",
                3,
                42,
                new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero),
                DateTimeOffset.UtcNow,
                "approval"),
            "Hold for review");

        Assert.Equal("hold-train", proposal.Action.Kind);
        Assert.Equal("blue-1", proposal.Action.Target);
        Assert.Equal(60, proposal.Action.Amount);
        Assert.Equal("scenario-test", proposal.ScenarioId);
        Assert.Equal(3, proposal.Generation);
        Assert.Equal(42, proposal.Version);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(181)]
    public void HoldAmountOutsideBoundIsRejected(double amount)
    {
        using var arguments = JsonDocument.Parse(
            $$"""{"kind":"hold-train","target":"blue-1","amount":{{amount}}}""");

        var exception = Assert.Throws<ToolInputException>(
            () => ServiceActionParser.Parse(arguments.RootElement));

        Assert.Contains("no more than 180 seconds", exception.Message);
    }

    [Fact]
    public void HeadwayAboveRuntimeAuthorityBoundIsRejected()
    {
        using var arguments = JsonDocument.Parse(
            """{"kind":"set-headway","target":"AX","amount":901}""");

        var exception = Assert.Throws<ToolInputException>(
            () => ServiceActionParser.Parse(arguments.RootElement));

        Assert.Contains("300 to 900 seconds", exception.Message);
    }
}
