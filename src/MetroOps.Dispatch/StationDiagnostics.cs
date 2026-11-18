using MetroOps.Contracts;
using MetroOps.Domain;

namespace MetroOps.Dispatch;

public static class StationDiagnostics
{
    private const int EvidenceLimit = 12;

    public static StationDiagnosis Observe(WorldState world, StationDefinition station, DateTimeOffset now)
    {
        if (now - world.UpdatedAt > TimeSpan.FromSeconds(15))
            return new(station.Id, station.Name, world.ScenarioId, world.Generation, world.Version, world.SimTime, world.UpdatedAt,
                "stale", "insufficient-evidence", null, 0, 0, 0, 0, 0, [], 0, [], 0, []);

        var local = world.Cohorts.Where(c => c.StationId == station.Id && c.ReadyAt <= world.SimTime).ToArray();
        var waiting = local.Where(c => Kernel.IsWaitingAt(c, world.SimTime)).ToArray();
        var queue = new StationQueueObservation(
            waiting.Sum(c => c.Count),
            waiting.Where(c => c.State == "platform").Sum(c => c.Count),
            waiting.Where(c => c.State == "outside").Sum(c => c.Count),
            local.Where(c => c.State == "transfer").Sum(c => c.Count),
            station.PlatformCapacity);
        var trains = world.Trains.Where(t => t.State is not ("reserve" or "withdrawn") &&
            (t.StationId == station.Id || t.NextStationId == station.Id)).ToArray();
        var restrictions = world.Incidents.Where(i => i.State == "active" && i.Kind != "demand-surge" &&
            (Kernel.IncidentAffectsStation(i, station.Id) || trains.Any(t => t.Id == i.Target))).ToArray();
        var adjacentBlocks = Network.Definition.Lines.SelectMany(line => line.Stations.Zip(line.Stations.Skip(1))
            .Where(pair => pair.First == station.Id || pair.Second == station.Id)
            .SelectMany(pair => new[] { Network.Block(line.Id, pair.First, pair.Second), Network.Block(line.Id, pair.Second, pair.First) }))
            .ToHashSet(StringComparer.Ordinal);
        var reservations = world.BlockOccupancy.Where(entry => adjacentBlocks.Contains(entry.Key)).ToArray();
        var fullCount = trains.Count(t => t.Load >= t.Capacity && t.Capacity > 0);
        var heldCount = trains.Count(t => t.State == "held" || !string.IsNullOrEmpty(t.HoldReason));
        var assessment = queue.Waiting > 0 && (heldCount > 0 || fullCount > 0 || restrictions.Length > 0)
            ? "possible-contributors" : "insufficient-evidence";

        return new(station.Id, station.Name, world.ScenarioId, world.Generation, world.Version, world.SimTime, world.UpdatedAt,
            "fresh", assessment, queue, trains.Length, trains.Sum(t => t.Load), trains.Sum(t => t.Capacity), fullCount, heldCount,
            trains.OrderByDescending(t => t.State == "held" || !string.IsNullOrEmpty(t.HoldReason)).Take(EvidenceLimit)
                .Select(t => new StationTrainObservation(t.Id, t.Line, t.State, t.Load, t.Capacity,
                    t.HoldReason is { Length: > 160 } reason ? reason[..160] : t.HoldReason)).ToArray(),
            restrictions.Length, restrictions.Take(EvidenceLimit).Select(i => new StationRestrictionObservation(i.Kind, i.Target)).ToArray(),
            reservations.Length, reservations.Take(EvidenceLimit).Select(entry => new StationBlockObservation(entry.Key, entry.Value)).ToArray());
    }
}
