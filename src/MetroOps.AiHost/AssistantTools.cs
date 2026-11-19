using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Collections.Concurrent;
using MetroOps.Contracts;

namespace MetroOps.AiHost;

internal sealed record AssistantToolDefinition(string Name, string Description, JsonElement Parameters);

internal sealed class AssistantCapture
{
    private readonly List<JourneyResult> _passengerJourneys = [];
    private readonly DateTimeOffset? _serviceTime;
    private bool _askReturnTime;
    public AiEndpoints.PassengerTripTiming? TripTiming { get; }

    public JourneyResult? Journey { get; set; }
    public ComparisonResult? Comparison { get; set; }
    public ActionProposal? Proposal { get; set; }
    public StationBoard? Arrivals { get; set; }
    public string? StationDiagnosisText { get; set; }
    public string? Clarification { get; set; }
    public AssistantJourneyContext? JourneyContext { get; set; }
    public bool? AccessiblePreference { get; }
    public bool ReuseTripContext { get; }
    public StepFreeResult? StationAccess { get; set; }
    public Dictionary<JourneyRequest, StepFreeResult> StepFreeChecks { get; } = [];
    public List<string> Tools { get; } = [];
    public TimeZoneInfo ViewerTimeZone { get; }
    public bool HasSavedOutbound => _passengerJourneys.Count > 0 && JourneyContext?.AwaitingReturnTime == true;

    public AssistantCapture(
        AssistantJourneyContext? journeyContext,
        TimeZoneInfo? viewerTimeZone = null,
        AiEndpoints.PassengerTripTiming? tripTiming = null,
        DateTimeOffset? serviceTime = null,
        bool? accessiblePreference = null,
        bool reuseTripContext = false)
    {
        JourneyContext = journeyContext;
        ViewerTimeZone = viewerTimeZone ?? TimeZoneInfo.Utc;
        TripTiming = tripTiming;
        _serviceTime = serviceTime;
        AccessiblePreference = accessiblePreference;
        ReuseTripContext = reuseTripContext;
        if (journeyContext is { AwaitingReturnTime: true, SavedOutboundId: { } savedId } &&
            PassengerJourneyStore.TryGet(savedId, out var saved) &&
            saved.Request.Origin == journeyContext.Origin &&
            saved.Request.Destination == journeyContext.Destination)
        {
            _passengerJourneys.Add(saved);
        }
    }

    public string LocalTime(DateTimeOffset time) =>
        TimeZoneInfo.ConvertTime(time, ViewerTimeZone).ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

    public void RecordPassengerJourney(JourneyResult journey, bool askReturnTime)
    {
        var index = _passengerJourneys.FindIndex(previous => previous.Request.Origin == journey.Request.Origin &&
            previous.Request.Destination == journey.Request.Destination);
        if (index >= 0) _passengerJourneys[index] = journey;
        else if (_passengerJourneys.Count < 2) _passengerJourneys.Add(journey);
        _askReturnTime |= askReturnTime;
    }

    public void SaveJourneyContext(JourneyResult journey, bool awaitingReturnTime)
    {
        var savedOutboundId = awaitingReturnTime ? PassengerJourneyStore.Add(journey) : null;
        JourneyContext = new(
            journey.Request.Origin,
            journey.Request.Destination,
            journey.Request.Accessible,
            journey.Request.DepartureTime,
            savedOutboundId,
            awaitingReturnTime);
    }

    public AssistantResponse BuildResponse(string text, bool passenger = true)
    {
        if (Clarification is not null)
            return new AssistantResponse(Clarification, Tools: Tools, JourneyContext: JourneyContext);
        if (passenger && StationAccess is { } stationAccess && Journey is null)
            return new AssistantResponse(DescribeStepFree(stationAccess), Tools: Tools, JourneyContext: JourneyContext);
        if (StationDiagnosisText is not null && Journey is null && Comparison is null && Proposal is null)
            text = StationDiagnosisText;
        else if (Arrivals is not null && Journey is null && Comparison is null && Proposal is null)
        {
            var station = Network.Station(Arrivals.StationId).Name;
            text = Arrivals.Stale
                ? $"The {station} arrival board is out of date; arrival times are unavailable."
                : passenger ? PassengerArrivals(Arrivals, station)
                : $"Here is the {station} arrival board. Times belong to the simulation.";
        }
        if (Journey is not null && Comparison is null && Proposal is null)
        {
            text = passenger && _passengerJourneys.Count > 0
                ? string.Join("\n\n", _passengerJourneys.Select((journey, index) =>
                    DescribeJourney(journey, passenger: true,
                        _passengerJourneys.Count > 1 || _askReturnTime ? index == 0 ? "Outbound" : "Return" : null,
                        index > 0 ? Departure(_passengerJourneys[0]) : null)))
                : DescribeJourney(Journey, passenger);
            if (passenger && _askReturnTime && _passengerJourneys.Count == 1)
                text += "\n\nWhat time would you like to head back?";
        }
        return new AssistantResponse(text, Journey, Comparison, Proposal, Tools.Count > 0 ? Tools : null, JourneyContext, Arrivals);
    }

    private static DateTimeOffset Departure(JourneyResult journey) => journey.Request.DepartureTime ?? journey.AsOf;

