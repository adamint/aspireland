using System.Collections.Concurrent;
using MetroOps.Contracts;

namespace MetroOps.Domain;

public static partial class Kernel
{
    private sealed record RideChoice(string Line, string From, string To, int Direction, string[] Stops);
    private sealed record WaitEstimate(double Seconds, bool Unknown, bool Stale, bool Crowded, IReadOnlyList<string> Restrictions);
    private static readonly ConcurrentDictionary<string, IReadOnlyList<List<RideChoice>>> RidePathCache = new(StringComparer.Ordinal);

    public static JourneyResult Plan(WorldState world, JourneyRequest request)
    {
        var origin = Network.Resolve(request.Origin);
        var destination = Network.Resolve(request.Destination);
        if (origin is null || destination is null)
        {
            return Journey(world, request, [], "Origin or destination is unknown or ambiguous.");
        }
        if (origin == destination)
        {
            var time = request.DepartureTime ?? world.SimTime;
            return Journey(world, request,
            [
                new Itinerary
                {
                    Label = "Fastest",
                    Accessible = true,
                    ArrivesAt = time,
                    TotalSeconds = 0,
                    EstimateKind = request.DepartureTime > world.SimTime ? "schedule" : "live"
                }
            ]);
        }
        if (HasRestriction(world, "station-closure", origin) || HasRestriction(world, "station-closure", destination))
        {
            return Journey(world, request, [], "Origin or destination is closed.");
        }

        var departure = request.DepartureTime ?? world.SimTime;
        var schedule = departure > world.SimTime.AddSeconds(1);
        var candidates = RidePaths(world, origin, destination, request.Accessible)
            .Select(path => BuildItinerary(world, path, request.Accessible, departure, schedule))
            .Where(i => i is not null)
            .Cast<Itinerary>()
            .OrderBy(i => i.TotalSeconds)
            .ThenBy(i => i.Transfers)
            .ToList();
        if (candidates.Count == 0)
        {
            var reason = request.Accessible
                ? "No accessible route is available under current restrictions."
                : "No route is available under current restrictions.";
            return Journey(world, request, [], reason);
        }

        var options = new List<Itinerary>();
        AddOption(options, candidates[0], "Fastest");
        var fewer = candidates.OrderBy(i => i.Transfers).ThenBy(i => i.TotalSeconds).First();
        AddOption(options, fewer, "Fewer transfers");
        if (!request.Accessible)
        {
            var accessible = RidePaths(world, origin, destination, true)
                .Select(path => BuildItinerary(world, path, true, departure, schedule))
                .Where(i => i is not null)
                .Cast<Itinerary>()
                .OrderBy(i => i.TotalSeconds)
                .FirstOrDefault();
            if (accessible is not null) AddOption(options, accessible, "Accessible");
        }
        else
        {
            options[0].Label = "Accessible";
        }

        return Journey(world, request, options);
    }

    public static StationBoard Board(WorldState world, string stationId)
    {
        var station = Network.Resolve(stationId);
        if (station is null)
        {
            return new(stationId, world.ScenarioId, world.Generation, world.SimTime, world.UpdatedAt, true, [], ["Unknown station."]);
        }

        var arrivals = new List<Arrival>();
        foreach (var train in world.Trains.Where(t => t.State is not "reserve" and not "withdrawn" && !string.IsNullOrEmpty(t.Line)))
        {
            var route = Network.Line(train.Line).Stations;
            if (!route.Contains(station)) continue;
            var overdueConfirmation = train.State == "travelling" && train.ArrivesAt <= world.SimTime;
            var unknown = train.State == "uncertain" || overdueConfirmation;
            var seconds = unknown ? null : EstimateTrainArrival(world, train, station);
            var prediction = seconds is null ? "unknown" : "live";
            var direction = train.Direction;
            var destination = route[ServiceDestinationIndex(train)];
            var shortTurn = UpcomingShortTurnIndex(train) is not null;
            var blocked = !unknown && EstimateTrainArrival(world, train, destination) is null;
            var status = overdueConfirmation
                ? "Awaiting movement confirmation"
                : train.State switch
                {
                    "uncertain" => "Position uncertain",
                    _ when blocked => "Service path blocked by restrictions.",
                    _ when seconds is null => shortTurn
                        ? $"Short-turn service to {destination}; no through arrival."
                        : "Service path blocked by restrictions.",
                    "held" => train.HoldReason ?? "Held",
                    "travelling" => "En route",
                    _ when train.HoldReason is not null => train.HoldReason,
                    _ when shortTurn => $"Short-turn service to {destination}.",
                    _ => "On time"
                };
            arrivals.Add(new(
                train.Id,
                train.Line,
                train.Line == "AX" ? "Airport Express" : train.Line == "Amber" ? "local" : "local",
                Network.Platform(station, train.Line, direction),
                destination,
                seconds,
                prediction,
                status,
                train.Load,
                train.Capacity));
        }

        var restrictions = world.Incidents
            .Where(i => i.State == "active" && IncidentAffectsStation(i, station))
            .Select(i => $"{i.Kind}: {i.Description}")
            .Order(StringComparer.Ordinal)
            .ToList();
        var stale = DateTimeOffset.UtcNow - world.UpdatedAt > TimeSpan.FromSeconds(5);
        return new(
            station,
            world.ScenarioId,
            world.Generation,
            world.SimTime,
            world.UpdatedAt,
            stale,
            arrivals.OrderBy(a => a.Seconds is null).ThenBy(a => a.Seconds).ThenBy(a => a.TrainId, StringComparer.Ordinal).ToList(),
            restrictions);
    }

