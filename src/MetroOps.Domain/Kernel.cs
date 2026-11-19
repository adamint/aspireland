using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using MetroOps.Contracts;

namespace MetroOps.Domain;

public static partial class Kernel
{
    private const int HistoryLimit = 250;
    public static WorldState Create(long seed = 20261117, string preset = "normal", DateTimeOffset? startTime = null)
    {
        if (preset is not "normal" and not "rough")
        {
            throw new ArgumentOutOfRangeException(nameof(preset), "Preset must be normal or rough.");
        }

        var world = new WorldState
        {
            ScenarioId = Guid.NewGuid().ToString("N"),
            Seed = seed,
            Preset = preset,
            SimTime = startTime ?? new DateTimeOffset(2026, 11, 17, 12, 30, 0, TimeSpan.Zero),
            UpdatedAt = DateTimeOffset.UtcNow,
            Headways = Network.Definition.Lines.ToDictionary(line => line.Id, line => (double)line.HeadwaySeconds)
        };

        foreach (var definition in Fleet.Trains) AddTrain(world, definition);

        var description = startTime is null
            ? $"Tuesday peak ({preset}; 07:30 America/New_York)."
            : $"Scenario created ({preset}; explicit start time).";
        AddHistory(world, "scenario-created", world.ScenarioId, "scripted", description, "create");
        return world;
    }

    public static void Tick(WorldState world, IReadOnlyList<DemandBatch> demand, int seconds, bool driveTrains = false)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(demand);
        if (seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (world.Paused)
        {
            DetectStaleWorkers(world);
            return;
        }

        var orderedDemand = demand.OrderBy(d => d.At).ThenBy(d => d.Id, StringComparer.Ordinal).ToArray();
        var nextDemand = 0;
        void AddDueDemand()
        {
            while (nextDemand < orderedDemand.Length && orderedDemand[nextDemand].At <= world.SimTime)
            {
                AddDemandBatch(world, orderedDemand[nextDemand]);
                nextDemand++;
            }
        }

        AddDueDemand();
        for (var second = 0; second < seconds; second++)
        {
            world.SimTime = world.SimTime.AddSeconds(1);
            AddDueDemand();
            ExpireIncidents(world);
            DetectStaleWorkers(world);
            ProcessStochasticOpportunities(world);
            ProcessPassengerSecond(world);
            if (driveTrains) DriveTrainSecond(world);
        }
    }