    private string DescribeJourney(JourneyResult journey, bool passenger, string? label = null,
        DateTimeOffset? outboundDeparture = null)
    {
        if (journey.Options.FirstOrDefault() is not { } itinerary)
        {
            if (passenger && journey.Request.Accessible)
            {
                var heading = label is null ? "" : $"**{label}**\n";
                return heading + (StepFreeChecks.TryGetValue(journey.Request, out var stepFree)
                    ? DescribeStepFree(stepFree)
                    : "I couldn't verify a step-free trip. Please check again before travelling.");
            }
            return journey.Error ?? "No journey is currently available between those stations.";
        }
        var localDeparture = TimeZoneInfo.ConvertTime(Departure(journey), ViewerTimeZone);
        var localArrival = TimeZoneInfo.ConvertTime(itinerary.ArrivesAt, ViewerTimeZone);
        var arrival = LocalPassengerTime(localArrival, localDeparture);
        var route = journey.Request.Accessible ? "step-free route" : "route";
        var duration = passenger ? TripMinutes(itinerary.TotalSeconds) : $"{Math.Ceiling(itinerary.TotalSeconds / 60)} minutes";
        var summary = $"The {route} from {Network.Station(journey.Request.Origin).Name} to {Network.Station(journey.Request.Destination).Name} " +
            $"takes about {duration}, arriving at {arrival}";
        if (passenger)
        {
            var heading = label is null ? "" : $"**{label}**\n";
            var readinessLabel = journey.Request.Accessible ? "Ready for a step-free trip from" : "Ready to leave";
            var reference = TimeZoneInfo.ConvertTime(outboundDeparture ?? _serviceTime ?? journey.AsOf, ViewerTimeZone);
            var readiness = $"- {readinessLabel} {Network.Station(journey.Request.Origin).Name}: {LocalPassengerTime(localDeparture, reference)}.";
            var arrivalLine = $"- Approx. arrival at {Network.Station(journey.Request.Destination).Name}: {arrival} ({duration}).";
            return PassengerJourney(itinerary, $"{heading}{readiness}\n{arrivalLine}");
        }
        var warning = itinerary.Warnings.Count > 0 ? " Check the estimate warnings on the card." : "";
        return $"{summary} ({ViewerTimeZone.Id}, simulated). The route card includes boarding waits and transfers.{warning}";
    }

    private string DescribeStepFree(StepFreeResult result)
    {
        if (result.Stale || result.OriginAccess is not { } origin)
            return "I couldn't verify current step-free access. Please check again before travelling.";

        var station = Network.Station(origin.StationId).Name;
        var trip = result.Journey?.Request;
        var destination = trip is null ? null : Network.Station(trip.Destination).Name;
        var summary = destination is null
            ? origin.Available ? $"Step-free station access is available at {station}." : $"Step-free station access is unavailable at {station}."
            : $"I couldn't find a step-free rail trip from {station} to {destination}" +
              (trip!.DepartureTime is { } departure
                  ? $" leaving at {LocalPassengerTime(TimeZoneInfo.ConvertTime(departure, ViewerTimeZone), TimeZoneInfo.ConvertTime(result.AsOf, ViewerTimeZone))}."
                  : " right now.");
        var reasons = result.Blocks.Select(block =>
        {
            var name = Network.Station(block.StationId).Name;
            return block.Kind switch
            {
                "elevator-outage" => $"A lift is out at {name}" +
                    (block.Role == "transfer" ? ", blocking a step-free transfer." : "."),
                "platform-closure" => $"Platform access is closed at {name}.",
                "station-closure" => $"{name} is closed.",
                _ => $"Step-free access is restricted at {name}."
            };
        });
        var detail = string.Join(' ', reasons);
        if (detail.Length > 0) summary += $" {detail}";
        if (result.Alternatives.Count == 0)
            return origin.Available && trip is null ? summary :
                summary + " I couldn't verify another station that would work.";

        var alternatives = result.Alternatives.Select(option =>
        {
            var name = Network.Station(option.StationId).Name;
            var rail = option.TotalSeconds is not { } seconds ? "" :
                $"; about {TripMinutes(seconds)} by rail from there{(option.EstimateKind == "schedule" ? " (schedule estimate)" : "")}";
            return $"- {name}: {option.SurfaceMeters} m{rail}.";
        });
        var heading = destination is null
            ? $"Closest stations with verified step-free access, by modeled road distance from {station}:"
            : $"Closest verified step-free trips to {destination}, by modeled road distance from {station}:";
        return $"{summary}\n\n{heading}\n{string.Join('\n', alternatives)}\n\n" +
            "These distances do not verify a step-free walking route. If you need accessible transit, you can get a free on-demand ride to a station.";
    }

    private static string PassengerArrivals(StationBoard board, string station)
    {
        if (board.Arrivals.Count == 0) return $"No arrival times are available at {station}.";
        var arrivals = board.Arrivals.Take(3).Select(arrival =>
        {
            var direction = arrival.Destination == board.StationId ? "terminating here"
                : $"toward {Network.Station(arrival.Destination).Name}";
            var eta = arrival.Seconds switch
            {
                null => "time unavailable",
                0 => "due now",
                var seconds => $"in about {TripMinutes(seconds.Value)}"
            };
            return $"- {Network.Line(arrival.Line).Name} {direction}: {eta}.";
        });
        return $"{station} arrivals:\n\n{string.Join('\n', arrivals)}";
    }

    private static string PassengerJourney(Itinerary itinerary, string summary)
    {
        var rides = itinerary.Legs.Where(leg => leg is { Kind: "ride", Line: not null }).ToArray();
        var directions = rides.Select((leg, index) =>
        {
            var direction = leg.Destination is null || leg.Destination == leg.To
                ? "" : $" toward {Network.Station(leg.Destination).Name}";
            var wait = leg.WaitSeconds > 0 ? $" (wait about {TripMinutes(leg.WaitSeconds)})" : "";
            var action = index == 0
                ? $"At {Network.Station(leg.From).Name}, take"
                : $"Transfer at {Network.Station(leg.From).Name}: take";
            return $"- {action} {Network.Line(leg.Line!).Name}{direction} to {Network.Station(leg.To).Name}{wait}.";
        });
        var caveats = rides.SelectMany(leg => PassengerCaveats(itinerary, leg)).Distinct();
        var details = directions.Concat(caveats.Select(caveat => $"- {caveat}")).ToArray();
        return details.Length == 0 ? summary : $"{summary}\n\n{string.Join('\n', details)}";
    }

