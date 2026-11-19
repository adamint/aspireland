using System.Text.Json;

namespace MetroOps.Contracts;

public static class Wire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;
}

public sealed class WorldState
{
    public string ScenarioId { get; set; } = Guid.NewGuid().ToString("N");
    public int Generation { get; set; } = 1;
    public long Seed { get; set; } = 20261117;
    public int StreamVersion { get; set; } = 1;
    public long Version { get; set; }
    public DateTimeOffset SimTime { get; set; } = new(2026, 11, 17, 12, 30, 0, TimeSpan.Zero);
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool Paused { get; set; }
    public double Speed { get; set; } = 10;
    public string Preset { get; set; } = "normal";
    public string AuthorityMode { get; set; } = "advise";
    public List<TrainState> Trains { get; set; } = [];
    public List<Cohort> Cohorts { get; set; } = [];
    public List<Incident> Incidents { get; set; } = [];
    public List<HistoryEvent> History { get; set; } = [];
    public Dictionary<string, string> BlockOccupancy { get; set; } = [];
    public Dictionary<string, string> PlatformOccupancy { get; set; } = [];
    public Dictionary<string, string> JunctionReservations { get; set; } = [];
    public Dictionary<string, MovementGrant> Grants { get; set; } = [];
    public Dictionary<string, CommandResult> Commands { get; set; } = [];
    public Dictionary<string, double> Headways { get; set; } = Network.Definition.Lines.ToDictionary(line => line.Id, line => (double)line.HeadwaySeconds);
    public PassengerMetrics Metrics { get; set; } = new();
    public List<Flight> Flights { get; set; } = [];
    public long DemandCursor { get; set; } = -1;
    public DateTimeOffset? LastAgentActionAt { get; set; }
}

public sealed record TrainControlView(string ScenarioId, int Generation, DateTimeOffset SimTime,
    bool Paused, TrainState Train, MovementGrant? Grant);

public sealed record ProjectionEnvelope(long Sequence, string EventId, string ScenarioId, int Generation,
    long Version, DateTimeOffset SimTime, RiderWorld World);

public sealed class TrainState
{
    public string Id { get; set; } = "";
    public string Line { get; set; } = "";
    public string State { get; set; } = "dwelling";
    public string StationId { get; set; } = "";
    public string? NextStationId { get; set; }
    public int Direction { get; set; } = 1;
    public int RouteIndex { get; set; }
    public int Capacity { get; set; } = 150;
    public int Load { get; set; }
    public long Version { get; set; }
    public string? HoldReason { get; set; }
    public DateTimeOffset ReadyAt { get; set; }
    public DateTimeOffset? DepartedAt { get; set; }
    public DateTimeOffset? ArrivesAt { get; set; }
    public DateTimeOffset? LastHeartbeatAt { get; set; }
    public string? WorkerInstanceId { get; set; }
    public string? ActiveGrantId { get; set; }
    public string? ShortTurnAt { get; set; }
    public string Depot { get; set; } = "";
    public string[] CompatibleLines { get; set; } = [];
}

public sealed class Cohort
{
    public string Id { get; set; } = "";
    public string Origin { get; set; } = "";
    public string Destination { get; set; } = "";
    public string StationId { get; set; } = "";
    public int Count { get; set; }
    public bool Accessible { get; set; }
    public string State { get; set; } = "outside";
    public string? TrainId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ReadyAt { get; set; }
    public double PatienceSeconds { get; set; } = 1800;
    public double ElapsedWaitSeconds { get; set; }
    public List<JourneyLeg> Legs { get; set; } = [];
    public int LegIndex { get; set; }
    public DateTimeOffset? ReturnAt { get; set; }
    public bool ReturnScheduled { get; set; }
}

public sealed class Incident
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "";
    public string Target { get; set; } = "";
    public string State { get; set; } = "active";
    public string Source { get; set; } = "manual";
    public string Description { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }
    public double Severity { get; set; } = 1;
}

