using MetroOps.Contracts;

namespace MetroOps.Domain;

public static partial class Kernel
{
    private sealed record RandomIncidentCandidate(string Kind, string Target);

    private static void ProcessStochasticStationIncidents(WorldState world, long opportunity, double multiplier)
    {
        foreach (var station in Network.Definition.Stations)
        {
            if (!HasRestriction(world, "circulation", station.Id)
                && Sample(world, "circulation-failure-profile-3", station.Id, opportunity) < multiplier / 40_000d)
            {
                var repair = Sample(world, "circulation-repair-profile-3", station.Id, opportunity);
                OpenIncident(world, "circulation", station.Id, 60 + (int)(repair * 180), "stochastic",
                    $"Circulation disruption at {station.Id}.");
            }
        }

        var elevatorCap = ActiveElevatorCap(world);
        var activeElevatorOutages = world.Incidents.Count(i => i.State == "active" && i.Kind == "elevator-outage");
        if (activeElevatorOutages >= elevatorCap) return;

        foreach (var station in Network.Definition.Stations)
        {
            if (activeElevatorOutages >= elevatorCap) break;
            if (ElevatorUnavailable(world, station.Id)) continue;
            if (Sample(world, "elevator-failure-profile-3", station.Id, opportunity) >= multiplier / 240_000d) continue;
            var duration = ElevatorOutageSeconds(world, station.Id, opportunity);
            OpenIncident(world, "elevator-outage", station.Id, duration, "stochastic",
                $"Elevator outage at {station.Id}.");
            activeElevatorOutages++;
        }
    }

    private static CommandResult CreateRandomIncident(WorldState world)
    {
        var canOpenElevatorOutage =
            world.Incidents.Count(i => i.State == "active" && i.Kind == "elevator-outage") < ActiveElevatorCap(world);
        var candidates = Network.Definition.Stations
            .SelectMany(station => new[]
            {
                new RandomIncidentCandidate("circulation", station.Id),
                new RandomIncidentCandidate("elevator-outage", station.Id),
                new RandomIncidentCandidate("platform-closure", station.Id)
            })
            .Where(candidate => !HasRestriction(world, "station-closure", candidate.Target))
            .Where(candidate => !HasRestriction(world, candidate.Kind, candidate.Target))
            .Where(candidate => candidate.Kind != "elevator-outage"
                || canOpenElevatorOutage && !ElevatorUnavailable(world, candidate.Target))
            .Where(candidate => candidate.Kind != "circulation"
                || !HasRestriction(world, "platform-closure", candidate.Target))
            .OrderBy(candidate => candidate.Kind, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Target, StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length == 0) return Rejected(world, "No eligible service alert is available.");

        var opportunity = world.Commands.Count;
        var choice = candidates[Math.Min(candidates.Length - 1,
            (int)(Sample(world, "random-service-alert-1", world.ScenarioId, opportunity) * candidates.Length))];
        var duration = RandomIncidentSeconds(world, choice, opportunity);
        var station = Network.Station(choice.Target);
        var label = IncidentLabel(choice.Kind);
        var incident = OpenIncident(world, choice.Kind, choice.Target, duration, "manual",
            $"{label} at {station.Name}.");
        return Accepted(world,
            $"{label} at {station.Name}; estimated restoration in {RelativeDuration(duration)}.",
            incident.Id);
    }

    private static int ActiveElevatorCap(WorldState world) => world.Preset == "rough" ? 2 : 1;

    private static int RandomIncidentSeconds(WorldState world, RandomIncidentCandidate candidate, long opportunity)
    {
        var repair = Sample(world, "random-service-alert-duration-1", $"{candidate.Kind}:{candidate.Target}", opportunity);
        return candidate.Kind switch
        {
            "circulation" => 120 + (int)(repair * 240),
            "platform-closure" => 600 + (int)(repair * 1_200),
            "elevator-outage" => ElevatorOutageSeconds(repair),
            _ => throw new InvalidOperationException($"Unsupported random incident kind '{candidate.Kind}'.")
        };
    }

    private static int ElevatorOutageSeconds(WorldState world, string target, long opportunity) =>
        ElevatorOutageSeconds(Sample(world, "elevator-repair-profile-3", target, opportunity));

    private static int ElevatorOutageSeconds(double repair) =>
        repair switch
        {
            < .70 => 7_200 + (int)(repair / .70 * 14_400),
            < .93 => 21_600 + (int)((repair - .70) / .23 * 21_600),
            _ => 43_200 + (int)((repair - .93) / .07 * 43_200)
        };

    private static string IncidentLabel(string kind) => kind switch
    {
        "circulation" => "Circulation disruption",
        "elevator-outage" => "Elevator outage",
        "platform-closure" => "Platform closure",
        _ => kind
    };

    private static string RelativeDuration(int seconds)
    {
        if (seconds < 90) return "about 1 minute";
        if (seconds < 5_400) return $"about {Math.Max(2, (int)Math.Round(seconds / 60d))} minutes";
        return $"about {Math.Max(2, (int)Math.Round(seconds / 3_600d))} hours";
    }
}
