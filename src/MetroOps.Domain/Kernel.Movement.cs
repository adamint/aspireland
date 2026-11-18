using MetroOps.Contracts;

namespace MetroOps.Domain;

public static partial class Kernel
{
    public static MovementResult Register(WorldState world, TrainRegistration request)
    {
        if (request.ScenarioId != world.ScenarioId) return MovementDenied("Stale scenario.");
        if (request.Generation != world.Generation) return MovementDenied("Stale generation.");
        var train = world.Trains.FirstOrDefault(t => t.Id == request.TrainId);
        if (train is null) return MovementDenied("Unknown train.");
        if (string.IsNullOrWhiteSpace(request.WorkerInstanceId)) return MovementDenied("Worker instance is required.");

        if (train.WorkerInstanceId is not null && train.WorkerInstanceId != request.WorkerInstanceId)
        {
            train.WorkerInstanceId = request.WorkerInstanceId;
            train.LastHeartbeatAt = DateTimeOffset.UtcNow;
            train.Version++;
            if (train.ActiveGrantId is not null)
            {
                train.State = "uncertain";
                train.HoldReason = "Worker takeover requires progress confirmation; occupancy retained.";
                AddHistory(world, "worker-takeover", train.Id, "consequence", train.HoldReason, train.ActiveGrantId);
            }
        }
        else
        {
            train.WorkerInstanceId = request.WorkerInstanceId;
            RefreshHeartbeat(train);
            if (train.State == "uncertain" && train.ActiveGrantId is null)
            {
                train.State = "dwelling";
                train.HoldReason = null;
                train.Version++;
            }
        }

        var active = train.ActiveGrantId is null
            ? null
            : world.Grants.Values.FirstOrDefault(g => g.Id == train.ActiveGrantId);
        return new(true, active is null ? "Registered." : "Registered; reconcile existing grant.", active, train);
    }

