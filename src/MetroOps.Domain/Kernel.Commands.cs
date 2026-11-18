using MetroOps.Contracts;

namespace MetroOps.Domain;

public static partial class Kernel
{
    private static readonly HashSet<string> IncidentKinds =
    [
        "door-fault", "extended-dwell", "train-withdrawal", "elevator-outage", "circulation",
        "platform-closure", "segment-closure", "station-closure", "demand-surge"
    ];

    public static CommandResult Apply(WorldState world, CommandEnvelope command)
    {
        if (world.Commands.TryGetValue(command.CommandId, out var previous)) return previous;
        if (string.IsNullOrWhiteSpace(command.CommandId)) return Store(world, command.CommandId, Rejected(world, "Command id is required."));
        if (command.ScenarioId != world.ScenarioId) return Store(world, command.CommandId, Rejected(world, "Stale scenario."));
        if (command.Generation != world.Generation) return Store(world, command.CommandId, Rejected(world, "Stale generation."));
        if (command.ExpectedVersion is { } expected)
        {
            if (expected > world.Version) return Store(world, command.CommandId, Rejected(world, "Expected version is in the future."));
            if (expected < world.Version - 200) return Store(world, command.CommandId, Rejected(world, "Expected version is materially stale."));
        }

        var action = command.Action;
        CommandResult result = action.Kind switch
        {
            "hold-train" => HoldTrain(world, action),
            "release-train" => ReleaseTrain(world, action),
            "withdraw-train" => WithdrawTrain(world, action),
            "deploy-reserve" => DeployReserve(world, action),
            "short-turn" => ShortTurn(world, action),
            "set-headway" => SetHeadway(world, action),
            "incident" => CreateIncident(world, action),
            "clear-incident" => ClearIncident(world, action),
            _ => Rejected(world, $"Unknown action '{action.Kind}'.")
        };

        world.Commands[command.CommandId] = result;
        AddHistory(
            world,
            result.Accepted ? "command-accepted" : "command-rejected",
            result.EntityId ?? action.Target ?? action.Kind,
            "manual",
            $"{action.Kind}: {result.Message}",
            command.CommandId);
        return result;
    }

    private static CommandResult HoldTrain(WorldState world, ServiceAction action)
    {
        var train = FindTrain(world, action.Target);
        if (train is null) return Rejected(world, "Target train was not found.");
        if (train.State is "withdrawn" or "reserve" or "travelling" or "uncertain")
        {
            return Rejected(world, $"Train cannot be held while {train.State}.");
        }
        var seconds = action.Amount ?? 120;
        if (seconds is < 1 or > 3600) return Rejected(world, "Hold duration must be between 1 and 3600 seconds.");
        train.State = "held";
        train.HoldReason = "Operator hold.";
        train.ReadyAt = Max(train.ReadyAt, world.SimTime.AddSeconds(seconds));
        train.Version++;
        return Accepted(world, "Train held.", train.Id);
    }

    private static CommandResult ReleaseTrain(WorldState world, ServiceAction action)
    {
        var train = FindTrain(world, action.Target);
        if (train is null) return Rejected(world, "Target train was not found.");
        if (train.State != "held") return Rejected(world, "Train is not held.");
        train.State = "dwelling";
        train.HoldReason = null;
        train.ReadyAt = world.SimTime;
        train.Version++;
        return Accepted(world, "Train released.", train.Id);
    }

    private static CommandResult WithdrawTrain(WorldState world, ServiceAction action)
    {
        var train = FindTrain(world, action.Target);
        if (train is null) return Rejected(world, "Target train was not found.");
        if (train.State is "travelling" or "uncertain") return Rejected(world, "Train cannot be withdrawn until its occupancy is confirmed.");
        if (train.State == "withdrawn") return Accepted(world, "Train is already withdrawn.", train.Id);
        train.State = "withdrawn";
        train.HoldReason = "Withdrawn from service.";
        train.Version++;
        return Accepted(world, "Train withdrawn.", train.Id);
    }

    private static CommandResult DeployReserve(WorldState world, ServiceAction action)
    {
        var train = FindTrain(world, action.Target);
        if (train is null) return Rejected(world, "Reserve train was not found.");
        if (train.State != "reserve") return Rejected(world, "Target is not an available reserve.");
        if (action.Value is null || !train.CompatibleLines.Contains(action.Value, StringComparer.Ordinal))
        {
            return Rejected(world, "Reserve is not compatible with the requested line.");
        }
        if (train.ReadyAt > world.SimTime) return Rejected(world, "Reserve is not ready.");

        var route = Network.Line(action.Value);
        var routeIndex = Array.IndexOf(route.Stations, train.Depot);
        if (routeIndex < 0) return Rejected(world, "Reserve depot has no physical access to the requested line.");
        var direction = routeIndex == route.Stations.Length - 1 ? -1 : 1;
        var platform = Network.Platform(train.Depot, action.Value, direction);
        if (OccupiedByOther(world.PlatformOccupancy, platform, train.Id)) return Rejected(world, "Depot platform is reserved.");

        train.Line = action.Value;
        train.StationId = train.Depot;
        train.RouteIndex = routeIndex;
        train.Direction = direction;
        train.State = "dwelling";
        train.HoldReason = null;
        train.ReadyAt = world.SimTime.AddSeconds(30 + (int)(Sample(world, "reserve-deploy", train.Id, world.SimTime.ToUnixTimeSeconds()) * 90));
        train.Version++;
        world.PlatformOccupancy[platform] = train.Id;
        return Accepted(world, $"Reserve deployed to {action.Value}.", train.Id);
    }