    private static JourneyResult Journey(WorldState world, JourneyRequest request, List<Itinerary> options, string? error = null) =>
        new(world.ScenarioId, world.Generation, world.Version, world.SimTime, request, options, error);

    private static IEnumerable<List<RideChoice>> EnumerateRidePaths(
        WorldState world,
        string origin,
        string destination,
        bool accessible)
    {
        var queue = new Queue<(string Station, List<RideChoice> Rides, HashSet<string> Visited)>();
        queue.Enqueue((origin, [], [origin]));
        var results = new List<List<RideChoice>>();
        while (queue.Count > 0)
        {
            var (station, rides, visited) = queue.Dequeue();
            if (rides.Count >= 3) continue;
            foreach (var line in Network.Definition.Lines.Where(l => l.Stations.Contains(station)))
            {
                if (rides.Count > 0 && rides[^1].Line == line.Id) continue;
                var fromIndex = Array.IndexOf(line.Stations, station);
                for (var toIndex = 0; toIndex < line.Stations.Length; toIndex++)
                {
                    if (toIndex == fromIndex) continue;
                    var target = line.Stations[toIndex];
                    if (visited.Contains(target) && target != destination) continue;
                    var direction = Math.Sign(toIndex - fromIndex);
                    var stops = direction > 0
                        ? line.Stations[fromIndex..(toIndex + 1)]
                        : line.Stations[toIndex..(fromIndex + 1)].Reverse().ToArray();
                    var choice = new RideChoice(line.Id, station, target, direction, stops);
                    if (!RideAvailable(world, choice, accessible)) continue;
                    var next = new List<RideChoice>(rides) { choice };
                    if (target == destination)
                    {
                        results.Add(next);
                        continue;
                    }
                    if (!Network.Definition.Lines.Any(l => l.Id != line.Id && l.Stations.Contains(target))) continue;
                    var nextVisited = new HashSet<string>(visited, StringComparer.Ordinal) { target };
                    queue.Enqueue((target, next, nextVisited));
                }
            }
        }
        return results;
    }

    private static IReadOnlyList<List<RideChoice>> RidePaths(
        WorldState world,
        string origin,
        string destination,
        bool accessible)
    {
        var restrictions = string.Join(
            ",",
            world.Incidents
                .Where(i => i.State == "active" && i.Kind is "station-closure" or "platform-closure" or "segment-closure" or "elevator-outage")
                .OrderBy(i => i.Kind, StringComparer.Ordinal)
                .ThenBy(i => i.Target, StringComparer.Ordinal)
                .Select(i => $"{i.Kind}:{i.Target}"));
        var key = $"{origin}|{destination}|{accessible}|{restrictions}";
        return RidePathCache.GetOrAdd(key, _ => EnumerateRidePaths(world, origin, destination, accessible).ToList());
    }

    private static bool RideAvailable(WorldState world, RideChoice ride, bool accessible)
    {
        if (HasRestriction(world, "platform-closure", ride.From)) return false;
        if (accessible && (ElevatorUnavailable(world, ride.From) || ElevatorUnavailable(world, ride.To))) return false;
        for (var i = 0; i < ride.Stops.Length - 1; i++)
        {
            if (RideStepBlocked(world, ride.Stops[i], ride.Stops[i + 1])) return false;
        }
        return true;
    }

    private static bool RideStepBlocked(WorldState world, string from, string to) =>
        HasRestriction(world, "station-closure", from)
        || HasRestriction(world, "station-closure", to)
        || HasRestriction(world, "platform-closure", to)
        || HasRestriction(world, "segment-closure", $"{from}>{to}")
        || HasRestriction(world, "segment-closure", $"{to}>{from}");

