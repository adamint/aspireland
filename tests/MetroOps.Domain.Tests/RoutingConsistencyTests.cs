using MetroOps.Contracts;
using MetroOps.Domain;
using Xunit;

namespace MetroOps.Domain.Tests;

public sealed class RoutingConsistencyTests
{
    [Theory]
    [InlineData("station-closure")]
    [InlineData("platform-closure")]
    public void Intermediate_closure_blocks_through_rides_until_cleared(string kind)
    {
        var world = Kernel.Create();
        Assert.Contains(Kernel.Plan(world, new("pinecrest", "eastgate")).Options,
            option => option.Legs.Any(leg => leg.Line == "Blue" && leg.From == "pinecrest" && leg.To == "eastgate"));

        var closure = Apply(world, new("incident", "civic-square", kind, 600));

        Assert.True(closure.Accepted);
        Assert.Empty(Kernel.Plan(world, new("pinecrest", "eastgate")).Options);
        Assert.True(Apply(world, new("clear-incident", closure.EntityId)).Accepted);
        Assert.Contains(Kernel.Plan(world, new("pinecrest", "eastgate")).Options,
            option => option.Legs.Any(leg => leg.Line == "Blue" && leg.From == "pinecrest" && leg.To == "eastgate"));
    }

    [Theory]
    [InlineData("station-closure", "civic-square")]
    [InlineData("platform-closure", "civic-square")]
    [InlineData("segment-closure", "west-junction>civic-square")]
    public void Board_does_not_publish_live_arrivals_through_a_blocked_path(string kind, string target)
    {
        var world = IsolatedBlueWorld();
        Assert.NotNull(Arrival(world, "eastgate").Seconds);
        var closure = Apply(world, new("incident", target, kind, 600));

        var blocked = Arrival(world, "eastgate");

        Assert.Null(blocked.Seconds);
        Assert.Equal("unknown", blocked.PredictionKind);
        Assert.Contains("blocked", blocked.Status, StringComparison.OrdinalIgnoreCase);
        Assert.True(Apply(world, new("clear-incident", closure.EntityId)).Accepted);
        var recovered = Arrival(world, "eastgate");
        Assert.NotNull(recovered.Seconds);
        Assert.Equal("live", recovered.PredictionKind);
    }