    private static IEnumerable<string> PassengerCaveats(Itinerary itinerary, JourneyLeg leg)
    {
        var station = Network.Station(leg.From).Name;
        // Match planner diagnostics for this ride, but never use their raw text as rider copy.
        if (itinerary.Warnings.Any(warning =>
            warning.StartsWith($"The {leg.Line} arrival at {leg.From} serving {leg.To} is uncertain;", StringComparison.Ordinal) ||
            warning.StartsWith($"The {leg.From} live board is stale;", StringComparison.Ordinal)))
            yield return $"The wait at {station} may vary.";
        if (itinerary.Warnings.Any(warning =>
            warning.StartsWith($"High {leg.Line} occupancy at {leg.From} ", StringComparison.Ordinal)))
            yield return $"Trains may be crowded at {station}; you may need to wait for the next one.";
        if (itinerary.Accessible && itinerary.Warnings.Any(warning =>
            warning.StartsWith($"Restriction at {leg.From}: elevator-outage:", StringComparison.Ordinal)))
            yield return $"A lift is out at {station}.";
    }

    private static string TripMinutes(double seconds)
    {
        var minutes = Math.Ceiling(seconds / 60);
        return minutes == 1 ? "1 minute" : $"{minutes} minutes";
    }

    private static string LocalPassengerTime(DateTimeOffset value, DateTimeOffset reference)
    {
        var format = value.Date == reference.Date ? "h:mm tt" : "ddd, MMM d 'at' h:mm tt";
        return value.ToString(format, CultureInfo.InvariantCulture);
    }
}

internal static class PassengerJourneyStore
{
    private static readonly ConcurrentDictionary<string, (JourneyResult Journey, DateTimeOffset ExpiresAt)> Journeys = new();

    public static string Add(JourneyResult journey)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var expired in Journeys.Where(entry => entry.Value.ExpiresAt <= now))
            Journeys.TryRemove(expired.Key, out _);
        var id = Guid.NewGuid().ToString("N");
        Journeys[id] = (journey, now.AddMinutes(30));
        return id;
    }

    public static bool TryGet(string id, out JourneyResult journey)
    {
        journey = null!;
        if (!Journeys.TryGetValue(id, out var saved)) return false;
        if (saved.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            Journeys.TryRemove(id, out _);
            return false;
        }
        journey = saved.Journey;
        return true;
    }
}

internal abstract class AssistantToolRegistry
{
    private readonly ServiceJsonClient? _services;
    protected ServiceJsonClient Services => _services ?? throw new ToolServiceException("Service tools are not configured.");
    protected readonly ProposalStore Proposals;

    protected AssistantToolRegistry(ProposalStore proposals, ServiceJsonClient? services)
    {
        Proposals = proposals;
        _services = services;
    }

    public abstract IReadOnlyList<AssistantToolDefinition> Definitions { get; }
    protected virtual bool IsPassenger => false;

    public async Task<string> ExecuteAsync(
        string name,
        string arguments,
        AssistantCapture capture,
        CancellationToken cancellationToken)
    {
        if (!Definitions.Any(definition => definition.Name == name))
        {
            return SerializeError("The requested tool is not available to this assistant.");
        }

        capture.Tools.Add(name);
        try
        {
            using var document = JsonDocument.Parse(arguments);
            return await ExecuteCoreAsync(name, document.RootElement, capture, cancellationToken);
        }
        catch (JsonException)
        {
            return SerializeError("The tool arguments were invalid.");
        }
        catch (ToolInputException exception)
        {
            return SerializeError(exception.Message);
        }
        catch (ToolServiceException exception)
        {
            return SerializeError(exception.Message, exception.StatusCode);
        }
    }