    private static int? UpcomingShortTurnIndex(TrainState train)
    {
        var index = Array.IndexOf(Network.Line(train.Line).Stations, train.ShortTurnAt);
        var distance = (index - train.RouteIndex) * train.Direction;
        return index >= 0 && (distance > 0 || distance == 0 && train.State != "travelling") ? index : null;
    }

    private static int ServiceDestinationIndex(TrainState train) =>
        UpcomingShortTurnIndex(train) ?? (train.Direction > 0 ? Network.Line(train.Line).Stations.Length - 1 : 0);

    private static double EstimatedServiceHeadway(WorldState world, string lineId)
    {
        var line = Network.Line(lineId);
        var floor = world.Headways.GetValueOrDefault(lineId, line.HeadwaySeconds);
        var fleet = world.Trains.Count(t => t.Line == lineId && t.State is not "reserve" and not "withdrawn");
        var travel = line.Stations.Zip(line.Stations.Skip(1),
            (from, to) => Network.TravelSeconds(lineId, from, to) + Network.TravelSeconds(lineId, to, from)).Sum();
        // Nominal 20-second stops and two 60-second turnbacks, before traffic and incidents.
        var cycle = travel + (line.Stations.Length - 1) * 40 + 120;
        return Math.Max(floor, cycle / (double)Math.Max(1, fleet));
    }

    private static Itinerary? BuildItinerary(
        WorldState world,
        List<RideChoice> rides,
        bool accessible,
        DateTimeOffset departure,
        bool schedule)
    {
        if (rides.Count == 0) return null;
        var itinerary = new Itinerary
        {
            Accessible = accessible,
            Transfers = rides.Count - 1,
            EstimateKind = schedule ? "schedule" : "live"
        };
        if (schedule)
        {
            itinerary.Warnings.Add("Headways are departure regulation floors, not guaranteed timetables; scheduled waits include the assigned fleet's estimated round-trip cycle.");
        }
        var accessSeconds = accessible ? 100d : 80d;
        var cursor = departure.AddSeconds(accessSeconds);
        itinerary.Legs.Add(new JourneyLeg
        {
            Kind = "access",
            From = rides[0].From,
            To = rides[0].From,
            Service = accessible ? "accessible station access" : "station access",
            WalkSeconds = accessSeconds
        });
        foreach (var (ride, index) in rides.Select((r, i) => (r, i)))
        {
            var transferWalk = index == 0 ? 0 : accessible ? 130 : 100;
            if (transferWalk > 0)
            {
                itinerary.Legs.Add(new JourneyLeg
                {
                    Kind = "transfer",
                    From = ride.From,
                    To = ride.From,
                    Service = accessible ? "accessible transfer" : "transfer",
                    WalkSeconds = transferWalk
                });
                cursor = cursor.AddSeconds(transferWalk);
            }
            var waitEstimate = schedule
                ? new WaitEstimate(
                    EstimatedServiceHeadway(world, ride.Line) / 2,
                    false,
                    false,
                    false,
                    [])
                : LiveWait(world, ride, cursor);
            var wait = waitEstimate.Seconds;
            var rideSeconds = 0d;
            for (var stop = 0; stop < ride.Stops.Length - 1; stop++)
            {
                rideSeconds += Network.TravelSeconds(ride.Line, ride.Stops[stop], ride.Stops[stop + 1]);
                if (stop + 1 < ride.Stops.Length - 1) rideSeconds += 20;
            }
            var route = Network.Line(ride.Line).Stations;
            itinerary.Legs.Add(new JourneyLeg
            {
                Kind = "ride",
                Line = ride.Line,
                From = ride.From,
                To = ride.To,
                Platform = Network.Platform(ride.From, ride.Line, ride.Direction),
                Destination = route[ride.Direction > 0 ? ^1 : 0],
                Service = ride.Line == "AX" ? "Airport Express" : ride.Line == "Amber" ? "local" : "local",
                WaitSeconds = wait,
                RideSeconds = rideSeconds,
                Stops = [.. ride.Stops]
            });
            cursor = cursor.AddSeconds(wait + rideSeconds);
            if (waitEstimate.Stale)
            {
                itinerary.Warnings.Add($"The {ride.From} live board is stale; wait uses the last board plus estimated service spacing.");
            }
            if (waitEstimate.Unknown)
            {
                itinerary.Warnings.Add($"The {ride.Line} arrival at {ride.From} serving {ride.To} is uncertain; wait uses estimated service spacing.");
            }
            if (waitEstimate.Crowded)
            {
                itinerary.Warnings.Add($"High {ride.Line} occupancy at {ride.From} may require waiting for a later service.");
            }
            foreach (var restriction in waitEstimate.Restrictions)
            {
                itinerary.Warnings.Add($"Restriction at {ride.From}: {restriction}");
            }
        }
        itinerary.Legs.Add(new JourneyLeg
        {
            Kind = "egress",
            From = rides[^1].To,
            To = rides[^1].To,
            Service = accessible ? "accessible station egress" : "station egress",
            WalkSeconds = accessSeconds
        });
        cursor = cursor.AddSeconds(accessSeconds);
        itinerary.TotalSeconds = (cursor - departure).TotalSeconds;
        itinerary.ArrivesAt = cursor;
        if (!schedule && rides.Any(r => !world.Trains.Any(t => t.Line == r.Line && t.State is not "withdrawn" and not "reserve")))
        {
            itinerary.Warnings.Add("No live train is currently assigned; wait uses the service plan.");
        }
        return itinerary;
    }

