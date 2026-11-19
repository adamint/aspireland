namespace MetroOps.Contracts;

// Keep rider field names aligned with WorldState so queued legacy snapshots can still be read.
public sealed record RiderWorld(
    string ScenarioId, int Generation, long Version, DateTimeOffset SimTime, DateTimeOffset UpdatedAt,
    bool Paused, double Speed, IReadOnlyList<RiderTrain> Trains, IReadOnlyList<RiderQueue> Cohorts,
    IReadOnlyList<RiderIncident> Incidents, IReadOnlyDictionary<string, RiderGrant> Grants,
    IReadOnlyDictionary<string, double> Headways)
{
    public WorldState ToWorldState() => new()
    {
        ScenarioId = ScenarioId, Generation = Generation, Version = Version, SimTime = SimTime,
        UpdatedAt = UpdatedAt, Paused = Paused, Speed = Speed,
        Trains = Trains.Select(t => new TrainState
        {
            Id = t.Id, Line = t.Line, State = t.State, StationId = t.StationId, NextStationId = t.NextStationId,
            Direction = t.Direction, RouteIndex = t.RouteIndex, Capacity = t.Capacity, Load = t.Load,
            HoldReason = t.HoldReason, ReadyAt = t.ReadyAt, DepartedAt = t.DepartedAt, ArrivesAt = t.ArrivesAt,
            ActiveGrantId = t.ActiveGrantId, ShortTurnAt = t.ShortTurnAt
        }).ToList(),
        Cohorts = Cohorts.Select(c => new Cohort
        {
            StationId = c.StationId, State = c.State, Accessible = c.Accessible, Count = c.Count, ReadyAt = c.ReadyAt
        }).ToList(),
        Incidents = Incidents.Select(i => new Incident
        {
            Id = i.Id, Kind = i.Kind, Target = i.Target, State = i.State, Description = i.Description,
            StartedAt = i.StartedAt, EndsAt = i.EndsAt
        }).ToList(),
        Grants = Grants.ToDictionary(entry => entry.Key, entry => new MovementGrant
        {
            Id = entry.Value.Id, TrainId = entry.Value.TrainId, From = entry.Value.From, To = entry.Value.To,
            DepartsAt = entry.Value.DepartsAt, ArrivesAt = entry.Value.ArrivesAt, Completed = entry.Value.Completed
        }),
        Headways = new(Headways)
    };
}

public sealed record RiderTrain(
    string Id, string Line, string State, string StationId, string? NextStationId, int Direction, int RouteIndex,
    int Capacity, int Load, string? HoldReason, DateTimeOffset ReadyAt, DateTimeOffset? DepartedAt,
    DateTimeOffset? ArrivesAt, string? ActiveGrantId, string? ShortTurnAt);

public sealed record RiderQueue(string StationId, string State, bool Accessible, int Count, DateTimeOffset ReadyAt);

public sealed record RiderIncident(
    string Id, string Kind, string Target, string State, string Description, DateTimeOffset StartedAt,
    DateTimeOffset? EndsAt = null);

public sealed record RiderGrant(
    string Id, string TrainId, string From, string To, DateTimeOffset DepartsAt, DateTimeOffset ArrivesAt, bool Completed);