    public async Task<ScenarioContext?> TryGetWorldAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await GetWorldContextAsync(cancellationToken);
        }
        catch (ToolServiceException)
        {
            return null;
        }
    }

    protected abstract Task<string> ExecuteCoreAsync(
        string name,
        JsonElement arguments,
        AssistantCapture capture,
        CancellationToken cancellationToken);

    protected Task<ScenarioContext> GetWorldContextAsync(CancellationToken cancellationToken) =>
        Services.GetWorldContextAsync(cancellationToken);

    protected async Task<string> ExecuteReadToolAsync(
        string name,
        JsonElement arguments,
        AssistantCapture capture,
        CancellationToken cancellationToken)
    {
        switch (name)
        {
            case "resolve_station":
                return ResolveStation(arguments, capture);
            case "system_status":
                return Serialize(await Services.GetSystemStatusAsync(cancellationToken));
            case "get_arrivals":
            {
                var lookup = StationResolution(RequiredString(arguments, "station"));
                if (lookup.Station is null)
                {
                    Clarify(lookup, capture);
                    return Serialize(lookup);
                }
                var board = await Services.GetAsync<StationBoard>(
                    "information",
                    $"stations/{Uri.EscapeDataString(lookup.Station.Id)}/arrivals",
                    "Passenger information service",
                    cancellationToken);
                capture.Arrivals = board;
                return Serialize(board);
            }
            case "check_step_free_access":
            {
                var lookup = StationResolution(RequiredString(arguments, "station"));
                if (lookup.Station is null)
                {
                    Clarify(lookup, capture);
                    return Serialize(lookup);
                }
                try
                {
                    capture.StationAccess = await CheckStepFreeAsync(new(lookup.Station.Id), cancellationToken);
                    return Serialize(capture.StationAccess);
                }
                catch (ToolServiceException)
                {
                    capture.Clarification = "I couldn't verify current step-free access. Please check again before travelling.";
                    throw;
                }
            }
            case "plan_journey":
            {
                var suppliedOrigin = OptionalString(arguments, "origin");
                var suppliedDestination = OptionalString(arguments, "destination");
                var returning = IsPassenger &&
                    capture.JourneyContext?.AwaitingReturnTime == true &&
                    capture.TripTiming?.ReturnTime is not null;
                var originText = returning
                    ? capture.JourneyContext!.Destination
                    : suppliedOrigin ?? capture.JourneyContext?.Origin;
                var destinationText = returning
                    ? capture.JourneyContext!.Origin
                    : suppliedDestination ?? capture.JourneyContext?.Destination;
                if (string.IsNullOrWhiteSpace(originText) || string.IsNullOrWhiteSpace(destinationText))
                {
                    return SerializeError("Origin and destination are required. Resolve ambiguous station names before planning.");
                }
                var origin = StationResolution(originText);
                var destination = StationResolution(destinationText);
                if (origin.Station is null || destination.Station is null)
                {
                    Clarify(origin.Station is null ? origin : destination, capture);
                    return Serialize(new
                    {
                        ok = false,
                        error = "One or more station names need clarification.",
                        origin,
                        destination
                    });
                }

                var askReturnTime = IsPassenger &&
                    (OptionalBool(arguments, "askReturnTime") == true ||
                     capture.TripTiming is { RoundTrip: true, ReturnTime: null } ||
                     capture.ReuseTripContext && capture.JourneyContext?.AwaitingReturnTime == true);
                var pairedReturnTime = capture.TripTiming?.ReturnTime ??
                    OptionalOffsetDateTime(arguments, "returnTime", capture.ViewerTimeZone);
                var context = IsPassenger && suppliedOrigin is not null && suppliedDestination is not null &&
                    !capture.ReuseTripContext
                    ? null : capture.JourneyContext is null ? null : new JourneyRequest(
                        capture.JourneyContext.Origin,
                        capture.JourneyContext.Destination,
                        capture.JourneyContext.Accessible,
                        capture.JourneyContext.DepartureTime);
                var authoritativeDeparture = returning
                    ? capture.TripTiming?.ReturnTime
                    : capture.TripTiming?.OutboundTime;
                var request = BuildJourneyRequest(arguments, context, origin.Station.Id, destination.Station.Id,
                    capture.ViewerTimeZone, authoritativeDeparture);
                if (IsPassenger && capture.AccessiblePreference is { } accessible)
                    request = request with { Accessible = accessible };
                var journey = await ReadJourneyAsync(request, capture, cancellationToken);
                capture.Journey = journey;
                if (IsPassenger)
                {
                    capture.RecordPassengerJourney(journey, askReturnTime);
                    if (!returning && pairedReturnTime is { } returnTime)
                    {
                        var returnRequest = new JourneyRequest(
                            journey.Request.Destination,
                            journey.Request.Origin,
                            journey.Request.Accessible,
                            returnTime);
                        var returnJourney = await ReadJourneyAsync(returnRequest, capture, cancellationToken);
                        capture.Journey = returnJourney;
                        capture.RecordPassengerJourney(returnJourney, askReturnTime: false);
                        capture.SaveJourneyContext(journey, awaitingReturnTime: false);
                        return Serialize(new
                        {
                            journeys = new[] { journey, returnJourney },
                            stepFree = capture.StepFreeChecks.Values,
                            viewerTimeZone = capture.ViewerTimeZone.Id,
                            localDepartures = new[]
                            {
                                capture.LocalTime(journey.Request.DepartureTime ?? journey.AsOf),
                                capture.LocalTime(returnJourney.Request.DepartureTime ?? returnJourney.AsOf)
                            }
                        });
                    }
                    capture.SaveJourneyContext(
                        returning && capture.JourneyContext?.SavedOutboundId is not null &&
                        PassengerJourneyStore.TryGet(capture.JourneyContext.SavedOutboundId, out var outbound)
                            ? outbound
                            : journey,
                        awaitingReturnTime: !returning && askReturnTime);
                }
                return Serialize(new
                {
                    journey,
                    stepFree = capture.StepFreeChecks.GetValueOrDefault(journey.Request),
                    viewerTimeZone = capture.ViewerTimeZone.Id,
                    localAsOf = capture.LocalTime(journey.AsOf),
                    localDeparture = capture.LocalTime(journey.Request.DepartureTime ?? journey.AsOf),
                    localArrivals = journey.Options.Select(option => new { option.Id, arrivesAt = capture.LocalTime(option.ArrivesAt) })
                });
            }
            default:
                throw new ToolInputException("The requested tool is not available.");
        }
    }

    private Task<StepFreeResult> CheckStepFreeAsync(StepFreeRequest request, CancellationToken cancellationToken) =>
        Services.PostAsync<StepFreeRequest, StepFreeResult>("planner", "step-free", request, "Journey planner", cancellationToken);

    private async Task<JourneyResult> ReadJourneyAsync(JourneyRequest request, AssistantCapture capture, CancellationToken cancellationToken)
    {
        if (!IsPassenger || !request.Accessible)
            return await Services.PostAsync<JourneyRequest, JourneyResult>("planner", "journeys", request, "Journey planner", cancellationToken);

        StepFreeResult check;
        try
        {
            check = await CheckStepFreeAsync(new(request.Origin, request.Destination, request.DepartureTime), cancellationToken);
        }
        catch (ToolServiceException)
        {
            capture.Clarification = "I couldn't verify current step-free service. Please check again before travelling.";
            throw;
        }
        capture.StepFreeChecks[request] = check;
        return check.Journey ?? new JourneyResult(check.ScenarioId, check.Generation, check.Version, check.AsOf,
            request, [], "Current step-free service could not be verified.");
    }

    protected async Task<string> CompareAsync(
        JsonElement arguments,
        AssistantCapture capture,
        CancellationToken cancellationToken)
    {
        var action = ServiceActionParser.Parse(arguments);
        var world = await GetWorldContextAsync(cancellationToken);
        var comparison = await Services.PostAsync<CompareRequest, ComparisonResult>(
            "scenario",
            "compare",
            new CompareRequest(action, world.ScenarioId, world.Generation),
            "Scenario comparison service",
            cancellationToken);
        capture.Comparison = comparison;
        return Serialize(comparison);
    }

    protected async Task<string> ProposeAsync(
        JsonElement arguments,
        AssistantCapture capture,
        CancellationToken cancellationToken)
    {
        var action = ServiceActionParser.Parse(arguments);
        var explanation = OptionalString(arguments, "explanation") ??
                          "Proposed by the operations assistant for human review.";
        if (explanation.Length > 1000)
        {
            throw new ToolInputException("The proposal explanation is too long.");
        }

        var world = await GetWorldContextAsync(cancellationToken);
        var proposal = Proposals.Add(action, world, explanation);
        capture.Proposal = proposal;
        return Serialize(proposal);
    }

    protected async Task<string> ExecuteActionAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var action = ServiceActionParser.Parse(arguments);
        var reason = OptionalString(arguments, "reason") ?? "Operations assistant request";
        if (reason.Length > 500)
        {
            throw new ToolInputException("The action reason is too long.");
        }

        var world = await GetWorldContextAsync(cancellationToken);
        var agentKey = Environment.GetEnvironmentVariable("AGENT_KEY");
        if (string.IsNullOrWhiteSpace(agentKey))
        {
            throw new ToolServiceException("Dispatch execution is unavailable because AGENT_KEY is not configured.");
        }
        var envelope = new CommandEnvelope(
            Guid.NewGuid().ToString("N"),
            world.ScenarioId,
            world.Generation,
            world.Version,
            "agent",
            reason,
            action);
        var result = await Services.PostCommandAsync(
            "dispatch",
            "agent-actions",
            envelope,
            "Dispatch service",
            cancellationToken,
            agentKey);
        return Serialize(result);
    }

    private static JourneyRequest BuildJourneyRequest(
        JsonElement arguments,
        JourneyRequest? context,
        string origin,
        string destination,
        TimeZoneInfo viewerTimeZone,
        DateTimeOffset? authoritativeDeparture = null)
    {
        var accessible = OptionalBool(arguments, "accessible") ?? context?.Accessible ?? false;
        DateTimeOffset? departureTime = authoritativeDeparture ?? context?.DepartureTime;
        var departureText = OptionalString(arguments, "departureTime");
        if (authoritativeDeparture is null && departureText is not null)
        {
            departureTime = ParseOffsetDateTime(departureText, "departureTime", viewerTimeZone);
        }

        return new JourneyRequest(origin, destination, accessible, departureTime);
    }

    protected string ResolveStation(JsonElement arguments, AssistantCapture capture, string property = "name")
    {
        var text = RequiredString(arguments, property);
        var resolution = StationResolution(text);
        if (!resolution.Resolved) Clarify(resolution, capture);
        return Serialize(resolution);
    }

    private static void Clarify(StationLookup resolution, AssistantCapture capture) =>
        capture.Clarification = resolution.Choices.Count > 0
            ? $"Which station do you mean: {string.Join(", ", resolution.Choices.Select(choice => choice.Name))}?"
            : "I couldn't match that name to an Aspireland station. Please choose a station on the map or in the station directory.";

    protected static string? ResolveStationId(string text)
    {
        if (text.Length > 100) return null;
        return Network.Resolve(text);
    }

    private sealed record StationChoice(string Id, string Name);
    private sealed record StationLookup(bool Resolved, StationChoice? Station, IReadOnlyList<StationChoice> Choices, string? Message = null);

    private StationLookup StationResolution(string text)
    {
        var station = ResolveStationId(text);
        if (station is not null)
        {
            var definition = Network.Station(station);
            return new(true, new(definition.Id, definition.Name), []);
        }

        var name = Network.NormalizeStationName(text);
        var normalized = Normalize(name);
        var choices = Network.Definition.Stations
            .Where(candidate =>
                normalized.Length > 0 &&
                (Normalize(candidate.Name).Contains(normalized, StringComparison.Ordinal) ||
                 candidate.Id.Contains(normalized, StringComparison.Ordinal)))
            .Select(candidate => new StationChoice(candidate.Id, candidate.Name))
            .Take(8)
            .ToList();
        if (IsPassenger && choices.Count == 0 && text.Length <= 100 && name.Length is >= 4 and <= 40 &&
            name.All(c => char.IsAsciiLetter(c) || c == ' '))
        {
            var close = Network.Definition.Stations
                .Where(candidate => OneEditAway(name, candidate.Name.ToLowerInvariant()))
                .Take(2)
                .ToArray();
            if (close.Length == 1) return new(true, new(close[0].Id, close[0].Name), []);
        }
        return new(false, null, choices, choices.Count > 0
                ? "The station name is ambiguous. Ask the passenger to choose one of these stations."
                : "No curated Aspireland station or landmark matched that name.");
    }

    private static bool OneEditAway(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        if (left.Length > right.Length) return OneEditAway(right, left);
        if (right.Length - left.Length > 1) return false;
        var prefix = 0;
        while (prefix < left.Length && left[prefix] == right[prefix]) prefix++;
        if (left.Length != right.Length) return left[prefix..].SequenceEqual(right[(prefix + 1)..]);
        if (prefix == left.Length) return false;
        return left[(prefix + 1)..].SequenceEqual(right[(prefix + 1)..]) ||
            (prefix + 1 < left.Length && left[prefix] == right[prefix + 1] && left[prefix + 1] == right[prefix] &&
             left[(prefix + 2)..].SequenceEqual(right[(prefix + 2)..]));
    }

    protected static string RequiredString(JsonElement arguments, string name)
    {
        var value = OptionalString(arguments, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ToolInputException($"{name} is required.");
        }
        return value;
    }

    protected static string? OptionalString(JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }
        if (property.ValueKind != JsonValueKind.String)
        {
            throw new ToolInputException($"{name} must be a string.");
        }
        return property.GetString()?.Trim();
    }

    protected static bool? OptionalBool(JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }
        if (property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new ToolInputException($"{name} must be a boolean.");
        }
        return property.GetBoolean();
    }

    private static DateTimeOffset? OptionalOffsetDateTime(
        JsonElement arguments,
        string name,
        TimeZoneInfo viewerTimeZone)
    {
        var value = OptionalString(arguments, name);
        return value is null ? null : ParseOffsetDateTime(value, name, viewerTimeZone);
    }

    private static DateTimeOffset ParseOffsetDateTime(string value, string name, TimeZoneInfo viewerTimeZone)
    {
        if (!(value.EndsWith('Z') ||
              value.Length >= 6 && (value[^6] == '+' || value[^6] == '-') && value[^3] == ':') ||
            !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            throw new ToolInputException($"{name} must be an ISO 8601 timestamp with an explicit offset.");
        }
        var expectedOffset = viewerTimeZone.GetUtcOffset(parsed.UtcDateTime);
        if (parsed.Offset != expectedOffset)
        {
            throw new ToolInputException($"{name} offset must match the viewer time zone for that date.");
        }
        return parsed;
    }

    protected static string Serialize(object value) => JsonSerializer.Serialize(value, Wire.Json);
    protected static string SerializeError(string message, int? status = null) =>
        Serialize(new { ok = false, error = message, status });
    private static string Normalize(string value) =>
        new(value.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}