public sealed class HistoryEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ScenarioId { get; set; } = "";
    public int Generation { get; set; }
    public long Version { get; set; }
    public DateTimeOffset SimTime { get; set; }
    public string Kind { get; set; } = "";
    public string EntityId { get; set; } = "";
    public string Source { get; set; } = "consequence";
    public string Description { get; set; } = "";
    public string CorrelationId { get; set; } = "";
    public int StreamVersion { get; set; } = 1;
}

public sealed class PassengerMetrics
{
    public long Generated { get; set; }
    public long Completed { get; set; }
    public long Abandoned { get; set; }
    public long DeniedBoardings { get; set; }
    public double WaitingPassengerSeconds { get; set; }
    public long AccessibilityFailures { get; set; }
    public long PlatformOverloads { get; set; }
}

public sealed record StationDefinition(string Id, string Name, double X, double Y, string Character,
    int AccessPerMinute, int PlatformCapacity, double Residential, double Jobs, double Education, double Leisure);
public sealed record LineDefinition(string Id, string Name, string Color, string[] Stations, int TravelSeconds, int HeadwaySeconds);
public sealed record AccessEdge(string StationId, string From, string To, int Seconds, int PeoplePerMinute, bool Accessible, string? EquipmentId);
public sealed record NetworkDefinition(IReadOnlyList<StationDefinition> Stations, IReadOnlyList<LineDefinition> Lines, IReadOnlyList<AccessEdge> AccessEdges);
public sealed record WorldSnapshot(WorldState World, NetworkDefinition Network, bool Stale = false, string? Warning = null);

public sealed record TrainRegistration(string TrainId, string WorkerInstanceId, string ScenarioId, int Generation);
public sealed record MovementRequest(string RequestId, string TrainId, string WorkerInstanceId, string ScenarioId, int Generation, long TrainVersion);
public sealed record MovementConfirmation(string RequestId, string TrainId, string WorkerInstanceId, string ScenarioId, int Generation, string GrantId);
public sealed class MovementGrant
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string RequestId { get; set; } = "";
    public string TrainId { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Block { get; set; } = "";
    public string Platform { get; set; } = "";
    public string? Junction { get; set; }
    public DateTimeOffset DepartsAt { get; set; }
    public DateTimeOffset ArrivesAt { get; set; }
    public bool Completed { get; set; }
}
public sealed record MovementResult(bool Granted, string Reason, MovementGrant? Grant, TrainState? Train);
public sealed record ServiceAction(string Kind, string? Target = null, string? Value = null, double? Amount = null);
public sealed record CommandEnvelope(string CommandId, string ScenarioId, int Generation, long? ExpectedVersion,
    string Actor, string Reason, ServiceAction Action);
public sealed record CommandResult(bool Accepted, string Status, string Message, long Version, string? EntityId = null);
public sealed record ScenarioRequest(long? Seed, string Preset = "normal", DateTimeOffset? StartTime = null,
    string? ScenarioId = null, int? Generation = null);
public sealed record ScenarioResetResult(string ScenarioId, int Generation, long Seed, DateTimeOffset SimTime);
public sealed record ScenarioContext(string ScenarioId, int Generation, long Version, DateTimeOffset SimTime,
    DateTimeOffset UpdatedAt, string AuthorityMode);
public sealed record StationQueueSummary(string Id, string Name, int Waiting, int PlatformWaiting, int AccessQueue,
    int AccessibleWaiting, Dictionary<string, int> CountsByState, double OldestWaitSeconds);
public sealed record ClockRequest(bool? Paused = null, double? Speed = null);
public sealed record ActionRequest(ServiceAction Action, string ScenarioId, int Generation, long? ExpectedVersion = null, string? CommandId = null, string Reason = "Operator action");
public sealed record AuthorityRequest(string Mode);

