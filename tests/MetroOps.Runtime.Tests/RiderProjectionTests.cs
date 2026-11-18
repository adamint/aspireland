using System.Text.Json;
using MetroOps.Contracts;
using MetroOps.Domain;
using MetroOps.Scenario;
using Xunit.Abstractions;

namespace MetroOps.Runtime.Tests;

public sealed class RiderProjectionTests(ITestOutputHelper output)
{
    [Fact]
    public void MapQueuesKeepReadyCountsAndActualOldestWait()
    {
        var world = Fixture();
        var riders = Snapshot(world);

        Assert.Equal(5, riders.Cohorts.Count);
        Assert.Equal(7, riders.Cohorts.Single(c => c is { StationId: "central-exchange", State: "outside", Accessible: false }).Count);
        Assert.All(riders.Cohorts, cohort => Assert.True(Kernel.IsWaitingAt(cohort, riders.SimTime)));
        Assert.Equal(Json(ScenarioRoutes.SummarizeQueues(world)), Json(ScenarioRoutes.SummarizeQueues(riders)));
        Assert.Equal(world.SimTime.AddSeconds(-120.5), riders.Cohorts.Min(c => c.ReadyAt));
        Assert.Equal(11, world.Cohorts.Count);
    }

    [Fact]
    public void GrantLookupKeepsMapInterpolationAndOnlyUsefulDepartures()
    {
        var world = Fixture();
        var riders = Snapshot(world);
        var train = riders.Trains.Single(t => t.Id == "ax-1");
        var grant = Assert.Single(riders.Grants.Values, g => g.Id == train.ActiveGrantId);

        Assert.Equal("AX", train.Line);
        Assert.Equal(train.Id, grant.TrainId);
        Assert.False(grant.Completed);
        Assert.Equal("airport", grant.From);
        Assert.Equal("central-exchange", grant.To);
        Assert.Equal(world.SimTime.AddSeconds(-300), grant.DepartsAt);
        Assert.Equal(world.SimTime.AddSeconds(780), grant.ArrivesAt);
        Assert.Equal(new[] { "active", "blue-1-latest", "blue-2-latest", "overdue-active", "reverse" },
            riders.Grants.Values.Select(g => g.Id).Order(StringComparer.Ordinal));
        Assert.All(world.Grants, entry => Assert.NotEmpty(entry.Value.RequestId));
        Assert.Equal(9, world.Grants.Count);
    }

    [Fact]
    public void AllStationBoardsMatchAtTheMaximumHeadwayWithRestrictionsAndLiveMovement()
    {
        var world = Fixture();
        var riders = Snapshot(world);

        foreach (var station in Network.Definition.Stations)
            Assert.Equal(Json(Kernel.Board(world, station.Id)), Json(Kernel.Board(riders, station.Id)));

        var westhaven = Kernel.Board(riders, "westhaven");
        Assert.Equal(3450, westhaven.Arrivals.Single(a => a.TrainId == "blue-1").Seconds);
        var central = Kernel.Board(riders, "central-exchange");
        Assert.Equal(780, central.Arrivals.Single(a => a.TrainId == "ax-1").Seconds);
        Assert.Equal("Awaiting movement confirmation", central.Arrivals.Single(a => a.TrainId == "ax-2").Status);
        Assert.Contains("elevator-outage: Lift offline.", central.Restrictions);
        Assert.Single(riders.Incidents);
    }

    [Fact]
    public void MatureCheckpointExceedsBrokerLimitButItsRiderEnvelopeStaysSmall()
    {
        var world = Fixture();
        for (var i = 0; i < 50_000; i++)
        {
            world.Cohorts.Add(new Cohort
            {
                Id = $"retained-{i}", Count = 5, State = i % 2 == 0 ? "completed" : "abandoned",
                StationId = "central-exchange", Origin = "airport", Destination = "central-exchange",
                CreatedAt = world.SimTime.AddHours(-2), ReadyAt = world.SimTime.AddHours(-2)
            });
        }
        for (var i = 0; i < 10_000; i++)
            AddGrant(world, $"retained-{i}", "blue-1", "westhaven", "west-junction", -3601 - i);

        var legacy = LegacyEnvelope(world);
        var compact = CompactEnvelope(world);

        output.WriteLine($"Legacy envelope: {legacy.Length:N0} bytes; rider envelope: {compact.Length:N0} bytes.");
        Assert.True(legacy.Length > 16 * 1024 * 1024, $"Legacy envelope was {legacy.Length:N0} bytes.");
        Assert.True(compact.Length < 64 * 1024, $"Rider envelope was {compact.Length:N0} bytes.");
        Assert.Equal(50_011, world.Cohorts.Count);
        Assert.Equal(10_009, world.Grants.Count);
    }