    [Fact]
    public void A_train_before_a_known_blockage_is_not_advertised_as_ordinary_on_time_service()
    {
        var world = IsolatedBlueWorld();
        Assert.True(Apply(world, new("incident", "civic-square", "station-closure", 600)).Accepted);

        Assert.Contains("blocked", Arrival(world, "pinecrest").Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Short_turn_board_advertises_the_turnback_and_no_through_eta()
    {
        var world = IsolatedBlueWorld();
        Position(world, "blue-1", "union-terminal", 1);
        Assert.True(Apply(world, new("short-turn", "blue-1", "east-junction")).Accepted);

        var turnback = Arrival(world, "east-junction");
        var beyond = Arrival(world, "eastgate");

        Assert.Equal("east-junction", turnback.Destination);
        Assert.Equal(115, turnback.Seconds);
        Assert.Equal("east-junction", beyond.Destination);
        Assert.Null(beyond.Seconds);
        Assert.Equal("unknown", beyond.PredictionKind);
        Assert.Contains("short-turn", beyond.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Short_turn_does_not_board_passengers_beyond_the_turnback()
    {
        var world = IsolatedBlueWorld();
        var train = Position(world, "blue-1", "union-terminal", 1);
        AddPassengers(world, "turnback", "platform", "union-terminal", "east-junction", 7);
        AddPassengers(world, "through", "platform", "union-terminal", "eastgate", 11);
        Assert.True(Apply(world, new("short-turn", train.Id, "east-junction")).Accepted);

        Kernel.Tick(world, [], 1);

        Assert.Equal(7, train.Load);
        Assert.Equal("onboard", world.Cohorts.Single(c => c.Id == "turnback").State);
        Assert.Equal("platform", world.Cohorts.Single(c => c.Id == "through").State);
        AssertConserved(world);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Turnback_waits_without_overwriting_an_occupied_or_reserved_reverse_platform(bool reserved)
    {
        var world = IsolatedBlueWorld();
        var train = Position(world, "blue-1", "east-junction", 1);
        var passengers = AddPassengers(world, "through", "onboard", "union-terminal", "eastgate", 12);
        train.Load = passengers.Count;
        Register(world, train);
        Assert.True(Apply(world, new("short-turn", train.Id, "east-junction")).Accepted);
        var other = Position(world, "blue-2", "riverbank", -1);
        Register(world, other);
        var approach = Kernel.RequestMovement(world, Request(world, other));
        Assert.True(approach.Granted, approach.Reason);
        var grant = approach.Grant!;
        void ConfirmApproach()
        {
            world.SimTime = grant.ArrivesAt;
            Assert.True(Kernel.ConfirmMovement(world,
                new($"confirm:{grant.Id}", other.Id, other.WorkerInstanceId!, world.ScenarioId, world.Generation, grant.Id)).Granted);
        }
        if (!reserved) ConfirmApproach();
        var version = train.Version;
        var readyAt = train.ReadyAt;
        var forward = Network.Platform("east-junction", "Blue", 1);
        var reverse = Network.Platform("east-junction", "Blue", -1);

        var blocked = Kernel.RequestMovement(world, Request(world, train));

        Assert.False(blocked.Granted);
        Assert.Equal(other.Id, world.PlatformOccupancy[reverse]);
        Assert.Equal(train.Id, world.PlatformOccupancy[forward]);
        Assert.Contains("platform", blocked.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, train.Direction);
        Assert.Equal(version, train.Version);
        Assert.Equal(readyAt, train.ReadyAt);
        Assert.Equal("east-junction", train.ShortTurnAt);
        Assert.Equal(12, train.Load);
        Assert.Equal("onboard", passengers.State);
        AssertConserved(world);

        if (reserved) ConfirmApproach();
        MoveToNextStop(world, other);
        world.SimTime = train.ReadyAt > world.SimTime ? train.ReadyAt : world.SimTime;
        var turn = Kernel.RequestMovement(world, Request(world, train));

        Assert.False(turn.Granted);
        Assert.Equal(-1, train.Direction);
        Assert.Equal(train.Id, world.PlatformOccupancy[reverse]);
        Assert.False(world.PlatformOccupancy.ContainsKey(forward));
        Assert.Equal("transfer", passengers.State);
        AssertConserved(world);
    }

    [Theory]
    [InlineData(false, 100)]
    [InlineData(true, 130)]
    public void Short_turn_requeues_onboard_passengers_until_a_through_train_completes_their_journey(
        bool accessible,
        int transferSeconds)
    {
        var world = IsolatedBlueWorld();
        var train = Position(world, "blue-1", "union-terminal", 1);
        var through = AddPassengers(world, "through", "onboard", "union-terminal", "eastgate", 12, accessible);
        through.ReturnAt = world.SimTime.AddHours(2);
        AddPassengers(world, "turnback", "onboard", "union-terminal", "east-junction", 4);
        train.Load = 16;
        Register(world, train);
        Assert.True(Apply(world, new("short-turn", train.Id, "east-junction")).Accepted);
        MoveToNextStop(world, train, exchange: false);
        var turnTime = world.SimTime = train.ReadyAt;

        var turn = Kernel.RequestMovement(world, Request(world, train));

        Assert.False(turn.Granted);
        Assert.Equal(-1, train.Direction);
        Assert.Null(train.ShortTurnAt);
        Assert.Equal(0, train.Load);
        Assert.Equal("transfer", through.State);
        Assert.Null(through.TrainId);
        Assert.Equal("east-junction", through.StationId);
        Assert.Equal("eastgate", through.Destination);
        Assert.Equal("east-junction", through.Legs[through.LegIndex].From);
        Assert.Equal("eastgate", through.Legs[through.LegIndex].To);
        Assert.Equal(turnTime.AddSeconds(transferSeconds), through.ReadyAt);
        Assert.False(through.ReturnScheduled);
        Assert.Equal(4, world.Metrics.Completed);
        Assert.DoesNotContain(world.Cohorts, cohort => cohort.Id == "through:return");
        AssertConserved(world);
        var reverseBoard = Arrival(world, "union-terminal");
        Assert.Equal("pinecrest", reverseBoard.Destination);
        Assert.NotNull(reverseBoard.Seconds);

        var following = Position(world, "blue-2", "east-junction", 1);
        Register(world, following);
        Kernel.Tick(world, [], transferSeconds - 1);
        Assert.Equal(0, following.Load);
        Kernel.Tick(world, [], 40);
        Assert.Equal(12, following.Load);
        Assert.Equal(4, world.Metrics.Completed);
        AssertConserved(world);

        MoveToNextStop(world, following);
        MoveToNextStop(world, following);

        Assert.Equal(16, world.Metrics.Completed);
        Assert.True(through.ReturnScheduled);
        Assert.All(world.Cohorts.Where(c => c.Id.StartsWith("through") && !c.Id.EndsWith(":return")),
            cohort => Assert.Equal("completed", cohort.State));
        AssertConserved(world);
    }

    [Fact]
    public void Live_through_journey_does_not_use_a_short_turn_arrival_as_its_wait()
    {
        var world = IsolatedBlueWorld();
        world.Trains.Single(t => t.Id == "blue-1").ReadyAt = world.SimTime.AddSeconds(90);
        Assert.True(Apply(world, new("short-turn", "blue-1", "east-junction")).Accepted);

        var itinerary = Kernel.Plan(world, new("pinecrest", "eastgate")).Options[0];
        var ride = Assert.Single(itinerary.Legs, leg => leg.Kind == "ride");

        Assert.True(ride.WaitSeconds >= BlueRoundTripTravel() / 2d);
        Assert.Contains(itinerary.Warnings, warning => warning.Contains("arrival", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Native_headway_is_a_regulation_floor_and_scheduled_wait_accounts_for_fleet_capacity()
    {
        var world = Kernel.Create();
        var result = Apply(world, new("set-headway", "Blue", Amount: 480));

        Assert.True(result.Accepted);
        Assert.Equal(480, world.Headways["Blue"]);
        Assert.Contains("regulation floor", result.Message, StringComparison.OrdinalIgnoreCase);
        var scheduled = ScheduledBlueRide(world);
        Assert.True(scheduled.WaitSeconds >= BlueRoundTripTravel() / 3d / 2);
        Assert.True(scheduled.WaitSeconds > 240);
        var itinerary = Kernel.Plan(world,
            new("pinecrest", "eastgate", DepartureTime: world.SimTime.AddHours(1))).Options[0];
        Assert.Equal("schedule", itinerary.EstimateKind);
        Assert.Contains(itinerary.Warnings, warning => warning.Contains("not guaranteed", StringComparison.OrdinalIgnoreCase));

        world.Trains.Single(t => t.Id == "blue-3").State = "withdrawn";
        Assert.True(ScheduledBlueRide(world).WaitSeconds > scheduled.WaitSeconds);
    }

    [Fact]
    public void A_longer_regulation_floor_still_sets_the_scheduled_wait_lower_bound()
    {
        var world = Kernel.Create();
        Assert.True(Apply(world, new("set-headway", "Blue", Amount: 1800)).Accepted);

        Assert.Equal(900, ScheduledBlueRide(world).WaitSeconds);
    }

    [Fact]
    public void Regulation_floor_accepts_a_request_below_cycle_capacity_without_promising_that_frequency()
    {
        var world = Kernel.Create();
        var result = Apply(world, new("set-headway", "AX", Amount: 600));

        Assert.True(result.Accepted);
        Assert.Contains("regulation floor", result.Message, StringComparison.OrdinalIgnoreCase);
        var itinerary = Kernel.Plan(world,
            new("airport", "central-exchange", DepartureTime: world.SimTime.AddHours(1))).Options[0];
        var ride = Assert.Single(itinerary.Legs, leg => leg.Kind == "ride");
        Assert.Equal("AX", ride.Line);
        Assert.True(ride.WaitSeconds >= (1080 + 105) / 2d);
        Assert.False(Apply(world, new("set-headway", "AX", Amount: 119)).Accepted);
    }

    private static WorldState IsolatedBlueWorld()
    {
        var world = Kernel.Create();
        foreach (var train in world.Trains) train.State = "withdrawn";
        world.BlockOccupancy.Clear();
        world.PlatformOccupancy.Clear();
        world.JunctionReservations.Clear();
        Position(world, "blue-1", "pinecrest", 1);
        return world;
    }

    private static TrainState Position(WorldState world, string trainId, string station, int direction)
    {
        var train = world.Trains.Single(t => t.Id == trainId);
        foreach (var key in world.PlatformOccupancy.Where(p => p.Value == trainId).Select(p => p.Key).ToArray())
            world.PlatformOccupancy.Remove(key);
        train.State = "dwelling";
        train.StationId = station;
        train.RouteIndex = Array.IndexOf(Network.Line("Blue").Stations, station);
        train.Direction = direction;
        train.ReadyAt = world.SimTime;
        train.Version++;
        world.PlatformOccupancy[Network.Platform(station, "Blue", direction)] = train.Id;
        return train;
    }

    private static Cohort AddPassengers(
        WorldState world, string id, string state, string from, string to, int count, bool accessible = false)
    {
        var cohort = new Cohort
        {
            Id = id,
            Origin = from,
            Destination = to,
            StationId = from,
            Count = count,
            Accessible = accessible,
            State = state,
            TrainId = state == "onboard" ? "blue-1" : null,
            CreatedAt = world.SimTime,
            ReadyAt = world.SimTime,
            PatienceSeconds = 7200,
            Legs = [new() { Kind = "ride", Line = "Blue", From = from, To = to }]
        };
        world.Cohorts.Add(cohort);
        world.Metrics.Generated += count;
        return cohort;
    }

    private static void Register(WorldState world, TrainState train) =>
        Assert.True(Kernel.Register(world, new(train.Id, $"worker:{train.Id}", world.ScenarioId, world.Generation)).Granted);

    private static void MoveToNextStop(WorldState world, TrainState train, bool exchange = true)
    {
        world.SimTime = train.ReadyAt > world.SimTime ? train.ReadyAt : world.SimTime;
        var result = Kernel.RequestMovement(world, Request(world, train));
        Assert.True(result.Granted, result.Reason);
        var grant = result.Grant!;
        world.SimTime = grant.ArrivesAt;
        Assert.True(Kernel.ConfirmMovement(world,
            new($"confirm:{grant.Id}", train.Id, train.WorkerInstanceId!, world.ScenarioId, world.Generation, grant.Id)).Granted);
        if (exchange) Kernel.Tick(world, [], 1);
    }

    private static MovementRequest Request(WorldState world, TrainState train) =>
        new(Guid.NewGuid().ToString("N"), train.Id, train.WorkerInstanceId!, world.ScenarioId, world.Generation, train.Version);

    private static CommandResult Apply(WorldState world, ServiceAction action) =>
        Kernel.Apply(world, new(Guid.NewGuid().ToString("N"), world.ScenarioId, world.Generation, world.Version, "test", "test", action));

    private static Arrival Arrival(WorldState world, string station) =>
        Assert.Single(Kernel.Board(world, station).Arrivals, arrival => arrival.TrainId == "blue-1");

    private static JourneyLeg ScheduledBlueRide(WorldState world) =>
        Assert.Single(Kernel.Plan(world, new("pinecrest", "eastgate", DepartureTime: world.SimTime.AddHours(1)))
            .Options[0].Legs, leg => leg.Kind == "ride");

    private static double BlueRoundTripTravel() => (Network.Line("Blue").Stations.Length - 1) * 115 * 2;

    private static void AssertConserved(WorldState world)
    {
        Assert.Equal(world.Metrics.Generated, world.Metrics.Completed + world.Metrics.Abandoned
            + world.Cohorts.Where(c => c.State is not "completed" and not "abandoned").Sum(c => c.Count));
        Assert.All(world.Trains, train =>
            Assert.Equal(train.Load, world.Cohorts.Where(c => c.State == "onboard" && c.TrainId == train.Id).Sum(c => c.Count)));
    }
}
