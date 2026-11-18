using MetroOps.Contracts;
using MetroOps.Domain;
using MetroOps.Scenario;
using System.Text.Json;

namespace MetroOps.Runtime.Tests;

public sealed class ScenarioReadModelTests
{
    [Fact]
    public void AnalyticsSummaryBoundsRetiredHistoryAndPreservesReadyAndFutureInventory()
    {
        var world = Kernel.Create();
        world.Cohorts = Enumerable.Range(0, 10_000).Select(i => new Cohort
        {
            Id = $"retired-{i}", State = "completed", Count = 2,
            Legs = [new JourneyLeg { From = new string('x', 1000) }]
        }).ToList();
        world.Cohorts.AddRange(
        [
            new() { State = "outside", StationId = "central-exchange", Count = 3, ReadyAt = world.SimTime.AddMinutes(-2) },
            new() { State = "outside", StationId = "central-exchange", Count = 4, ReadyAt = world.SimTime.AddMinutes(-1) },
            new() { State = "outside", StationId = "central-exchange", Count = 5, ReadyAt = world.SimTime.AddHours(1) },
            new() { State = "transfer", StationId = "university", Count = 6, Accessible = true, ReadyAt = world.SimTime.AddSeconds(20) },
            new() { State = "onboard", StationId = "university", Count = 7 },
            new() { State = "unknown", StationId = "university", Count = 8 }
        ]);
        world.Metrics.Completed = 20_000;
        world.Metrics.Generated = 20_033;

        var bytes = JsonSerializer.SerializeToUtf8Bytes(ScenarioRoutes.AnalyticsState(world), Wire.Json);
        var summary = JsonSerializer.Deserialize<WorldState>(bytes, Wire.Json)!;

        Assert.True(JsonSerializer.SerializeToUtf8Bytes(world, Wire.Json).Length > 8 * 1024 * 1024);
        Assert.True(bytes.Length < 64 * 1024);
        Assert.Equal(33, summary.Cohorts.Sum(c => c.Count));
        Assert.Equal(7, summary.Cohorts.Where(c => Kernel.IsWaitingAt(c, summary.SimTime)).Sum(c => c.Count));
        Assert.Equal(5, summary.Cohorts.Where(c => c.State == "outside" && c.ReadyAt > summary.SimTime).Sum(c => c.Count));
        Assert.Equal(world.SimTime.AddMinutes(-2), summary.Cohorts.Single(c => c.State == "outside" && c.ReadyAt <= summary.SimTime).ReadyAt);
        Assert.Equal(8, summary.Cohorts.Single(c => c.State == "unknown").Count);
        Assert.Equal(world.Metrics.Generated, summary.Metrics.Generated);
        Assert.Equal(world.Metrics.Completed, summary.Metrics.Completed);
        Assert.Equal(world.SimTime, summary.SimTime);
        Assert.Equal(world.UpdatedAt, summary.UpdatedAt);
        Assert.Equal(10_006, world.Cohorts.Count);
    }

    [Fact]
    public void QueueSummaryCountsPlatformAndAccessQueuesWithoutOnboardOrRetiredPassengers()
    {
        var world = Kernel.Create();
        world.Cohorts =
        [
            new() { Id = "platform", StationId = "central-exchange", State = "platform", Count = 7, ReadyAt = world.SimTime.AddMinutes(-2) },
            new() { Id = "access", StationId = "central-exchange", State = "outside", Count = 3, Accessible = true, ReadyAt = world.SimTime.AddSeconds(-30) },
            new() { Id = "riding", StationId = "central-exchange", State = "onboard", Count = 2, TrainId = "blue-1" },
            new() { Id = "retired", StationId = "central-exchange", State = "completed", Count = 5 },
            new() { Id = "scheduled-return", StationId = "central-exchange", State = "outside", Count = 19, ReadyAt = world.SimTime.AddHours(4) },
            new() { Id = "elsewhere", StationId = "university", State = "platform", Count = 4, ReadyAt = world.SimTime }
        ];

        var stations = ScenarioRoutes.SummarizeQueues(world);
        var central = stations.Single(s => s.Id == "central-exchange");

        Assert.Equal(10, central.Waiting);
        Assert.Equal(7, central.PlatformWaiting);
        Assert.Equal(3, central.AccessQueue);
        Assert.Equal(3, central.AccessibleWaiting);
        Assert.Equal(120, central.OldestWaitSeconds);
        Assert.Equal(14, stations.Sum(s => s.Waiting));
        Assert.DoesNotContain("onboard", central.CountsByState.Keys);
        Assert.DoesNotContain("completed", central.CountsByState.Keys);
    }
}