    [Fact]
    public void LegacyEnvelopeNormalizesToTheSameRiderViewWithoutExposingOperationalState()
    {
        var world = Fixture();
        var legacy = DecodeEnvelope(LegacyEnvelope(world));
        var wire = CompactEnvelope(world);
        var compact = DecodeEnvelope(wire);

        using var document = JsonDocument.Parse(wire);
        Assert.Equal(new[] { "sequence", "eventId", "scenarioId", "generation", "version", "simTime", "world" },
            document.RootElement.EnumerateObject().Select(property => property.Name));
        var inner = document.RootElement.GetProperty("world");
        Assert.False(inner.TryGetProperty("commands", out _));
        Assert.False(inner.TryGetProperty("history", out _));
        Assert.False(inner.GetProperty("trains")[0].TryGetProperty("workerInstanceId", out _));
        Assert.False(inner.GetProperty("cohorts")[0].TryGetProperty("legs", out _));
        Assert.False(inner.GetProperty("grants").GetProperty("active").TryGetProperty("requestId", out _));

        Assert.Equal(Json(compact), Json(legacy));
        Assert.Equal(world.ScenarioId, compact.ScenarioId);
        Assert.Equal(world.Generation, compact.Generation);
        Assert.Equal(world.Version, compact.Version);
        Assert.Equal(world.SimTime, compact.SimTime);
        Assert.Equal(world.UpdatedAt, compact.UpdatedAt);
        Assert.Equal(world.Paused, compact.Paused);
        Assert.Equal(world.Speed, compact.Speed);
        Assert.All(compact.Trains, t => Assert.Null(t.WorkerInstanceId));
        Assert.All(compact.Grants, g => Assert.Empty(g.Value.RequestId));
        Assert.All(compact.Cohorts, c => { Assert.Empty(c.Id); Assert.Empty(c.Legs); });
        Assert.Empty(compact.Commands);
        Assert.Empty(compact.History);
        Assert.Empty(compact.BlockOccupancy);
        Assert.Empty(compact.PlatformOccupancy);
        Assert.Empty(compact.JunctionReservations);
        Assert.Empty(compact.Flights);
    }

    [Fact]
    public void AttendeeWorldCanDistinguishCurrentAndFutureActiveIncidents()
    {
        var world = Fixture();
        var current = world.Incidents.Single(i => i.State == "active");
        current.StartedAt = world.SimTime.AddSeconds(-1);
        var future = new Incident
        {
            Kind = "station-closure", Target = "airport", StartedAt = world.SimTime.AddSeconds(1)
        };
        world.Incidents.Add(future);

        foreach (var delivery in new[] { CompactEnvelope(world), LegacyEnvelope(world) })
        {
            var snapshot = new WorldSnapshot(DecodeEnvelope(delivery), Network.Definition);
            using var document = JsonDocument.Parse(Json(snapshot));
            var incidents = document.RootElement.GetProperty("world").GetProperty("incidents").EnumerateArray().ToArray();
            Assert.Equal(2, incidents.Length);
            Assert.Equal(current.StartedAt, incidents.Single(i => i.GetProperty("id").GetString() == current.Id)
                .GetProperty("startedAt").GetDateTimeOffset());
            Assert.Equal(future.StartedAt, incidents.Single(i => i.GetProperty("id").GetString() == future.Id)
                .GetProperty("startedAt").GetDateTimeOffset());
        }
    }

