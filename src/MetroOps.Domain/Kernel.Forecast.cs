using MetroOps.Contracts;

namespace MetroOps.Domain;

public static partial class Kernel
{
    private sealed record ForecastPoint(
        double WaitingMinutes,
        double Completed,
        double Denied,
        double Abandoned,
        double Remaining,
        double Oldest,
        double Overloads,
        double Accessibility);

    public static ComparisonResult Compare(WorldState world, ServiceAction candidate)
    {
        const int samples = 8;
        const int horizonMinutes = 30;
        var futures = Enumerable.Range(0, samples)
            .Select(sample =>
            {
                var futureSeed = unchecked(world.Seed + 104_729L * (sample + 1));
                return new ForecastSample(
                    futureSeed,
                    SyntheticFutureDemand(world, futureSeed, sample, horizonMinutes),
                    []);
            })
            .ToArray();
        return CompareCore(world, candidate, futures, true);
    }

    public static ComparisonResult Compare(
        WorldState world,
        ServiceAction candidate,
        IReadOnlyList<ForecastSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        return CompareCore(world, candidate, samples, false);
    }

    private static ComparisonResult CompareCore(
        WorldState world,
        ServiceAction candidate,
        IReadOnlyList<ForecastSample> samples,
        bool localFallback)
    {
        const int horizonMinutes = 30;
        if (samples.Count != 8) throw new ArgumentException("Comparison requires exactly eight paired forecast samples.", nameof(samples));

        var validation = Wire.Clone(world);
        validation.Paused = false;
        var validationResult = Apply(validation, new CommandEnvelope(
            $"compare:validate:{candidate.Kind}:{candidate.Target}:{candidate.Value}:{candidate.Amount}",
            validation.ScenarioId,
            validation.Generation,
            validation.Version,
            "forecast",
            "Candidate comparison validation",
            candidate));
        if (!validationResult.Accepted)
        {
            throw new ArgumentException($"Candidate was rejected at the checkpoint: {validationResult.Message}", nameof(candidate));
        }

        var baseline = new List<ForecastPoint>(samples.Count);
        var intervention = new List<ForecastPoint>(samples.Count);
        var warnings = new List<string>
        {
            "Eight paired samples are a bounded demonstration estimate, not a statistical guarantee."
        };
        if (localFallback)
        {
            warnings.Insert(0, "Future demand is sampled by the pure kernel fallback; runtime comparisons should provide authoritative forecast samples.");
        }

        for (var sampleIndex = 0; sampleIndex < samples.Count; sampleIndex++)
        {
            var sample = samples[sampleIndex] ?? throw new ArgumentException($"Forecast sample {sampleIndex} is null.", nameof(samples));
            if (sample.Demand is null || sample.Flights is null)
            {
                throw new ArgumentException($"Forecast sample {sampleIndex} must include demand and flights.", nameof(samples));
            }
            var baselineWorld = Wire.Clone(world);
            var interventionWorld = Wire.Clone(world);
            baselineWorld.Paused = false;
            interventionWorld.Paused = false;
            baselineWorld.Seed = sample.Seed;
            interventionWorld.Seed = sample.Seed;
            baselineWorld.Flights = MergeFlights(world.Flights, sample.Flights);
            interventionWorld.Flights = MergeFlights(world.Flights, sample.Flights);
            var baselineStart = Wire.Clone(baselineWorld.Metrics);
            var interventionStart = Wire.Clone(interventionWorld.Metrics);

            var action = Apply(interventionWorld, new CommandEnvelope(
                $"compare:{sampleIndex}:{candidate.Kind}:{candidate.Target}:{candidate.Value}:{candidate.Amount}",
                interventionWorld.ScenarioId,
                interventionWorld.Generation,
                interventionWorld.Version,
                "forecast",
                "Candidate comparison",
                candidate));
            if (!action.Accepted) throw new InvalidOperationException($"Validated candidate was rejected in sample {sampleIndex}: {action.Message}");

            var expectedEnd = world.SimTime.AddMinutes(horizonMinutes);
            Tick(baselineWorld, sample.Demand, horizonMinutes * 60, true);
            Tick(interventionWorld, sample.Demand, horizonMinutes * 60, true);
            if (baselineWorld.SimTime != expectedEnd || interventionWorld.SimTime != expectedEnd)
            {
                throw new InvalidOperationException("Forecast did not advance the complete 30-minute horizon.");
            }
            baseline.Add(Forecast(baselineWorld, baselineStart));
            intervention.Add(Forecast(interventionWorld, interventionStart));
        }

        return new(
            world.ScenarioId,
            world.Generation,
            world.Version,
            world.SimTime,
            horizonMinutes,
            samples.Count,
            candidate,
            Metrics(baseline),
            Metrics(intervention),
            localFallback
                ? "Eight paired locally sampled synthetic futures using cloned checkpoints and the live kernel movement/passenger rules."
                : "Eight paired authoritative demand/flight futures using cloned checkpoints and the live kernel movement/passenger rules.",
            warnings);
    }