    public static MovementResult RequestMovement(WorldState world, MovementRequest request)
    {
        if (request.ScenarioId != world.ScenarioId) return MovementDenied("Stale scenario.");
        if (request.Generation != world.Generation) return MovementDenied("Stale generation.");
        var train = world.Trains.FirstOrDefault(t => t.Id == request.TrainId);
        if (train is null) return MovementDenied("Unknown train.");
        if (train.WorkerInstanceId != request.WorkerInstanceId) return MovementDenied("Worker identity does not own this train.", train);

        RefreshHeartbeat(train);
        if (world.Grants.TryGetValue(request.RequestId, out var existing))
        {
            if (existing.TrainId != train.Id) return MovementDenied("Request id belongs to another train.", train);
            return new(true, existing.Completed ? "Movement already confirmed." : "Existing grant.", existing, train);
        }

        if (request.TrainVersion != train.Version) return MovementDenied("Stale train version.", train);
        if (train.State != "dwelling") return MovementDenied($"Train is {train.State}; movement is not available.", train);
        if (train.ReadyAt > world.SimTime) return MovementDenied("Train is not ready to depart.", train);
        if (!string.IsNullOrEmpty(train.HoldReason)) return MovementDenied(train.HoldReason, train);
        if (HasRestriction(world, "door-fault", train.Id) || HasRestriction(world, "extended-dwell", train.Id))
        {
            return MovementDenied("An active train incident prevents departure.", train);
        }
        if (string.IsNullOrEmpty(train.Line)) return MovementDenied("Train has no line assignment.", train);
        if (RequiresTurn(train))
        {
            if (!PrepareDirection(world, train))
            {
                return MovementDenied("Reverse platform is reserved; turnback is not available.", train);
            }
            return MovementDenied("Train is turning; retry with the updated train version when ready.", train);
        }

        var line = Network.Line(train.Line);
        var nextIndex = train.RouteIndex + train.Direction;
        if (nextIndex < 0 || nextIndex >= line.Stations.Length) return MovementDenied("Train must turn before moving.", train);
        var from = train.StationId;
        var to = line.Stations[nextIndex];
        if (HasRestriction(world, "station-closure", from) || HasRestriction(world, "station-closure", to))
        {
            return MovementDenied("Station closure prevents movement.", train);
        }
        if (HasRestriction(world, "segment-closure", $"{from}>{to}") || HasRestriction(world, "segment-closure", $"{to}>{from}"))
        {
            return MovementDenied("Segment closure prevents movement.", train);
        }
        var headwayReadyAt = HeadwayReadyAt(world, train, from, to);
        if (headwayReadyAt > world.SimTime)
        {
            return MovementDenied($"Headway regulation prevents departure until {headwayReadyAt:O}.", train);
        }

        var block = Network.Block(train.Line, from, to);
        var platform = Network.Platform(to, train.Line, train.Direction);
        var junction = JunctionKey(train.Line, from, to, train.Direction);
        if (OccupiedByOther(world.BlockOccupancy, block, train.Id)) return MovementDenied($"Block {block} is reserved.", train);
        if (OccupiedByOther(world.PlatformOccupancy, platform, train.Id)) return MovementDenied($"Platform {platform} is reserved.", train);
        if (junction is not null && JunctionOccupiedByOther(world, junction, train.Id))
        {
            return MovementDenied($"Junction {junction} is reserved.", train);
        }
        if (HasRestriction(world, "platform-closure", to)) return MovementDenied("Destination platform is closed.", train);

        var travelSeconds = Network.TravelSeconds(train.Line, from, to);
        var denied = world.Cohorts
            .Where(c => c.State == "platform" && IsWaitingAt(c, world.SimTime) && c.StationId == from)
            .Where(c =>
            {
                var leg = CurrentLeg(c);
                return leg?.Line == train.Line && leg.From == from && TrainHeadsToward(train, leg.To);
            })
            .Sum(c => c.Count);
        world.Metrics.DeniedBoardings += denied;
        var grant = new MovementGrant
        {
            Id = HashId(world.Seed, $"grant:{request.RequestId}", world.Generation, (int)(train.Version & int.MaxValue)),
            RequestId = request.RequestId,
            TrainId = train.Id,
            From = from,
            To = to,
            Block = block,
            Platform = platform,
            Junction = junction,
            DepartsAt = world.SimTime,
            ArrivesAt = world.SimTime.AddSeconds(travelSeconds)
        };

        world.BlockOccupancy[block] = train.Id;
        world.PlatformOccupancy[platform] = train.Id;
        if (junction is not null) world.JunctionReservations[junction] = train.Id;
        world.Grants[request.RequestId] = grant;
        train.State = "travelling";
        train.NextStationId = to;
        train.DepartedAt = world.SimTime;
        train.ArrivesAt = grant.ArrivesAt;
        train.ActiveGrantId = grant.Id;
        train.Version++;
        AddHistory(world, "movement-granted", train.Id, "consequence", $"{from} to {to}.", request.RequestId);
        return new(true, "Granted.", grant, train);
    }

    public static MovementResult ConfirmMovement(WorldState world, MovementConfirmation request)
    {
        if (request.ScenarioId != world.ScenarioId) return MovementDenied("Stale scenario.");
        if (request.Generation != world.Generation) return MovementDenied("Stale generation.");
        if (string.IsNullOrWhiteSpace(request.RequestId)) return MovementDenied("Confirmation request id is required.");
        var train = world.Trains.FirstOrDefault(t => t.Id == request.TrainId);
        if (train is null) return MovementDenied("Unknown train.");
        if (train.WorkerInstanceId != request.WorkerInstanceId) return MovementDenied("Worker identity does not own this train.", train);
        var grant = world.Grants.Values.FirstOrDefault(g => g.Id == request.GrantId && g.TrainId == train.Id);
        if (grant is null)
        {
            return MovementDenied("Unknown movement grant.", train);
        }

        var confirmationKey = $"internal:confirmation:{request.RequestId}";
        if (world.Commands.TryGetValue(confirmationKey, out var priorConfirmation)
            && priorConfirmation.EntityId != grant.Id)
        {
            return MovementDenied("Confirmation request id belongs to another grant.", train);
        }

        RefreshHeartbeat(train);
        if (grant.Completed)
        {
            MarkConfirmation(world, confirmationKey, grant.Id);
            return new(true, "Movement already confirmed.", grant, train);
        }
        if (train.ActiveGrantId != grant.Id) return MovementDenied("Grant is not active for this train.", train);
        if (world.SimTime < grant.ArrivesAt) return MovementDenied("Movement has not reached its reserved destination.", train);

        grant.Completed = true;
        train.StationId = grant.To;
        train.RouteIndex = Array.IndexOf(Network.Line(train.Line).Stations, grant.To);
        train.NextStationId = null;
        train.State = "dwelling";
        train.ActiveGrantId = null;
        train.ArrivesAt = null;
        train.HoldReason = null;
        train.ReadyAt = world.SimTime.AddSeconds(20);
        train.Version++;

        if (world.BlockOccupancy.GetValueOrDefault(grant.Block) == train.Id) world.BlockOccupancy.Remove(grant.Block);
        if (grant.Junction is not null && world.JunctionReservations.GetValueOrDefault(grant.Junction) == train.Id)
        {
            world.JunctionReservations.Remove(grant.Junction);
        }
        foreach (var key in world.PlatformOccupancy
            .Where(p => p.Value == train.Id && p.Key != grant.Platform)
            .Select(p => p.Key).ToArray())
        {
            world.PlatformOccupancy.Remove(key);
        }
        world.PlatformOccupancy[grant.Platform] = train.Id;

        MarkConfirmation(world, confirmationKey, grant.Id);
        AddHistory(world, "movement-confirmed", train.Id, "consequence", $"Arrived at {grant.To}.", request.RequestId);
        return new(true, "Confirmed.", grant, train);
    }