internal sealed class PassengerToolRegistry(ProposalStore proposals, ServiceJsonClient? services = null) : AssistantToolRegistry(proposals, services)
{
    public override IReadOnlyList<AssistantToolDefinition> Definitions { get; } = ToolDefinitions.Passenger;
    protected override bool IsPassenger => true;

    protected override Task<string> ExecuteCoreAsync(
        string name,
        JsonElement arguments,
        AssistantCapture capture,
        CancellationToken cancellationToken) =>
        ExecuteReadToolAsync(name, arguments, capture, cancellationToken);
}

internal sealed class OperatorToolRegistry(ProposalStore proposals, ServiceJsonClient? services = null, string? agentKey = null)
    : AssistantToolRegistry(proposals, services)
{
    public override IReadOnlyList<AssistantToolDefinition> Definitions { get; } = ToolDefinitions.Operator;

    protected override Task<string> ExecuteCoreAsync(
        string name,
        JsonElement arguments,
        AssistantCapture capture,
        CancellationToken cancellationToken) =>
        name switch
        {
            "diagnose_station" => DiagnoseStationAsync(arguments, capture, cancellationToken),
            "compare_intervention" => CompareAsync(arguments, capture, cancellationToken),
            "propose_action" => ProposeAsync(arguments, capture, cancellationToken),
            "execute_action" => ExecuteActionAsync(arguments, cancellationToken),
            _ => ExecuteReadToolAsync(name, arguments, capture, cancellationToken)
        };

    private async Task<string> DiagnoseStationAsync(JsonElement arguments, AssistantCapture capture, CancellationToken ct)
    {
        var station = ResolveStationId(RequiredString(arguments, "station"));
        if (station is null) return ResolveStation(arguments, capture, "station");

        capture.StationDiagnosisText = $"{Network.Station(station).Name}: I can't determine the cause without current station evidence. The Dispatch checkpoint is unavailable.";
        if (string.IsNullOrWhiteSpace(agentKey)) return SerializeError("Station diagnosis is unavailable.");
        var evidence = await Services.GetStationDiagnosisAsync(station, agentKey, ct);
        var localAsOf = capture.LocalTime(evidence.SimTime);
        var time = $"{localAsOf} ({capture.ViewerTimeZone.Id}, simulated)";
        if (evidence is { Status: "fresh", Queue: { } queue })
        {
            var hold = evidence.Trains.FirstOrDefault(t => !string.IsNullOrEmpty(t.HoldReason));
            var restriction = evidence.Restrictions.FirstOrDefault();
            var holds = hold is null ? "" : $" {hold.Id}: {hold.HoldReason}";
            var restrictions = restriction is null ? "" : $" One active restriction: {restriction.Kind} ({restriction.Target}).";
            var load = evidence.TrainCount == 0 ? "No nearby trains are recorded."
                : $"Nearby trains carry {evidence.TrainLoad}/{evidence.TrainCapacity} passengers; {evidence.FullTrainCount} full, " +
                  $"{evidence.HeldTrainCount} with a hold or recorded restriction.{holds}";
            List<string> contributors = [];
            if (evidence.FullTrainCount > 0) contributors.Add("full trains");
            if (evidence.HeldTrainCount > 0) contributors.Add("train holds");
            if (evidence.RestrictionCount > 0) contributors.Add("active restrictions");
            var assessment = evidence.Assessment == "possible-contributors"
                ? $"These are possible contributors, not a confirmed cause: {string.Join(", ", contributors)}."
                : "This snapshot doesn't establish a cause for a backup.";
            capture.StationDiagnosisText =
                $"{evidence.StationName}: {queue.Waiting} waiting at {time} ({queue.PlatformWaiting} on platforms, " +
                $"capacity {queue.PlatformCapacity}; {queue.AccessQueue} at access points). " +
                $"{queue.ReadyTransfers} more are ready to transfer. " +
                $"{load} {evidence.RestrictionCount} active restrictions and " +
                $"{evidence.ReservedBlockCount} adjacent block reservations.{restrictions} " +
                $"{assessment} One checkpoint can't show whether the queue is growing.";
        }
        else
            capture.StationDiagnosisText = $"{evidence.StationName}: I can't determine a current cause. The last station checkpoint (at {time}) is stale.";
        return Serialize(new { evidence, localAsOf, viewerTimeZone = capture.ViewerTimeZone.Id });
    }
}