    private static List<Flight> MergeFlights(IReadOnlyList<Flight> known, IReadOnlyList<Flight> future)
    {
        var merged = future.ToDictionary(f => f.Id, Wire.Clone, StringComparer.Ordinal);
        foreach (var flight in known)
        {
            merged[flight.Id] = Wire.Clone(flight);
        }
        return merged.Values.OrderBy(f => f.ActualAt).ThenBy(f => f.Id, StringComparer.Ordinal).ToList();
    }

    private static List<DemandBatch> SyntheticFutureDemand(WorldState checkpoint, long seed, int sample, int horizonMinutes)
    {
        var result = new List<DemandBatch>();
        var stations = Network.Definition.Stations;
        for (var minute = 1; minute <= horizonMinutes; minute++)
        {
            for (var wave = 0; wave < 2; wave++)
            {
                var opportunity = minute * 10L + wave;
                var version = checkpoint.StreamVersion;
                var originIndex = (int)(DeterministicUnit(seed, $"forecast-origin:v{version}", "network", opportunity) * stations.Count);
                var destinationIndex = (int)(DeterministicUnit(seed, $"forecast-destination:v{version}", "network", opportunity) * (stations.Count - 1));
                if (destinationIndex >= originIndex) destinationIndex++;
                var count = 3 + (int)(DeterministicUnit(seed, $"forecast-size:v{version}", stations[originIndex].Id, opportunity) * 10);
                if (checkpoint.Preset == "rough") count = (int)Math.Ceiling(count * 1.25);
                var accessible = DeterministicUnit(seed, $"forecast-access:v{version}", stations[originIndex].Id, opportunity) < .09;
                var patience = 900 + DeterministicUnit(seed, $"forecast-patience:v{version}", stations[originIndex].Id, opportunity) * 1500;
                result.Add(new(
                    $"forecast:{sample}:{minute}:{wave}",
                    stations[originIndex].Id,
                    stations[destinationIndex].Id,
                    count,
                    accessible,
                    checkpoint.SimTime.AddMinutes(minute),
                    patience));
            }
        }
        return result;
    }

    private static ForecastPoint Forecast(WorldState world, PassengerMetrics start)
    {
        var queue = world.Cohorts.Where(c => IsWaitingAt(c, world.SimTime)).ToArray();
        var oldest = queue.Length == 0
            ? 0
            : queue.Max(c => Math.Max(0, (world.SimTime - Max(c.CreatedAt, c.ReadyAt)).TotalSeconds));
        return new(
            (world.Metrics.WaitingPassengerSeconds - start.WaitingPassengerSeconds) / 60,
            world.Metrics.Completed - start.Completed,
            world.Metrics.DeniedBoardings - start.DeniedBoardings,
            world.Metrics.Abandoned - start.Abandoned,
            queue.Sum(c => c.Count),
            oldest,
            world.Metrics.PlatformOverloads - start.PlatformOverloads,
            world.Metrics.AccessibilityFailures - start.AccessibilityFailures);
    }

    private static ForecastMetrics Metrics(IReadOnlyList<ForecastPoint> values) =>
        new(
            Range(values.Select(v => v.WaitingMinutes)),
            Range(values.Select(v => v.Completed)),
            Range(values.Select(v => v.Denied)),
            Range(values.Select(v => v.Abandoned)),
            Range(values.Select(v => v.Remaining)),
            Range(values.Select(v => v.Oldest)),
            Range(values.Select(v => v.Overloads)),
            Range(values.Select(v => v.Accessibility)));

    private static MetricRange Range(IEnumerable<double> sequence)
    {
        var values = sequence.ToArray();
        return new(values.Average(), values.Min(), values.Max());
    }
}