    public static double DeterministicUnit(long seed, string stream, string entity, long opportunity)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}|{stream}|{entity}|{opportunity}"));
        var value = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        return (value >> 11) * (1.0 / (1UL << 53));
    }

    public static bool IsWaitingAt(Cohort cohort, DateTimeOffset simTime)
    {
        ArgumentNullException.ThrowIfNull(cohort);
        return cohort.State is "outside" or "platform" && cohort.ReadyAt <= simTime;
    }

    private static double Sample(WorldState world, string stream, string entity, long opportunity) =>
        DeterministicUnit(world.Seed, $"{stream}:v{world.StreamVersion}", entity, opportunity);

    private static void AddTrain(WorldState world, FleetTrain definition)
    {
        var train = new TrainState
        {
            Id = definition.Id,
            Line = definition.Line,
            State = definition.Reserve ? "reserve" : "dwelling",
            StationId = definition.StationId,
            Direction = definition.Direction,
            RouteIndex = definition.RouteIndex,
            Capacity = definition.Capacity,
            Depot = definition.Depot,
            CompatibleLines = definition.CompatibleLines.ToArray(),
            ReadyAt = world.SimTime.AddSeconds(definition.Reserve
                ? ReserveReadiness(world, definition.Id)
                : 20 + (int)(Sample(world, "initial-dwell", definition.Id, 0) * 40))
        };
        world.Trains.Add(train);
        if (!definition.Reserve)
            world.PlatformOccupancy[Network.Platform(train.StationId, train.Line, train.Direction)] = train.Id;
    }

    private static int ReserveReadiness(WorldState world, string trainId) =>
        90 + (int)(Sample(world, "reserve-readiness", trainId, 0) * 180);

    private static void AddDemandBatch(WorldState world, DemandBatch batch)
    {
        var key = $"demand:{batch.Id}";
        if (world.Commands.ContainsKey(key)) return;
        world.Commands[key] = new(true, "accepted", "Demand accepted.", world.Version, batch.Id);
        if (batch.Count <= 0 || Network.Resolve(batch.Origin) is not { } origin || Network.Resolve(batch.Destination) is not { } destination)
        {
            AddHistory(world, "demand-rejected", batch.Id, "scripted", "Demand batch was invalid.", batch.Id);
            return;
        }

        var request = new JourneyRequest(origin, destination, batch.Accessible, batch.At);
        var route = Plan(world, request).Options.FirstOrDefault();
        var cohort = new Cohort
        {
            Id = batch.Id,
            Origin = origin,
            Destination = destination,
            StationId = origin,
            Count = batch.Count,
            Accessible = batch.Accessible,
            State = "outside",
            CreatedAt = batch.At,
            ReadyAt = batch.At,
            PatienceSeconds = Math.Max(60, batch.PatienceSeconds),
            ReturnAt = batch.ReturnAt,
            Legs = route?.Legs.Where(l => l.Kind == "ride").Select(Wire.Clone).ToList() ?? []
        };
        world.Cohorts.Add(cohort);
        world.Metrics.Generated += batch.Count;
        AddHistory(world, "demand-generated", batch.Id, "scripted", $"{batch.Count} passengers {origin} to {destination}.", batch.Id);
    }

    private static void ProcessPassengerSecond(WorldState world)
    {
        var intervalStart = world.SimTime.AddSeconds(-1);
        foreach (var cohort in world.Cohorts)
        {
            if (cohort.State is not ("outside" or "platform" or "transfer")) continue;
            if (cohort.ReadyAt >= world.SimTime) continue;
            var waited = (world.SimTime - Max(intervalStart, cohort.ReadyAt)).TotalSeconds;
            cohort.ElapsedWaitSeconds += waited;
            world.Metrics.WaitingPassengerSeconds += waited * cohort.Count;
            if (cohort.ElapsedWaitSeconds < cohort.PatienceSeconds) continue;
            cohort.State = "abandoned";
            cohort.TrainId = null;
            world.Metrics.Abandoned += cohort.Count;
            AddHistory(world, "passengers-abandoned", cohort.Id, "consequence", $"{cohort.Count} passengers abandoned.", cohort.Id);
        }

        ProcessAccess(world);
        var onboardByTrain = world.Cohorts
            .Where(c => c.State == "onboard" && c.TrainId is not null)
            .GroupBy(c => c.TrainId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var platformByStation = world.Cohorts
            .Where(c => c.State == "platform" && IsWaitingAt(c, world.SimTime))
            .GroupBy(c => c.StationId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id, StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
        foreach (var train in world.Trains.Where(t => t.State == "dwelling").OrderBy(t => t.Id, StringComparer.Ordinal))
        {
            ExchangePassengers(
                world,
                train,
                onboardByTrain.GetValueOrDefault(train.Id) ?? [],
                platformByStation.GetValueOrDefault(train.StationId) ?? []);
        }

        var platformCounts = world.Cohorts
            .Where(c => c.State == "platform" && IsWaitingAt(c, world.SimTime))
            .GroupBy(c => c.StationId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(c => c.Count), StringComparer.Ordinal);
        foreach (var station in Network.Definition.Stations)
        {
            var waiting = platformCounts.GetValueOrDefault(station.Id);
            if (waiting > station.PlatformCapacity) world.Metrics.PlatformOverloads++;
        }
    }

    private static void ProcessAccess(WorldState world)
    {
        var candidates = world.Cohorts
            .Where(c => c.State is "outside" or "transfer")
            .GroupBy(c => (c.StationId, c.Accessible, c.State))
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id, StringComparer.Ordinal).ToArray());
        var platformCounts = world.Cohorts
            .Where(c => c.State == "platform" && IsWaitingAt(c, world.SimTime))
            .GroupBy(c => c.StationId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(c => c.Count), StringComparer.Ordinal);

        foreach (var station in Network.Definition.Stations)
        {
            if (HasRestriction(world, "station-closure", station.Id) || HasRestriction(world, "platform-closure", station.Id)) continue;
            var platformFree = station.PlatformCapacity - platformCounts.GetValueOrDefault(station.Id);
            if (platformFree <= 0) continue;

            foreach (var accessible in new[] { false, true })
            {
                if (accessible && ElevatorUnavailable(world, station.Id))
                {
                    foreach (var state in new[] { "outside", "transfer" })
                    {
                        if (!candidates.TryGetValue((station.Id, true, state), out var inaccessible)) continue;
                        foreach (var cohort in inaccessible)
                        {
                            if (HasMarker(world, "accessibility-failure", cohort.Id)) continue;
                            world.Metrics.AccessibilityFailures += cohort.Count;
                            Mark(world, "accessibility-failure", cohort.Id);
                            AddHistory(world, "accessibility-failure", cohort.Id, "consequence", $"No accessible path at {station.Id}.", cohort.Id);
                        }
                    }
                    continue;
                }

                foreach (var state in new[] { "outside", "transfer" })
                {
                    var rate = accessible ? 24 : state == "transfer" ? 100 : Math.Min(station.AccessPerMinute, 120);
                    if (HasRestriction(world, "circulation", station.Id)) rate = Math.Max(1, rate / 2);
                    var capacity = Math.Min(CapacityThisSecond(rate, world.SimTime), platformFree);
                    if (capacity <= 0) continue;

                    if (!candidates.TryGetValue((station.Id, accessible, state), out var waiting)) continue;
                    foreach (var cohort in waiting)
                    {
                        if (cohort.State != state || cohort.ReadyAt > world.SimTime) continue;
                        if (cohort.Legs.Count == 0) continue;
                        var moved = Math.Min(capacity, cohort.Count);
                        MovePart(world, cohort, moved, "platform", null);
                        capacity -= moved;
                        platformFree -= moved;
                        if (capacity == 0 || platformFree == 0) break;
                    }
                    if (platformFree == 0) break;
                }
            }
        }
    }

    private static int CapacityThisSecond(int perMinute, DateTimeOffset time)
    {
        var second = time.ToUnixTimeSeconds();
        return (int)(Math.Floor((second + 1) * perMinute / 60d) - Math.Floor(second * perMinute / 60d));
    }

    private static void ExchangePassengers(
        WorldState world,
        TrainState train,
        IReadOnlyList<Cohort> onboard,
        IReadOnlyList<Cohort> platform)
    {
        if (string.IsNullOrEmpty(train.Line) || HasRestriction(world, "station-closure", train.StationId)) return;
        var exchangeKey = $"exchange:{train.Id}:{train.StationId}:{train.Version}";
        var alighted = 0;
        var firstExchange = !HasMarker(world, "stop-exchange", exchangeKey);
        if (firstExchange)
        {
            foreach (var cohort in onboard)
            {
                if (cohort.State != "onboard" || cohort.TrainId != train.Id) continue;
                var leg = CurrentLeg(cohort);
                if (leg?.To != train.StationId) continue;
                train.Load -= cohort.Count;
                alighted += cohort.Count;
                cohort.TrainId = null;
                cohort.StationId = train.StationId;
                cohort.LegIndex++;
                if (cohort.LegIndex >= cohort.Legs.Count)
                {
                    CompleteCohort(world, cohort);
                }
                else
                {
                    cohort.State = "transfer";
                    cohort.ReadyAt = world.SimTime.AddSeconds(cohort.Accessible ? 130 : 100);
                }
            }
        }

        var boarded = 0;
        var available = Math.Max(0, train.Capacity - train.Load);
        if (available > 0 && !HasRestriction(world, "platform-closure", train.StationId))
        {
            foreach (var cohort in platform)
            {
                if (cohort.State != "platform" || !IsWaitingAt(cohort, world.SimTime) || cohort.StationId != train.StationId) continue;
                if (!CanBoard(train, CurrentLeg(cohort))) continue;
                var count = Math.Min(available, cohort.Count);
                MovePart(world, cohort, count, "onboard", train.Id);
                train.Load += count;
                boarded += count;
                available -= count;
                if (available == 0) break;
            }
        }

        if (firstExchange)
        {
            var residual = 5 + (int)(Sample(world, "dwell", train.Id, world.SimTime.ToUnixTimeSeconds()) * 16);
            var dwell = Math.Max(20, residual + (int)Math.Ceiling(alighted * .2 + boarded * .15));
            if (HasRestriction(world, "extended-dwell", train.Id) || HasRestriction(world, "door-fault", train.Id)) dwell += 90;
            train.ReadyAt = Max(train.ReadyAt, world.SimTime.AddSeconds(dwell));
            Mark(world, "stop-exchange", exchangeKey);
            AddHistory(world, "stop-exchange", exchangeKey, "consequence", $"{alighted} alighted, {boarded} boarded.", exchangeKey);
        }
        else if (boarded > 0)
        {
            train.ReadyAt = train.ReadyAt.AddSeconds(Math.Ceiling(boarded * .15));
        }
    }

    private static void CompleteCohort(WorldState world, Cohort cohort)
    {
        cohort.State = "completed";
        world.Metrics.Completed += cohort.Count;
        AddHistory(world, "journey-completed", cohort.Id, "consequence", $"{cohort.Count} passengers completed.", cohort.Id);
        if (cohort.ReturnAt is not { } returnAt || cohort.ReturnScheduled) return;
        cohort.ReturnScheduled = true;
        var returnId = $"{cohort.Id}:return";
        world.Cohorts.Add(new Cohort
        {
            Id = returnId,
            Origin = cohort.Destination,
            Destination = cohort.Origin,
            StationId = cohort.Destination,
            Count = cohort.Count,
            Accessible = cohort.Accessible,
            State = "outside",
            CreatedAt = returnAt,
            ReadyAt = returnAt,
            PatienceSeconds = cohort.PatienceSeconds,
            Legs = Plan(world, new(cohort.Destination, cohort.Origin, cohort.Accessible, returnAt))
                .Options.FirstOrDefault()?.Legs.Where(l => l.Kind == "ride").Select(Wire.Clone).ToList() ?? []
        });
        world.Metrics.Generated += cohort.Count;
    }

    private static void MovePart(WorldState world, Cohort cohort, int count, string state, string? trainId)
    {
        if (count <= 0) return;
        if (count == cohort.Count)
        {
            cohort.State = state;
            cohort.TrainId = trainId;
            return;
        }

        cohort.Count -= count;
        var split = Wire.Clone(cohort);
        split.Id = $"{cohort.Id}:split:{HashId(world.Seed, cohort.Id, world.SimTime.UtcTicks, cohort.Count)}";
        split.Count = count;
        split.State = state;
        split.TrainId = trainId;
        world.Cohorts.Add(split);
    }

    private static JourneyLeg? CurrentLeg(Cohort cohort) =>
        cohort.LegIndex >= 0 && cohort.LegIndex < cohort.Legs.Count ? cohort.Legs[cohort.LegIndex] : null;

    private static bool CanBoard(TrainState train, JourneyLeg? leg) =>
        leg?.Line is { } line && leg.From == train.StationId
        && Network.Group(line) == Network.Group(train.Line)
        && TrainHeadsToward(train, leg.To);

    private static bool TrainHeadsToward(TrainState train, string destination)
    {
        var route = Network.Line(train.Line).Stations;
        var destinationIndex = Array.IndexOf(route, destination);
        return destinationIndex >= 0
            && Math.Sign(destinationIndex - train.RouteIndex) == train.Direction
            && (ServiceDestinationIndex(train) - destinationIndex) * train.Direction >= 0;
    }

    private static void DriveTrainSecond(WorldState world)
    {
        foreach (var train in world.Trains.OrderBy(t => t.Id, StringComparer.Ordinal))
        {
            if (train.State == "travelling" && train.ActiveGrantId is { } activeId)
            {
                var grant = world.Grants.Values.FirstOrDefault(g => g.Id == activeId);
                if (grant is not null && grant.ArrivesAt <= world.SimTime && train.WorkerInstanceId is { } worker)
                {
                    ConfirmMovement(world, new(grant.RequestId, train.Id, worker, world.ScenarioId, world.Generation, grant.Id));
                }
                continue;
            }

            if (train.State != "dwelling" || EffectiveReadyAt(world, train) > world.SimTime) continue;
            var workerId = train.WorkerInstanceId ?? $"headless:{train.Id}";
            if (train.WorkerInstanceId is null)
            {
                Register(world, new(train.Id, workerId, world.ScenarioId, world.Generation));
            }

            var requestId = $"{train.Id}:{train.Version}:{world.SimTime.ToUnixTimeSeconds()}";
            RequestMovement(world, new(requestId, train.Id, workerId, world.ScenarioId, world.Generation, train.Version));
        }
    }

    private static bool PrepareDirection(WorldState world, TrainState train)
    {
        var route = Network.Line(train.Line).Stations;
        var atEnd = train.RouteIndex == 0 && train.Direction < 0 || train.RouteIndex == route.Length - 1 && train.Direction > 0;
        var atShortTurn = train.ShortTurnAt == train.StationId;
        if (!atEnd && !atShortTurn) return false;
        var reversePlatform = Network.Platform(train.StationId, train.Line, -train.Direction);
        if (OccupiedByOther(world.PlatformOccupancy, reversePlatform, train.Id)) return false;
        if (atShortTurn) TransferTurnbackPassengers(world, train);
        train.Direction *= -1;
        train.ShortTurnAt = null;
        train.Version++;
        var delay = 35 + (int)(Sample(world, "turnback", train.Id, world.SimTime.ToUnixTimeSeconds()) * 50);
        train.ReadyAt = world.SimTime.AddSeconds(delay);
        ReplacePlatformDirection(world, train);
        AddHistory(world, "train-turned", train.Id, "consequence", $"Turned at {train.StationId}.", train.Id);
        return true;
    }

    private static void TransferTurnbackPassengers(WorldState world, TrainState train)
    {
        var onboard = world.Cohorts.Where(c => c.State == "onboard" && c.TrainId == train.Id).ToArray();
        ExchangePassengers(world, train, onboard, []);
        var transferred = 0;
        var route = Network.Line(train.Line).Stations;
        foreach (var cohort in onboard.Where(c => c.State == "onboard" && c.TrainId == train.Id))
        {
            train.Load -= cohort.Count;
            transferred += cohort.Count;
            cohort.TrainId = null;
            cohort.StationId = train.StationId;
            cohort.State = "transfer";
            cohort.ReadyAt = world.SimTime.AddSeconds(cohort.Accessible ? 130 : 100);
            if (CurrentLeg(cohort) is not { } leg) continue;
            var to = Array.IndexOf(route, leg.To);
            leg.From = train.StationId;
            leg.Platform = Network.Platform(train.StationId, train.Line, train.Direction);
            if (to < 0) continue;
            leg.Stops = to >= train.RouteIndex
                ? [.. route[train.RouteIndex..(to + 1)]]
                : [.. route[to..(train.RouteIndex + 1)].Reverse()];
        }
        if (transferred > 0)
        {
            AddHistory(world, "short-turn-transfer", train.Id, "consequence",
                $"{transferred} passengers require another service at {train.StationId}.", train.Id);
        }
    }

    private static void ReplacePlatformDirection(WorldState world, TrainState train)
    {
        foreach (var key in world.PlatformOccupancy.Where(p => p.Value == train.Id).Select(p => p.Key).ToArray())
        {
            world.PlatformOccupancy.Remove(key);
        }
        world.PlatformOccupancy[Network.Platform(train.StationId, train.Line, train.Direction)] = train.Id;
    }

    private static void DetectStaleWorkers(WorldState world)
    {
        var wallNow = DateTimeOffset.UtcNow;
        foreach (var train in world.Trains.Where(t => t.LastHeartbeatAt is not null && t.State is "travelling" or "dwelling" or "held"))
        {
            if ((wallNow - train.LastHeartbeatAt!.Value).TotalSeconds <= 90) continue;
            train.State = "uncertain";
            train.HoldReason = "Worker heartbeat stale; occupancy retained.";
            train.Version++;
            AddHistory(world, "train-uncertain", train.Id, "consequence", train.HoldReason, train.ActiveGrantId ?? train.Id);
        }
    }

    private static void ExpireIncidents(WorldState world)
    {
        foreach (var train in world.Trains.Where(t => t.State == "held" && t.HoldReason == "Operator hold." && t.ReadyAt <= world.SimTime))
        {
            train.State = "dwelling";
            train.HoldReason = null;
            train.Version++;
            AddHistory(world, "train-released", train.Id, "consequence", "Timed hold completed.", train.Id);
        }

        foreach (var incident in world.Incidents.Where(i => i.State == "pending" && i.Kind == "train-withdrawal").ToArray())
        {
            var train = world.Trains.FirstOrDefault(t => t.Id == incident.Target);
            if (train is null || train.State is "travelling" or "uncertain") continue;
            incident.State = "active";
            train.State = "withdrawn";
            train.HoldReason = incident.Description;
            train.Version++;
        }

        foreach (var incident in world.Incidents.Where(i => i.State is "active" or "pending" && i.EndsAt <= world.SimTime).ToArray())
        {
            incident.State = "cleared";
            ClearIncidentEffects(world, incident);
            AddHistory(world, "incident-cleared", incident.Id, incident.Source, $"{incident.Kind} cleared.", incident.Id);
        }
    }

    private static void ClearIncidentEffects(WorldState world, Incident incident)
    {
        var train = world.Trains.FirstOrDefault(t => t.Id == incident.Target);
        if (incident.Kind is "door-fault" or "extended-dwell")
        {
            if (train is not null && train.HoldReason == incident.Description)
            {
                train.HoldReason = null;
                train.ReadyAt = world.SimTime;
            }
        }
        else if (incident.Kind == "train-withdrawal"
            && train is not null
            && train.State == "withdrawn"
            && train.HoldReason == incident.Description)
        {
            train.State = "dwelling";
            train.HoldReason = null;
            train.ReadyAt = world.SimTime;
            train.Version++;
        }
    }

    private static void ProcessStochasticOpportunities(WorldState world)
    {
        var opportunity = world.SimTime.ToUnixTimeSeconds();
        var multiplier = world.Preset == "rough" ? 3d : 1d;
        foreach (var train in world.Trains.Where(t => t.State is not "reserve" and not "withdrawn"))
        {
            if (Sample(world, "train-failure-profile-2", train.Id, opportunity) >= multiplier / 6_000d) continue;
            if (HasRestriction(world, "door-fault", train.Id) || HasRestriction(world, "extended-dwell", train.Id)) continue;
            var minor = Sample(world, "train-failure-kind-2", train.Id, opportunity) < 0.8;
            var repair = Sample(world, "train-repair-profile-2", train.Id, opportunity);
            var duration = minor ? 30 + (int)(repair * 90) : 120 + (int)(repair * 360);
            OpenIncident(world, minor ? "extended-dwell" : "door-fault", train.Id, duration, "stochastic",
                minor ? $"Extended dwell on {train.Id}." : $"Door fault on {train.Id}.");
        }

        ProcessStochasticStationIncidents(world, opportunity, multiplier);
    }

    private static Incident OpenIncident(
        WorldState world,
        string kind,
        string target,
        double durationSeconds,
        string source,
        string description,
        string? id = null)
    {
        var incident = new Incident
        {
            Id = id ?? HashId(world.Seed, $"{kind}:{target}", world.SimTime.UtcTicks, world.Incidents.Count),
            Kind = kind,
            Target = target,
            Source = source,
            Description = description,
            StartedAt = world.SimTime,
            EndsAt = world.SimTime.AddSeconds(durationSeconds),
            Severity = 1
        };
        world.Incidents.Add(incident);
        AddHistory(world, "incident-opened", incident.Id, source, description, incident.Id);
        if (kind is "door-fault" or "extended-dwell")
        {
            var train = world.Trains.FirstOrDefault(t => t.Id == target);
            if (train is not null)
            {
                train.HoldReason = description;
                train.ReadyAt = Max(train.ReadyAt, incident.EndsAt!.Value);
            }
        }
        return incident;
    }

    private static bool ElevatorUnavailable(WorldState world, string stationId) =>
        world.Incidents.Any(i => i.State == "active"
            && i.Kind == "elevator-outage"
            && (i.Target == stationId || i.Target == $"{stationId}:elevator"));

    private static bool HasRestriction(WorldState world, string kind, string target) =>
        world.Incidents.Any(i => i.State == "active" && i.Kind == kind && i.Target == target);

    private static bool HasMarker(WorldState world, string kind, string correlationId) =>
        world.Commands.ContainsKey($"internal:{kind}:{correlationId}");

    private static void Mark(WorldState world, string kind, string correlationId) =>
        world.Commands[$"internal:{kind}:{correlationId}"] = new(true, "internal", kind, world.Version, correlationId);

    private static void AddHistory(
        WorldState world,
        string kind,
        string entityId,
        string source,
        string description,
        string correlationId)
    {
        world.History.Add(new HistoryEvent
        {
            Id = HashId(world.Seed, $"{kind}:{entityId}", world.SimTime.UtcTicks, world.History.Count),
            ScenarioId = world.ScenarioId,
            Generation = world.Generation,
            Version = world.Version + 1,
            SimTime = world.SimTime,
            Kind = kind,
            EntityId = entityId,
            Source = source,
            Description = description,
            CorrelationId = correlationId,
            StreamVersion = world.StreamVersion
        });
        if (world.History.Count > HistoryLimit)
        {
            world.History.RemoveRange(0, world.History.Count - HistoryLimit);
        }
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left >= right ? left : right;

    private static string HashId(long seed, string scope, long opportunity, int salt = 0)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}|{scope}|{opportunity}|{salt}"));
        return Convert.ToHexString(bytes.AsSpan(0, 12)).ToLowerInvariant();
    }
}