internal static class ToolDefinitions
{
    private static readonly AssistantToolDefinition ResolveStation = Define(
        "resolve_station",
        "Resolve an exact curated Aspireland station or landmark. Ambiguous input returns clarification choices.",
        """
        {"type":"object","properties":{"name":{"type":"string"}},"required":["name"],"additionalProperties":false}
        """);
    private static readonly AssistantToolDefinition SystemStatus = Define(
        "system_status",
        "Read the current authoritative simulated system state, time, incidents, trains, queues, and authority mode.",
        """{"type":"object","properties":{},"additionalProperties":false}""");
    private static readonly AssistantToolDefinition GetArrivals = Define(
        "get_arrivals",
        "Read the live arrival board for one exact station.",
        """
        {"type":"object","properties":{"station":{"type":"string"}},"required":["station"],"additionalProperties":false}
        """);
    private static readonly AssistantToolDefinition DiagnoseStation = Define(
        "diagnose_station",
        "Read station-specific, authoritative passenger queues, nearby train load and holds, local restrictions, and block reservations. Use this to explain a station backup or delay. A reservation is not proof of obstruction.",
        """
        {"type":"object","properties":{"station":{"type":"string"}},"required":["station"],"additionalProperties":false}
        """);
    private static readonly AssistantToolDefinition PlanJourney = Define(
        "plan_journey",
        "Plan a real journey. Origin or destination may be omitted only when available in the prior journey context.",
        """
        {"type":"object","properties":{"origin":{"type":"string"},"destination":{"type":"string"},"accessible":{"type":"boolean"},"departureTime":{"type":"string","description":"ISO 8601 simulated departure time"}},"additionalProperties":false}
        """);
    private static readonly AssistantToolDefinition PlanPassengerJourney = Define(
        "plan_journey",
        "Plan a real journey, including verified nearby alternatives if a step-free journey is blocked. Pass supplied station names as written; this tool resolves them. A new trip with both stations and no departure time leaves now. For a round trip with both times, include returnTime in this single call. Omit both stations when following up on the same trip without endpoint changes.",
        """
        {"type":"object","properties":{"origin":{"type":"string"},"destination":{"type":"string"},"accessible":{"type":"boolean"},"departureTime":{"type":"string","description":"ISO 8601 simulated departure time with the viewer-zone offset"},"returnTime":{"type":"string","description":"ISO 8601 return departure time with the viewer-zone offset, when supplied by the rider"},"askReturnTime":{"type":"boolean","description":"True for the outbound leg of a round trip when the rider has not given a return time."}},"additionalProperties":false}
        """);
    private static readonly AssistantToolDefinition CheckStepFreeAccess = Define(
        "check_step_free_access",
        "Check current step-free station access and, when blocked, nearby accessible stations ranked by modeled road distance. Does not verify walking or an onward trip.",
        """{"type":"object","properties":{"station":{"type":"string"}},"required":["station"],"additionalProperties":false}""");
    private static readonly AssistantToolDefinition CompareIntervention = Define(
        "compare_intervention",
        "Run the authoritative paired simulator comparison for a bounded service action. Use numeric amount for hold-train and set-headway; value is only for deploy-reserve and short-turn.",
        ActionSchema(includeExplanation: false, includeReason: false));
    private static readonly AssistantToolDefinition ProposeAction = Define(
        "propose_action",
        "Create a 15-minute proposal tied to the latest scenario identity and version. This does not execute it. A hold-train duration must be passed as numeric amount seconds, never as value.",
        ActionSchema(includeExplanation: true, includeReason: false));
    private static readonly AssistantToolDefinition ExecuteAction = Define(
        "execute_action",
        "Request an allowlisted action through the dispatch API. The runtime authority mode and limits decide the actual CommandResult. Use numeric amount for hold-train and set-headway.",
        ActionSchema(includeExplanation: false, includeReason: true));