    private static void MarkConfirmation(WorldState world, string key, string grantId) =>
        world.Commands[key] = new(true, "internal", "Movement confirmation accepted.", world.Version, grantId);

    private static void RefreshHeartbeat(TrainState train)
    {
        var now = DateTimeOffset.UtcNow;
        if (train.LastHeartbeatAt is null || now - train.LastHeartbeatAt.Value >= TimeSpan.FromSeconds(30))
            train.LastHeartbeatAt = now;
    }

    private static bool OccupiedByOther(Dictionary<string, string> occupancy, string key, string trainId) =>
        occupancy.TryGetValue(key, out var occupant) && occupant != trainId;

    private static bool RequiresTurn(TrainState train)
    {
        var route = Network.Line(train.Line).Stations;
        return train.ShortTurnAt == train.StationId
            || train.RouteIndex == 0 && train.Direction < 0
            || train.RouteIndex == route.Length - 1 && train.Direction > 0;
    }

    private static DateTimeOffset EffectiveReadyAt(WorldState world, TrainState train)
    {
        var readyAt = train.ReadyAt;
        if (string.IsNullOrEmpty(train.Line)) return readyAt;
        var route = Network.Line(train.Line).Stations;
        var nextIndex = train.RouteIndex + train.Direction;
        if (nextIndex < 0 || nextIndex >= route.Length) return readyAt;
        return Max(readyAt, HeadwayReadyAt(world, train, train.StationId, route[nextIndex]));
    }

    private static DateTimeOffset HeadwayReadyAt(WorldState world, TrainState train, string from, string to)
    {
        var headway = world.Headways.GetValueOrDefault(train.Line, Network.Line(train.Line).HeadwaySeconds);
        var lastDeparture = world.Grants.Values
            .Where(grant => grant.From == from && grant.To == to)
            .Where(grant => world.Trains.Any(candidate => candidate.Id == grant.TrainId && candidate.Line == train.Line))
            .Select(grant => (DateTimeOffset?)grant.DepartsAt)
            .Max();
        return lastDeparture is null ? DateTimeOffset.MinValue : lastDeparture.Value.AddSeconds(headway);
    }

    private static string? JunctionKey(string line, string from, string to, int direction)
    {
        var junction = from is "west-junction" or "east-junction"
            ? from
            : to is "west-junction" or "east-junction" ? to : null;
        return junction is null ? null : $"{junction}:{Network.Group(line)}:{(direction > 0 ? "+" : "-")}";
    }

    private static bool JunctionOccupiedByOther(WorldState world, string key, string trainId)
    {
        var conflictGroup = key[..key.LastIndexOf(':')];
        return world.JunctionReservations.Any(r => r.Key.StartsWith(conflictGroup + ":", StringComparison.Ordinal) && r.Value != trainId);
    }

    private static MovementResult MovementDenied(string reason, TrainState? train = null) => new(false, reason, null, train);
}
