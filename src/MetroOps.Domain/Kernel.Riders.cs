using MetroOps.Contracts;

namespace MetroOps.Domain;

public static partial class Kernel
{
    public static RiderWorld ProjectRiders(WorldState world)
    {
        var trains = world.Trains.Select(t => new RiderTrain(t.Id, t.Line, t.State, t.StationId, t.NextStationId,
            t.Direction, t.RouteIndex, t.Capacity, t.Load, t.HoldReason, t.ReadyAt, t.DepartedAt, t.ArrivesAt,
            t.ActiveGrantId, t.ShortTurnAt)).ToArray();
        var queues = world.Cohorts.Where(c => IsWaitingAt(c, world.SimTime))
            .GroupBy(c => (c.StationId, c.State, c.Accessible))
            .Select(g => new RiderQueue(g.Key.StationId, g.Key.State, g.Key.Accessible,
                g.Sum(c => c.Count), g.Min(c => c.ReadyAt))).ToArray();
        var incidents = world.Incidents.Where(i => i.State == "active")
            .Select(i => new RiderIncident(i.Id, i.Kind, i.Target, i.State, i.Description, i.StartedAt, i.EndsAt)).ToArray();

        return new RiderWorld(world.ScenarioId, world.Generation, world.Version, world.SimTime, world.UpdatedAt,
            world.Paused, world.Speed, trains, queues, incidents, RiderGrants(world), new Dictionary<string, double>(world.Headways));
    }

    private static Dictionary<string, RiderGrant> RiderGrants(WorldState world)
    {
        var activeIds = world.Trains.ToDictionary(t => t.Id, t => t.ActiveGrantId);
        var grants = new Dictionary<string, RiderGrant>();
        var departures = new Dictionary<(string Train, string From, string To), MovementGrant>();
        var headwayWindow = TimeSpan.FromSeconds(MaxHeadwaySeconds);
        foreach (var grant in world.Grants.Values)
        {
            if (!activeIds.TryGetValue(grant.TrainId, out var activeId)) continue;
            if (!grant.Completed && grant.Id == activeId)
                grants.TryAdd(grant.Id, ToRiderGrant(grant));

            // Board clamps elapsed waits to zero; departures at or before this cutoff cannot affect an ETA.
            if (world.SimTime - grant.DepartsAt >= headwayWindow) continue;
            var key = (grant.TrainId, grant.From, grant.To);
            if (!departures.TryGetValue(key, out var previous) || grant.DepartsAt > previous.DepartsAt)
                departures[key] = grant;
        }
        foreach (var grant in departures.Values)
            grants.TryAdd(grant.Id, ToRiderGrant(grant));
        return grants;
    }

    private static RiderGrant ToRiderGrant(MovementGrant grant) =>
        new(grant.Id, grant.TrainId, grant.From, grant.To, grant.DepartsAt, grant.ArrivesAt, grant.Completed);
}