    public static IReadOnlyList<AssistantToolDefinition> Passenger { get; } =
        [ResolveStation, SystemStatus, GetArrivals, PlanPassengerJourney, CheckStepFreeAccess];
    public static IReadOnlyList<AssistantToolDefinition> Operator { get; } =
        [ResolveStation, SystemStatus, GetArrivals, DiagnoseStation, PlanJourney, CompareIntervention, ProposeAction, ExecuteAction];

    private static AssistantToolDefinition Define(string name, string description, string schema) =>
        new(name, description, JsonDocument.Parse(schema).RootElement.Clone());

    private static string ActionSchema(bool includeExplanation, bool includeReason)
    {
        var extraProperty = includeExplanation
            ? ""","explanation":{"type":"string","maxLength":1000}"""
            : includeReason
                ? ""","reason":{"type":"string","maxLength":500}"""
                : "";
        return $$"""
        {
          "type":"object",
          "properties":{
            "kind":{"type":"string","enum":["hold-train","release-train","deploy-reserve","short-turn","set-headway"]},
            "target":{"type":"string","description":"Train id, reserve id, or line name, depending on kind"},
            "value":{"type":"string","description":"Only for deploy-reserve line assignment or short-turn station. Never put a hold duration here."},
            "amount":{"type":"number","description":"Required numeric seconds for hold-train (greater than 0, maximum 180) and set-headway (300 to 900)."}{{extraProperty}}
          },
          "required":["kind","target"],
          "additionalProperties":false
        }
        """;
    }
}

internal static class ServiceActionParser
{
    public static ServiceAction Parse(JsonElement arguments)
    {
        var kind = RequiredString(arguments, "kind").ToLowerInvariant();
        var target = RequiredString(arguments, "target");
        var amount = OptionalDouble(arguments, "amount");

        return kind switch
        {
            "hold-train" when (amount ?? NumericValue(arguments, "value")) is > 0 and <= 180
                => new ServiceAction(kind, target, null, amount ?? NumericValue(arguments, "value")),
            "hold-train" =>
                throw new ToolInputException("hold-train amount must be greater than 0 and no more than 180 seconds."),
            "release-train" =>
                new ServiceAction(kind, target),
            "deploy-reserve" =>
                ParseReserve(target, OptionalString(arguments, "value")),
            "short-turn" when OptionalString(arguments, "value") is "west-junction" or "east-junction" =>
                new ServiceAction(kind, target, OptionalString(arguments, "value")),
            "short-turn" =>
                throw new ToolInputException("short-turn value must be west-junction or east-junction."),
            "set-headway" =>
                ParseHeadway(target, amount ?? NumericValue(arguments, "value")),
            _ =>
                throw new ToolInputException("The action kind is not allowlisted.")
        };
    }