    private static WorldState Fixture()
    {
        var world = Kernel.Create();
        world.Generation = 4;
        world.Version = 901;
        world.Paused = true;
        world.Speed = 45;
        world.UpdatedAt = world.SimTime.AddYears(-1); // Keep the board stale flag stable across both reads.
        Assert.True(Kernel.Apply(world, new("headway", world.ScenarioId, world.Generation, null, "operator", "test",
            new("set-headway", "Blue", Amount: 3600))).Accepted);
        world.BlockOccupancy["occupied-test-block"] = "ax-1";
        world.PlatformOccupancy["occupied-test-platform"] = "ax-1";
        world.JunctionReservations["occupied-test-junction"] = "ax-1";
        world.Flights.Add(new("test-flight", "arriving", world.SimTime, world.SimTime, 100, false, "clear"));
        world.Cohorts =
        [
            Cohort("outside", 4, -120.5),
            Cohort("outside", 3, -30),
            Cohort("outside", 2, 0, accessible: true),
            Cohort("platform", 8, -15),
            Cohort("platform", 6, -9, accessible: true),
            Cohort("platform", 5, -12, station: "university"),
            Cohort("outside", 100, 1),
            Cohort("transfer", 100, -1),
            Cohort("onboard", 100, -1),
            Cohort("completed", 100, -1),
            Cohort("abandoned", 100, -1)
        ];
        world.Incidents =
        [
            new() { Kind = "elevator-outage", Target = "central-exchange:elevator", Description = "Lift offline." },
            new() { Kind = "station-closure", Target = "westhaven", State = "cleared" },
            new() { Kind = "station-closure", Target = "airport", State = "pending" }
        ];
        var blue1 = world.Trains.Single(t => t.Id == "blue-1");
        blue1.State = "held";
        blue1.HoldReason = "Operator hold.";
        blue1.ReadyAt = world.SimTime.AddSeconds(5);
        blue1.ShortTurnAt = "central-exchange";
        world.Trains.Single(t => t.Id == "blue-3").State = "uncertain";
        world.Trains.Single(t => t.Id == "green-1").State = "withdrawn";
        var express = world.Trains.Single(t => t.Id == "ax-1");
        express.StationId = "airport";
        express.RouteIndex = 0;
        express.NextStationId = "central-exchange";
        express.State = "travelling";
        express.WorkerInstanceId = "internal-worker";
        var active = AddGrant(world, "active", express.Id, "airport", "central-exchange", -300, duration: 1080);
        active.Completed = false;
        express.ActiveGrantId = active.Id;
        express.ArrivesAt = active.ArrivesAt;
        var overdue = world.Trains.Single(t => t.Id == "ax-2");
        overdue.State = "travelling";
        overdue.NextStationId = "central-exchange";
        var overdueGrant = AddGrant(world, "overdue-active", overdue.Id, "union-terminal", "central-exchange", -3700);
        overdueGrant.Completed = false;
        overdue.ActiveGrantId = overdueGrant.Id;
        overdue.ArrivesAt = overdueGrant.ArrivesAt;
        AddGrant(world, "blue-1-latest", "blue-1", "westhaven", "west-junction", -400);
        AddGrant(world, "blue-1-older", "blue-1", "westhaven", "west-junction", -401);
        AddGrant(world, "blue-2-latest", "blue-2", "westhaven", "west-junction", -150);
        AddGrant(world, "reverse", "blue-1", "west-junction", "westhaven", -120);
        AddGrant(world, "at-cutoff", "blue-1", "west-junction", "civic-square", -3600);
        AddGrant(world, "expired", "blue-1", "civic-square", "central-exchange", -3601);
        AddGrant(world, "orphan", "amber-1", "airport", "aerocity", -3700).Completed = false;
        return world;

        Cohort Cohort(string state, int count, double offset, bool accessible = false, string station = "central-exchange") =>
            new() { Id = $"{state}-{count}", State = state, StationId = station, Accessible = accessible,
                Count = count, ReadyAt = world.SimTime.AddSeconds(offset), Legs = [new JourneyLeg()] };
    }

    private static MovementGrant AddGrant(WorldState world, string id, string trainId, string from, string to, int offset, int duration = 115)
    {
        var grant = new MovementGrant
        {
            Id = id, RequestId = $"request-{id}", TrainId = trainId, From = from, To = to,
            Block = "internal-block", Platform = "internal-platform", Junction = "internal-junction",
            DepartsAt = world.SimTime.AddSeconds(offset), ArrivesAt = world.SimTime.AddSeconds(offset + duration), Completed = true
        };
        world.Grants.Add(grant.RequestId, grant);
        return grant;
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, Wire.Json);
    private static byte[] LegacyEnvelope(WorldState world) => JsonSerializer.SerializeToUtf8Bytes(
        new { Sequence = 42L, EventId = "test-event", world.ScenarioId, world.Generation, world.Version, world.SimTime, World = world }, Wire.Json);
    private static byte[] CompactEnvelope(WorldState world) => JsonSerializer.SerializeToUtf8Bytes(
        new ProjectionEnvelope(42, "test-event", world.ScenarioId, world.Generation, world.Version, world.SimTime, Kernel.ProjectRiders(world)), Wire.Json);
    private static WorldState DecodeEnvelope(byte[] body) =>
        Snapshot(JsonSerializer.Deserialize<ProjectionEnvelope>(body, Wire.Json)!.World.ToWorldState());
    private static WorldState Snapshot(WorldState world) => Kernel.ProjectRiders(world).ToWorldState();
}
