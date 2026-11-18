using MetroOps.Domain;
using Xunit;
using Xunit.Abstractions;

namespace MetroOps.Domain.Tests;

public sealed class DisruptionTests(ITestOutputHelper output)
{
    [Fact]
    public void SeededNetworkDisruptionsAreFrequentMostlyShortAndRecoverWithoutStoppingMovement()
    {
        int[] seeds = [11, 42, 123, 456, 789, 2026, 20261117, 87654];
        var counts = new Dictionary<string, int>();
        foreach (var preset in new[] { "normal", "rough" })
        {
            var total = 0;
            var shortIssues = 0;
            foreach (var seed in seeds)
            {
                var world = Kernel.Create(seed, preset);
                var middle = world.SimTime.AddHours(1);
                Kernel.Tick(world, [], 3600, driveTrains: true);
                var firstHourGrants = world.Grants.Count;
                Kernel.Tick(world, [], 3600, driveTrains: true);
                var incidents = world.Incidents.Where(i => i.Source == "stochastic").ToArray();
                total += incidents.Length;
                shortIssues += incidents.Count(i => i.Kind is "extended-dwell" or "circulation");
                Assert.All(incidents.Where(i => i.EndsAt <= world.SimTime), i => Assert.Equal("cleared", i.State));
                Assert.DoesNotContain(world.Incidents.GroupBy(i => (i.Kind, i.Target)),
                    group => group.OrderBy(i => i.StartedAt).Zip(group.OrderBy(i => i.StartedAt).Skip(1))
                        .Any(pair => pair.First.EndsAt > pair.Second.StartedAt));
                Assert.True(world.Grants.Count > firstHourGrants, $"Movement stopped in {preset} seed {seed} after {middle:O}.");
                Assert.All(world.Grants.Values.Where(g => !g.Completed).GroupBy(g => g.Block), group => Assert.Single(group));
                Assert.All(world.Trains, train => Assert.InRange(train.Load, 0, train.Capacity));
            }
            var interval = seeds.Length * 7200d / total;
            output.WriteLine($"{preset}: {total} incidents / {seeds.Length * 2} simulated hours; mean interval {interval:F1}s; {shortIssues} short issues.");
            Assert.InRange(interval, preset == "normal" ? 300 : 100, preset == "normal" ? 600 : 300);
            Assert.True(shortIssues > total / 2, "Most disruptions should be short local delays, not equipment failures.");
            counts[preset] = total;
        }
        Assert.True(counts["rough"] > counts["normal"] * 1.5);
    }
}