    private static WaitEstimate LiveWait(
        WorldState world,
        RideChoice ride,
        DateTimeOffset readyAt)
    {
        var board = Board(world, ride.From);
        var platform = Network.Platform(ride.From, ride.Line, ride.Direction);
        var route = Network.Line(ride.Line).Stations;
        var destination = Array.IndexOf(route, ride.To);
        var arrivals = board.Arrivals
            .Where(a => a.Line == ride.Line && a.Platform == platform)
            .Where(a => (Array.IndexOf(route, a.Destination) - destination) * ride.Direction >= 0)
            .ToArray();
        var known = arrivals.Where(a => a.Seconds is not null).ToArray();
        var headway = EstimatedServiceHeadway(world, ride.Line);
        var readyOffset = Math.Max(0, (readyAt - world.SimTime).TotalSeconds);
        var wait = known.Length == 0
            ? headway / 2
            : known.Min(a =>
            {
                var first = a.Seconds!.Value;
                if (first >= readyOffset) return first - readyOffset;
                var cycles = Math.Ceiling((readyOffset - first) / headway);
                return first + cycles * headway - readyOffset;
            });
        return new(
            wait,
            known.Length == 0,
            board.Stale,
            known.Any(a => a.Load >= a.Capacity * .9),
            board.Restrictions);
    }

    private static void AddOption(List<Itinerary> options, Itinerary source, string label)
    {
        var identity = string.Join("|", source.Legs.Select(l => $"{l.Line}:{l.From}>{l.To}"));
        if (options.Any(o => string.Join("|", o.Legs.Select(l => $"{l.Line}:{l.From}>{l.To}")) == identity)) return;
        var copy = Wire.Clone(source);
        copy.Label = label;
        options.Add(copy);
    }

    private static double? EstimateTrainArrival(WorldState world, TrainState train, string station)
    {
        if (train.State == "uncertain") return null;
        var route = Network.Line(train.Line).Stations;
        var target = Array.IndexOf(route, station);
        if (target < 0) return null;
        if (HasRestriction(world, "station-closure", station) || HasRestriction(world, "platform-closure", station)) return null;
        if (train.State is "dwelling" or "held" && train.RouteIndex == target)
        {
            return Math.Max(0, (EffectiveReadyAt(world, train) - world.SimTime).TotalSeconds);
        }

        var seconds = 0d;
        var index = train.RouteIndex;
        var direction = train.Direction;
        if (train.State == "travelling" && train.NextStationId is not null)
        {
            seconds = Math.Max(0, ((train.ArrivesAt ?? world.SimTime) - world.SimTime).TotalSeconds);
            index = Array.IndexOf(route, train.NextStationId);
            if (index == target) return seconds;
            seconds += 20;
        }
        else
        {
            seconds = Math.Max(0, (EffectiveReadyAt(world, train) - world.SimTime).TotalSeconds);
        }

        for (var moves = 0; moves < (route.Length - 1) * 2 + 1; moves++)
        {
            if (route[index] == train.ShortTurnAt) return null;
            var next = index + direction;
            if (next < 0 || next >= route.Length)
            {
                direction *= -1;
                seconds += 45;
                next = index + direction;
            }
            if (RideStepBlocked(world, route[index], route[next])) return null;
            seconds += Network.TravelSeconds(train.Line, route[index], route[next]);
            index = next;
            if (index == target) return seconds;
            seconds += 20;
        }
        return null;
    }

    public static bool IncidentAffectsStation(Incident incident, string station)
    {
        if (incident.Target == station || incident.Target.StartsWith(station + ":", StringComparison.Ordinal)) return true;
        if (incident.Kind == "segment-closure")
        {
            var parts = incident.Target.Split('>');
            return parts.Contains(station, StringComparer.Ordinal);
        }
        return false;
    }
}