public sealed record JourneyRequest(string Origin, string Destination, bool Accessible = false, DateTimeOffset? DepartureTime = null);
public sealed class JourneyLeg
{
    public string Kind { get; set; } = "ride";
    public string? Line { get; set; }
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string? Platform { get; set; }
    public string? Destination { get; set; }
    public string Service { get; set; } = "local";
    public double WaitSeconds { get; set; }
    public double RideSeconds { get; set; }
    public double WalkSeconds { get; set; }
    public List<string> Stops { get; set; } = [];
}
public sealed class Itinerary
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "Fastest";
    public List<JourneyLeg> Legs { get; set; } = [];
    public double TotalSeconds { get; set; }
    public int Transfers { get; set; }
    public bool Accessible { get; set; }
    public DateTimeOffset ArrivesAt { get; set; }
    public string EstimateKind { get; set; } = "live";
    public List<string> Warnings { get; set; } = [];
}
public sealed record JourneyResult(string ScenarioId, int Generation, long Version, DateTimeOffset AsOf,
    JourneyRequest Request, List<Itinerary> Options, string? Error = null);
public sealed record Arrival(string TrainId, string Line, string Service, string Platform, string Destination,
    double? Seconds, string PredictionKind, string Status, int Load, int Capacity);
public sealed record StationBoard(string StationId, string ScenarioId, int Generation, DateTimeOffset AsOf,
    DateTimeOffset UpdatedAt, bool Stale, List<Arrival> Arrivals, List<string> Restrictions);
public sealed record MetricRange(double Mean, double Min, double Max);
public sealed record ForecastMetrics(MetricRange WaitingMinutes, MetricRange Completed, MetricRange DeniedBoardings,
    MetricRange Abandoned, MetricRange RemainingQueue, MetricRange OldestQueueSeconds,
    MetricRange PlatformOverloads, MetricRange AccessibilityFailures);
public sealed record ComparisonResult(string ScenarioId, int Generation, long Version, DateTimeOffset CheckpointTime,
    int HorizonMinutes, int Samples, ServiceAction Candidate, ForecastMetrics Baseline, ForecastMetrics Intervention,
    string Method, List<string> Warnings);
public sealed record CompareRequest(ServiceAction Action, string ScenarioId, int Generation);
public sealed record DemandRequest(long Seed, int StreamVersion, string Preset, DateTimeOffset From, DateTimeOffset To);
public sealed record DemandBatch(string Id, string Origin, string Destination, int Count, bool Accessible,
    DateTimeOffset At, double PatienceSeconds, DateTimeOffset? ReturnAt = null);
public sealed record Flight(string Id, string Direction, DateTimeOffset ScheduledAt, DateTimeOffset ActualAt, int Passengers, bool Cancelled, string Weather);
public sealed record DemandResponse(List<DemandBatch> Cohorts, List<Flight> Flights);
public sealed record ForecastSample(long Seed, List<DemandBatch> Demand, List<Flight> Flights);
public sealed record ChatMessage(string Role, string Content);
public sealed record AssistantJourneyContext(string Origin, string Destination, bool Accessible = false,
    DateTimeOffset? DepartureTime = null, string? SavedOutboundId = null, bool AwaitingReturnTime = false);
public sealed record AssistantRequest(string Message, List<ChatMessage>? History = null, AssistantJourneyContext? JourneyContext = null,
    string? ViewerTimeZone = null);
public sealed record ActionProposal(string Id, ServiceAction Action, string ScenarioId, int Generation,
    long Version, string Explanation, string Status = "proposed");
public sealed record AssistantResponse(string Text, JourneyResult? Journey = null, ComparisonResult? Comparison = null,
    ActionProposal? Proposal = null, List<string>? Tools = null, AssistantJourneyContext? JourneyContext = null,
    StationBoard? Arrivals = null);
public sealed record AiCapabilities(bool Text, bool Transcription, bool Speech, string Provider, string? Reason);