    private static ServiceAction ParseReserve(string target, string? value)
    {
        var reserve = target.ToLowerInvariant();
        var line = value?.ToLowerInvariant() switch
        {
            "blue" => "Blue",
            "amber" => "Amber",
            "green" => "Green",
            _ => null
        };
        if (reserve is not ("reserve-ba" or "reserve-green") || line is null)
        {
            throw new ToolInputException("deploy-reserve requires reserve-ba or reserve-green and a Blue, Amber, or Green value.");
        }
        if (reserve == "reserve-ba" && line == "Green" || reserve == "reserve-green" && line != "Green")
        {
            throw new ToolInputException("The selected reserve is not compatible with that line.");
        }
        return new ServiceAction("deploy-reserve", reserve, line);
    }

    private static ServiceAction ParseHeadway(string target, double? amount)
    {
        var line = target.ToLowerInvariant() switch
        {
            "blue" => "Blue",
            "amber" => "Amber",
            "green" => "Green",
            "ax" => "AX",
            _ => null
        };
        if (line is null || amount is < 300 or > 900)
        {
            throw new ToolInputException("set-headway requires a valid line and 300 to 900 seconds.");
        }
        return new ServiceAction("set-headway", line, null, amount);
    }

    private static string RequiredString(JsonElement arguments, string name)
    {
        var value = OptionalString(arguments, name);
        if (string.IsNullOrWhiteSpace(value) || value.Length > 100)
        {
            throw new ToolInputException($"{name} is required and must be at most 100 characters.");
        }
        return value;
    }

    private static string? OptionalString(JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }
        if (property.ValueKind != JsonValueKind.String)
        {
            throw new ToolInputException($"{name} must be a string.");
        }
        return property.GetString()?.Trim().ToLowerInvariant();
    }

    private static double? OptionalDouble(JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetDouble(out var value) ||
            double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ToolInputException($"{name} must be a finite number.");
        }
        return value;
    }

    private static double? NumericValue(JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }
        if (property.ValueKind == JsonValueKind.Number &&
            property.TryGetDouble(out var number) &&
            double.IsFinite(number))
        {
            return number;
        }
        if (property.ValueKind == JsonValueKind.String &&
            double.TryParse(property.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out number) &&
            double.IsFinite(number))
        {
            return number;
        }
        return null;
    }
}

internal sealed class ServiceJsonClient(HttpClient http)
{
    private const int MaximumResponseBytes = 2 * 1024 * 1024;

    public Task<T> GetAsync<T>(
        string serviceName,
        string path,
        string service,
        CancellationToken cancellationToken) =>
        SendAsync<T>(serviceName, path, service, HttpMethod.Get, null, cancellationToken, null);

    public Task<TResponse> PostAsync<TRequest, TResponse>(
        string serviceName,
        string path,
        TRequest value,
        string service,
        CancellationToken cancellationToken,
        string? agentKey = null) =>
        SendAsync<TResponse>(serviceName, path, service, HttpMethod.Post, value, cancellationToken, agentKey, false);

    public Task<CommandResult> PostCommandAsync(
        string serviceName,
        string path,
        CommandEnvelope value,
        string service,
        CancellationToken cancellationToken,
        string agentKey) =>
        SendAsync<CommandResult>(serviceName, path, service, HttpMethod.Post, value, cancellationToken, agentKey, true);

    public Task<ScenarioContext> GetWorldContextAsync(CancellationToken cancellationToken) =>
        GetAsync<ScenarioContext>("scenario", "context", "Scenario context service", cancellationToken);

    public Task<JsonElement> GetSystemStatusAsync(CancellationToken cancellationToken) =>
        GetAsync<JsonElement>("scenario", "status", "Scenario status service", cancellationToken);

    public Task<StationDiagnosis> GetStationDiagnosisAsync(string station, string agentKey, CancellationToken cancellationToken) =>
        SendAsync<StationDiagnosis>("dispatch", $"stations/{Uri.EscapeDataString(station)}/diagnosis", "Station diagnosis service",
            HttpMethod.Get, null, cancellationToken, agentKey);

    private async Task<T> SendAsync<T>(
        string serviceName,
        string path,
        string service,
        HttpMethod method,
        object? value,
        CancellationToken cancellationToken,
        string? agentKey,
        bool acceptErrorJson = false)
    {
        using var request = new HttpRequestMessage(method, $"https+http://{serviceName}/{path.TrimStart('/')}");
        if (value is not null)
        {
            request.Content = JsonContent.Create(value, options: Wire.Json);
        }
        if (!string.IsNullOrWhiteSpace(agentKey))
        {
            request.Headers.TryAddWithoutValidation("X-Agent-Key", agentKey);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ToolServiceException($"{service} timed out.");
        }
        catch (HttpRequestException)
        {
            throw new ToolServiceException($"{service} could not be reached.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode && !acceptErrorJson)
            {
                throw new ToolServiceException($"{service} returned HTTP {(int)response.StatusCode}.", (int)response.StatusCode);
            }
            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            {
                throw new ToolServiceException($"{service} returned a response that was too large.");
            }

            using var buffer = new MemoryStream();
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                var chunk = new byte[16 * 1024];
                while (true)
                {
                    var read = await stream.ReadAsync(chunk, timeout.Token);
                    if (read == 0) break;
                    if (buffer.Length + read > MaximumResponseBytes)
                    {
                        throw new ToolServiceException($"{service} returned a response that was too large.");
                    }
                    buffer.Write(chunk, 0, read);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ToolServiceException($"{service} timed out.");
            }
            catch (IOException)
            {
                throw new ToolServiceException($"{service} response could not be read.");
            }

            try
            {
                return JsonSerializer.Deserialize<T>(buffer.ToArray(), Wire.Json)
                       ?? throw new ToolServiceException($"{service} returned an empty response.");
            }
            catch (JsonException)
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new ToolServiceException($"{service} returned HTTP {(int)response.StatusCode}.", (int)response.StatusCode);
                }
                throw new ToolServiceException($"{service} returned invalid JSON.");
            }
        }
    }

}

internal sealed class ToolInputException(string message) : Exception(message);
internal sealed class ToolServiceException(string message, int? statusCode = null) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
}
