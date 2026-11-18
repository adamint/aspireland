using System.Text.Json;
using MetroOps.Contracts;
using MetroOps.AiHost;

namespace MetroOps.Runtime.Tests;

public sealed class AiToolTests
{
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
        Assert.DoesNotContain("Terminal 2", response.Text);
        Assert.DoesNotContain("**", response.Text);
    }

    [Fact]
    public void AssistantContextUsesValidatedViewerLocalSimulationTime()
    {
        var zone = AiEndpoints.ResolveViewerTimeZone("America/New_York");
        var world = new ScenarioContext("scenario", 1, 2,
            new DateTimeOffset(2026, 11, 17, 11, 34, 24, TimeSpan.Zero), DateTimeOffset.UtcNow, "advise");

        var prompt = AiEndpoints.BuildSystemPrompt("passenger", world, zone);

        Assert.Contains("06:34:24 -05:00", prompt);
        Assert.Contains("America/New_York", prompt);
        Assert.Contains("never invent station", prompt);
    }

    [Fact]
    public void JourneyNarrationUsesActualLocalArrivalInsteadOfModelTimetable()
    {
        var departure = new DateTimeOffset(2026, 11, 17, 11, 34, 24, TimeSpan.Zero);
        var capture = new AssistantCapture(null, AiEndpoints.ResolveViewerTimeZone("America/New_York"))
        {
            Journey = new("scenario", 1, 2, departure, new("university", "airport", true),
                [new() { TotalSeconds = 1800, ArrivesAt = departure.AddMinutes(30), Accessible = true }])
        };

        var response = capture.BuildResponse("Take a fictional train at 11:34 AM.");

        Assert.Contains("step-free route", response.Text);
        Assert.Contains("30 minutes", response.Text);
        Assert.Contains("7:04 AM", response.Text);
        Assert.Contains("America/New_York", response.Text);
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