    private static CommandResult ShortTurn(WorldState world, ServiceAction action)
    {
        var train = FindTrain(world, action.Target);
        if (train is null) return Rejected(world, "Target train was not found.");
        if (action.Value is not "west-junction" and not "east-junction")
        {
            return Rejected(world, "Short turns are available only at west-junction or east-junction.");
        }
        if (string.IsNullOrEmpty(train.Line) || !Network.Line(train.Line).Stations.Contains(action.Value))
        {
            return Rejected(world, "Train cannot reach that turnback.");
        }
        if (train.State is "reserve" or "withdrawn" or "uncertain") return Rejected(world, $"Train cannot short-turn while {train.State}.");
        train.ShortTurnAt = action.Value;
        train.Version++;
        return Accepted(world, $"Train will short-turn at {action.Value}.", train.Id);
    }

    private static CommandResult SetHeadway(WorldState world, ServiceAction action)
    {
        if (action.Target is null || !Network.Definition.Lines.Any(l => l.Id == action.Target))
        {
            return Rejected(world, "Target line was not found.");
        }
        if (action.Amount is not { } requested || !double.IsFinite(requested) || requested is < 120 or > 3600)
            return Rejected(world, "Departure regulation floor must be between 120 and 3600 seconds.");
        var fleet = world.Trains.Count(t => t.Line == action.Target && t.State is not "reserve" and not "withdrawn");
        if (fleet == 0) return Rejected(world, "Line has no active fleet.");
        world.Headways[action.Target] = requested;
        return Accepted(world, $"Departure regulation floor set to {requested:0} seconds; actual service spacing may be longer.", action.Target);
    }

    private static CommandResult CreateIncident(WorldState world, ServiceAction action)
    {
        if (action.Value is null || !IncidentKinds.Contains(action.Value)) return Rejected(world, "Incident kind is invalid.");
        if (string.IsNullOrWhiteSpace(action.Target)) return Rejected(world, "Incident target is required.");
        if (!ValidIncidentTarget(world, action.Value, action.Target)) return Rejected(world, "Incident target is invalid for this kind.");
        var duration = action.Amount ?? 600;
        if (duration is < 1 or > 86_400) return Rejected(world, "Incident duration must be between 1 and 86400 seconds.");

        var incident = OpenIncident(
            world,
            action.Value,
            action.Target,
            duration,
            "manual",
            $"{action.Value} at {action.Target}.");
        if (action.Value == "train-withdrawal")
        {
            var train = FindTrain(world, action.Target)!;
            if (train.State is "travelling" or "uncertain")
            {
                incident.State = "pending";
                incident.Description += " Awaiting confirmed occupancy.";
            }
            else
            {
                train.State = "withdrawn";
                train.HoldReason = incident.Description;
                train.Version++;
            }
        }
        if (action.Value == "demand-surge") AddDemandSurge(world, action.Target, incident.Id);
        return Accepted(world, "Incident opened.", incident.Id);
    }

    private static CommandResult ClearIncident(WorldState world, ServiceAction action)
    {
        var incident = world.Incidents.FirstOrDefault(i => i.Id == action.Target);
        if (incident is null) return Rejected(world, "Incident was not found.");
        if (incident.State == "cleared") return Accepted(world, "Incident is already cleared.", incident.Id);
        incident.State = "cleared";
        incident.EndsAt = world.SimTime;
        ClearIncidentEffects(world, incident);
        return Accepted(world, "Incident cleared.", incident.Id);
    }

    private static bool ValidIncidentTarget(WorldState world, string kind, string target)
    {
        if (kind is "door-fault" or "extended-dwell" or "train-withdrawal") return FindTrain(world, target) is not null;
        if (kind == "segment-closure")
        {
            var parts = target.Split('>', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) return false;
            return Network.Definition.Lines.Any(l =>
            {
                var from = Array.IndexOf(l.Stations, parts[0]);
                var to = Array.IndexOf(l.Stations, parts[1]);
                return from >= 0 && to >= 0 && Math.Abs(from - to) == 1;
            });
        }
        return Network.Definition.Stations.Any(s => s.Id == target);
    }

    private static void AddDemandSurge(WorldState world, string station, string incidentId)
    {
        var destinations = Network.Definition.Stations.Where(s => s.Id != station).OrderBy(s => s.Id).ToArray();
        var destination = destinations[(int)(Sample(world, "demand-surge", station, world.SimTime.UtcTicks) * destinations.Length)];
        var count = world.Preset == "rough" ? 180 : 120;
        var route = Plan(world, new(station, destination.Id)).Options.FirstOrDefault();
        world.Cohorts.Add(new Cohort
        {
            Id = $"{incidentId}:surge",
            Origin = station,
            Destination = destination.Id,
            StationId = station,
            Count = count,
            State = "outside",
            CreatedAt = world.SimTime,
            ReadyAt = world.SimTime,
            PatienceSeconds = 1800,
            Legs = route?.Legs.Where(l => l.Kind == "ride").Select(Wire.Clone).ToList() ?? []
        });
        world.Metrics.Generated += count;
    }

    private static TrainState? FindTrain(WorldState world, string? id) =>
        id is null ? null : world.Trains.FirstOrDefault(t => t.Id == id);

    private static CommandResult Accepted(WorldState world, string message, string? entityId = null) =>
        new(true, "accepted", message, world.Version + 1, entityId);

    private static CommandResult Rejected(WorldState world, string message) =>
        new(false, "rejected", message, world.Version + 1);

    private static CommandResult Store(WorldState world, string key, CommandResult result)
    {
        if (!string.IsNullOrWhiteSpace(key)) world.Commands[key] = result;
        return result;
    }
}
