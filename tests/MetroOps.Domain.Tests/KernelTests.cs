using MetroOps.Contracts;
using MetroOps.Domain;
using Xunit;

namespace MetroOps.Domain.Tests;

public sealed class KernelTests
{
    [Fact]
    public void Create_builds_the_authoritative_twelve_train_fleet()
    {
        var world = Kernel.Create();

        Assert.Equal(
            ["amber-1", "amber-2", "amber-3", "ax-1", "ax-2", "blue-1", "blue-2", "blue-3", "green-1", "green-2", "reserve-ba", "reserve-green"],
            world.Trains.Select(t => t.Id).Order().ToArray());
        Assert.Equal(["Amber", "Blue"], world.Trains.Single(t => t.Id == "reserve-ba").CompatibleLines.Order());
        Assert.Equal(["Green"], world.Trains.Single(t => t.Id == "reserve-green").CompatibleLines);
        Assert.Equal("west-junction", world.Trains.Single(t => t.Id == "reserve-ba").Depot);
        Assert.Equal("southbank", world.Trains.Single(t => t.Id == "reserve-green").Depot);
        foreach (var definition in Fleet.Trains)
        {
            var train = Assert.Single(world.Trains, t => t.Id == definition.Id);
            Assert.Equal(definition.StationId, train.StationId);
            Assert.Equal(definition.RouteIndex, train.RouteIndex);
            Assert.Equal(definition.Direction, train.Direction);
            Assert.Equal(definition.Capacity, train.Capacity);
            Assert.Equal(definition.Line, train.Line);
            Assert.Equal(definition.Depot, train.Depot);
            Assert.Equal(definition.CompatibleLines, train.CompatibleLines);
            var stream = definition.Reserve ? "reserve-readiness" : "initial-dwell";
            var sample = Kernel.DeterministicUnit(world.Seed, $"{stream}:v{world.StreamVersion}", train.Id, 0);
            Assert.Equal(world.SimTime.AddSeconds(definition.Reserve ? 90 + (int)(sample * 180) : 20 + (int)(sample * 40)), train.ReadyAt);
            Assert.Equal(!definition.Reserve, world.PlatformOccupancy.ContainsValue(train.Id));
        }
    }

    [Fact]
    public void Create_uses_network_headway_defaults()
    {
        var world = Kernel.Create();

        Assert.Equal(
            Network.Definition.Lines.ToDictionary(line => line.Id, line => (double)line.HeadwaySeconds),
            world.Headways);
        Assert.Equal(1440, world.Headways["Amber"]);
        Assert.Equal(1440, world.Headways["AX"]);
    }

    [Fact]
    public void Create_starts_new_scenarios_at_city_peak_without_retiming_explicit_start_times()
    {
        var world = Kernel.Create();
        var explicitStart = DateTimeOffset.Parse("2026-11-17T07:30:00Z");
        var restoredClock = Kernel.Create(startTime: explicitStart);

        Assert.Equal(DateTimeOffset.Parse("2026-11-17T12:30:00Z"), world.SimTime);
        Assert.Contains("07:30 America/New_York", world.History[0].Description);
        Assert.Equal(explicitStart, restoredClock.SimTime);
    }

    [Theory]
    [InlineData("AX", "airport", "central-exchange", 0, 1, 1080)]
    [InlineData("AX", "central-exchange", "airport", 1, -1, 1080)]
    [InlineData("Amber", "airport", "aerocity", 0, 1, 540)]
    [InlineData("Amber", "aerocity", "airport", 1, -1, 540)]
    [InlineData("Amber", "aerocity", "west-junction", 1, 1, 480)]
    [InlineData("Amber", "west-junction", "aerocity", 2, -1, 480)]
    public void Movement_grants_use_asymmetric_segment_times_in_both_directions(
        string line,
        string from,
        string to,
        int routeIndex,
        int direction,
        int expectedSeconds)
    {
        var world = Kernel.Create();
        var trainId = line == "AX" ? "ax-1" : "amber-1";
        foreach (var other in world.Trains.Where(t => t.Id != trainId)) other.State = "withdrawn";
        world.BlockOccupancy.Clear();
        world.PlatformOccupancy.Clear();
        world.JunctionReservations.Clear();
        Position(world, trainId, line, from, routeIndex, direction).ReadyAt = world.SimTime;
        Register(world, trainId, "worker");

        var movement = Kernel.RequestMovement(world, Request(world, trainId, "worker", "segment"));

        Assert.True(movement.Granted, movement.Reason);
        Assert.Equal(to, movement.Grant!.To);
        Assert.Equal(expectedSeconds, (movement.Grant.ArrivesAt - movement.Grant.DepartsAt).TotalSeconds);
    }

    [Fact]
    public void Airport_segment_totals_are_eighteen_minute_express_and_twenty_one_minute_local()
    {
        var express = Network.TravelSeconds("AX", "airport", "central-exchange");
        var localStops = new[] { "airport", "aerocity", "west-junction", "civic-square", "central-exchange" };
        var local = localStops.Zip(localStops.Skip(1), (from, to) => Network.TravelSeconds("Amber", from, to)).Sum();

        Assert.Equal(18 * 60, express);
        Assert.Equal(21 * 60, local);
    }

    [Fact]
    public void Accepted_headway_change_delays_and_then_allows_matching_real_departure()
    {
        var (checkpoint, firstDeparture) = HeadwayCheckpoint();
        var baseline = Wire.Clone(checkpoint);
        var regulated = Wire.Clone(checkpoint);
        var command = Kernel.Apply(regulated, Command(regulated, "set-headway", "Blue", amount: 900));
        baseline.SimTime = firstDeparture.AddSeconds(480);
        regulated.SimTime = firstDeparture.AddSeconds(480);

        var baselineMovement = Kernel.RequestMovement(
            baseline,
            Request(baseline, "blue-2", "blue-2-worker", "baseline-departure"));
        var delayed = Kernel.RequestMovement(
            regulated,
            Request(regulated, "blue-2", "blue-2-worker", "regulated-departure"));
        var regulatedBoard = Kernel.Board(regulated, "pinecrest").Arrivals.Single(arrival => arrival.TrainId == "blue-2");
        regulated.SimTime = firstDeparture.AddSeconds(900);
        var allowed = Kernel.RequestMovement(
            regulated,
            Request(regulated, "blue-2", "blue-2-worker", "regulated-departure"));

        Assert.True(command.Accepted);
        Assert.True(baselineMovement.Granted, baselineMovement.Reason);
        Assert.False(delayed.Granted);
        Assert.Contains("headway", delayed.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(420, regulatedBoard.Seconds);
        Assert.True(allowed.Granted, allowed.Reason);
    }

    [Fact]
    public void Compare_applies_candidate_headway_to_real_headless_movements()
    {
        var world = Kernel.Create(6543);
        foreach (var other in world.Trains.Where(t => t.Id is not "blue-1" and not "blue-2")) other.State = "withdrawn";
        world.BlockOccupancy.Clear();
        world.PlatformOccupancy.Clear();
        world.JunctionReservations.Clear();
        Position(world, "blue-1", "Blue", "pinecrest", 0, 1).ReadyAt = world.SimTime;
        Position(world, "blue-2", "Blue", "west-junction", 2, -1).ReadyAt = world.SimTime;
        var samples = Enumerable.Range(0, 8)
            .Select(sample => new ForecastSample(
                30_000 + sample,
                Enumerable.Range(0, 30)
                    .Select(minute => new DemandBatch(
                        $"headway-{sample}-{minute}",
                        "pinecrest",
                        "westhaven",
                        10,
                        false,
                        world.SimTime.AddMinutes(minute).AddSeconds(1),
                        1800))
                    .ToList(),
                []))
            .ToArray();

        var result = Kernel.Compare(world, new("set-headway", "Blue", Amount: 1800), samples);

        Assert.True(result.Baseline.Completed.Mean > result.Intervention.Completed.Mean);
    }

    [Fact]
    public void Shared_trunk_grants_reserve_block_platform_and_junction_atomically()
    {
        var world = Kernel.Create();
        Position(world, "blue-1", "Blue", "west-junction", 2, 1);
        Position(world, "amber-1", "Amber", "west-junction", 2, 1);
        foreach (var train in world.Trains.Where(t => t.Id is not "blue-1" and not "amber-1")) train.State = "withdrawn";
        world.BlockOccupancy.Clear();
        world.PlatformOccupancy.Clear();
        world.JunctionReservations.Clear();
        Register(world, "blue-1", "blue-worker");
        Register(world, "amber-1", "amber-worker");

        var blue = Kernel.RequestMovement(world, Request(world, "blue-1", "blue-worker", "blue-request"));
        var amber = Kernel.RequestMovement(world, Request(world, "amber-1", "amber-worker", "amber-request"));

        Assert.True(blue.Granted);
        Assert.False(amber.Granted);
        Assert.Contains("reserved", amber.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("blue-1", world.BlockOccupancy["BA:west-junction>civic-square"]);
        Assert.Equal("blue-1", world.PlatformOccupancy["civic-square:BA:+"]);
        Assert.Equal("blue-1", world.JunctionReservations["west-junction:BA:+"]);
    }

    [Fact]
    public void Repeated_request_and_registration_reconcile_the_same_grant()
    {
        var world = Kernel.Create();
        Position(world, "blue-1", "Blue", "pinecrest", 0, 1);
        Register(world, "blue-1", "worker");
        var request = Request(world, "blue-1", "worker", "same-request");

        var first = Kernel.RequestMovement(world, request);
        var retry = Kernel.RequestMovement(world, request);
        var registration = Kernel.Register(world, new("blue-1", "worker", world.ScenarioId, world.Generation));

        Assert.True(first.Granted);
        Assert.Equal(first.Grant!.Id, retry.Grant!.Id);
        Assert.Equal(first.Grant.Id, registration.Grant!.Id);
        Assert.Single(world.Grants);
    }

    [Fact]
    public void Confirmation_is_idempotent_and_releases_only_cleared_resources()
    {
        var world = Kernel.Create();
        Position(world, "blue-1", "Blue", "pinecrest", 0, 1);
        Register(world, "blue-1", "worker");
        var request = Request(world, "blue-1", "worker", "move");
        var grant = Kernel.RequestMovement(world, request).Grant!;
        world.SimTime = grant.ArrivesAt;
        var confirmation = new MovementConfirmation(request.RequestId, "blue-1", "worker", world.ScenarioId, world.Generation, grant.Id);

        var first = Kernel.ConfirmMovement(world, confirmation);
        var retry = Kernel.ConfirmMovement(world, confirmation);

        Assert.True(first.Granted);
        Assert.True(retry.Granted);
        Assert.Equal("westhaven", first.Train!.StationId);
        Assert.False(world.BlockOccupancy.ContainsKey(grant.Block));
        Assert.Equal("blue-1", world.PlatformOccupancy[grant.Platform]);
        Assert.Equal(first.Train.Version, retry.Train!.Version);
    }

    [Fact]
    public void Confirmation_request_id_is_independent_from_the_movement_request_id()
    {
        var world = Kernel.Create();
        Position(world, "blue-1", "Blue", "pinecrest", 0, 1);
        Register(world, "blue-1", "worker");
        var grant = Kernel.RequestMovement(world, Request(world, "blue-1", "worker", "movement-request")).Grant!;
        world.SimTime = grant.ArrivesAt;
        var confirmation = new MovementConfirmation(
            $"confirm:{grant.Id}",
            "blue-1",
            "worker",
            world.ScenarioId,
            world.Generation,
            grant.Id);

        var first = Kernel.ConfirmMovement(world, confirmation);
        var retry = Kernel.ConfirmMovement(world, confirmation);

        Assert.True(first.Granted);
        Assert.True(retry.Granted);
        Assert.Equal("westhaven", first.Train!.StationId);
        Assert.True(grant.Completed);
    }

    [Fact]
    public void Stale_worker_messages_and_versions_are_rejected()
    {
        var world = Kernel.Create();
        Position(world, "blue-1", "Blue", "pinecrest", 0, 1);
        Register(world, "blue-1", "worker");
        var train = world.Trains.Single(t => t.Id == "blue-1");

        Assert.False(Kernel.RequestMovement(world, new("a", train.Id, "worker", "old", world.Generation, train.Version)).Granted);
        Assert.False(Kernel.RequestMovement(world, new("b", train.Id, "worker", world.ScenarioId, world.Generation + 1, train.Version)).Granted);
        Assert.False(Kernel.RequestMovement(world, new("c", train.Id, "worker", world.ScenarioId, world.Generation, train.Version - 1)).Granted);
        Assert.False(Kernel.RequestMovement(world, new("d", train.Id, "other-worker", world.ScenarioId, world.Generation, train.Version)).Granted);
    }

    [Fact]
    public void Live_worker_requests_keep_service_running_beyond_two_route_cycles()
    {
        var world = Kernel.Create(9876);
        foreach (var other in world.Trains.Where(t => t.Id != "blue-1")) other.State = "withdrawn";
        world.BlockOccupancy.Clear();
        world.PlatformOccupancy.Clear();
        world.JunctionReservations.Clear();
        var train = Position(world, "blue-1", "Blue", "pinecrest", 0, 1);
        train.ReadyAt = world.SimTime;
        world.Cohorts.Add(new()
        {
            Id = "finite-riders",
            Origin = "pinecrest",
            Destination = "eastgate",
            StationId = "pinecrest",
            Count = 20,
            State = "platform",
            CreatedAt = world.SimTime,
            ReadyAt = world.SimTime,
            Legs = [new() { Kind = "ride", Line = "Blue", From = "pinecrest", To = "eastgate" }]
        });
        world.Metrics.Generated = 20;
        Kernel.Tick(world, [], 1);
        Register(world, train.Id, "live-worker");

        var confirmations = 0;
        var directionChanges = 0;
        var previousDirection = train.Direction;
        for (var attempt = 0; attempt < 200 && confirmations < 40; attempt++)
        {
            if (train.ReadyAt > world.SimTime) world.SimTime = train.ReadyAt;
            var requestId = $"{world.ScenarioId}:{train.Id}:{train.Version}";
            var movement = Kernel.RequestMovement(world, Request(world, train.Id, "live-worker", requestId));
            if (train.Direction != previousDirection)
            {
                directionChanges++;
                previousDirection = train.Direction;
            }
            if (!movement.Granted) continue;

            world.SimTime = movement.Grant!.ArrivesAt;
            var confirmation = Kernel.ConfirmMovement(world, new(
                $"confirm:{movement.Grant.Id}",
                train.Id,
                "live-worker",
                world.ScenarioId,
                world.Generation,
                movement.Grant.Id));
            Assert.True(confirmation.Granted, confirmation.Reason);
            confirmations++;
            Kernel.Tick(world, [], 1);
        }

        Assert.True(confirmations >= 40, $"Only {confirmations} movements completed.");
        Assert.True(directionChanges >= 4, $"Only {directionChanges} terminal turns completed.");
        Assert.Equal(20, world.Metrics.Completed);
        Assert.Equal(0, train.Load);
        Assert.Equal(20, Conserved(world));
    }

    [Fact]
    public void Frequent_same_worker_heartbeats_coalesce_but_refresh_before_stale_detection()
    {
        var world = Kernel.Create();
        Register(world, "blue-1", "worker");
        var train = world.Trains.Single(t => t.Id == "blue-1");
        var recent = DateTimeOffset.UtcNow.AddSeconds(-10);
        train.LastHeartbeatAt = recent;

        Register(world, "blue-1", "worker");

        Assert.Equal(recent, train.LastHeartbeatAt);
        train.LastHeartbeatAt = DateTimeOffset.UtcNow.AddSeconds(-40);
        Register(world, "blue-1", "worker");
        Assert.True(train.LastHeartbeatAt >= DateTimeOffset.UtcNow.AddSeconds(-2));
        Kernel.Tick(world, [], 1);
        Assert.NotEqual("uncertain", train.State);
    }

    [Fact]
    public void Repeated_denied_departure_does_not_change_an_already_fresh_train()
    {
        var world = Kernel.Create();
        Register(world, "blue-1", "worker");
        var train = world.Trains.Single(t => t.Id == "blue-1");
        train.ReadyAt = world.SimTime.AddMinutes(1);
        var before = System.Text.Json.JsonSerializer.Serialize(world, Wire.Json);

        var result = Kernel.RequestMovement(world, Request(world, train.Id, "worker", "not-ready"));

        Assert.False(result.Granted);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(world, Wire.Json));
    }

    [Fact]
    public void Missing_heartbeat_never_releases_reserved_occupancy()
    {
        var world = Kernel.Create(startTime: DateTimeOffset.Parse("2026-11-17T07:30:00Z"));
        Position(world, "blue-1", "Blue", "pinecrest", 0, 1);
        Register(world, "blue-1", "worker");
        var grant = Kernel.RequestMovement(world, Request(world, "blue-1", "worker", "move")).Grant!;
        world.Trains.Single(t => t.Id == "blue-1").LastHeartbeatAt = DateTimeOffset.UtcNow.AddMinutes(-5);

        Kernel.Tick(world, [], 60);

        Assert.Equal("uncertain", world.Trains.Single(t => t.Id == "blue-1").State);
        Assert.Equal("blue-1", world.BlockOccupancy[grant.Block]);
        Assert.Equal("blue-1", world.PlatformOccupancy[grant.Platform]);
    }

    [Fact]
    public void Worker_takeover_retains_occupancy_until_the_new_instance_confirms_progress()
    {
        var world = Kernel.Create();
        Position(world, "blue-1", "Blue", "pinecrest", 0, 1);
        Register(world, "blue-1", "old-worker");
        var grant = Kernel.RequestMovement(world, Request(world, "blue-1", "old-worker", "move")).Grant!;

        var takeover = Kernel.Register(world, new("blue-1", "new-worker", world.ScenarioId, world.Generation));
        var takeoverState = takeover.Train!.State;
        var early = Kernel.ConfirmMovement(world, new("move", "blue-1", "new-worker", world.ScenarioId, world.Generation, grant.Id));
        world.SimTime = grant.ArrivesAt;
        var confirmed = Kernel.ConfirmMovement(world, new("move", "blue-1", "new-worker", world.ScenarioId, world.Generation, grant.Id));

        Assert.Equal(grant.Id, takeover.Grant!.Id);
        Assert.Equal("uncertain", takeoverState);
        Assert.False(early.Granted);
        Assert.True(confirmed.Granted);
        Assert.Equal("westhaven", confirmed.Train!.StationId);
    }

    [Fact]
    public void Boarding_splits_cohorts_without_losing_passengers()
    {
        var world = Kernel.Create();
        var train = world.Trains.Single(t => t.Id == "blue-1");
        Position(world, train.Id, "Blue", "pinecrest", 0, 1);
        train.Capacity = 100;
        train.ReadyAt = world.SimTime.AddMinutes(5);
        world.Cohorts.Add(new()
        {
            Id = "cohort",
            Origin = "pinecrest",
            Destination = "eastgate",
            StationId = "pinecrest",
            Count = 140,
            State = "platform",
            CreatedAt = world.SimTime,
            ReadyAt = world.SimTime,
            Legs = [new() { Kind = "ride", Line = "Blue", From = "pinecrest", To = "eastgate" }]
        });
        world.Metrics.Generated = 140;

        Kernel.Tick(world, [], 1);

        Assert.Equal(100, train.Load);
        Assert.Equal(40, world.Cohorts.Where(c => c.State == "platform").Sum(c => c.Count));
        Assert.Equal(100, world.Cohorts.Where(c => c.State == "onboard").Sum(c => c.Count));
        Assert.Equal(140, Conserved(world));
    }

    [Fact]
    public void Transfer_flow_preserves_every_passenger_and_train_load()
    {
        var world = Kernel.Create();
        var blue = Position(world, "blue-1", "Blue", "central-exchange", 4, 1);
        blue.Load = 20;
        world.Cohorts.Add(new()
        {
            Id = "transfer",
            Origin = "pinecrest",
            Destination = "medical-center",
            StationId = "central-exchange",
            Count = 20,
            State = "onboard",
            TrainId = blue.Id,
            CreatedAt = world.SimTime,
            ReadyAt = world.SimTime,
            Legs =
            [
                new() { Kind = "ride", Line = "Blue", From = "pinecrest", To = "central-exchange" },
                new() { Kind = "ride", Line = "Green", From = "central-exchange", To = "medical-center" }
            ]
        });
        world.Metrics.Generated = 20;

        Kernel.Tick(world, [], 1);
        var transfer = world.Cohorts.Single(c => c.Id == "transfer");
        Assert.Equal("transfer", transfer.State);
        Assert.Equal(0, blue.Load);
        Assert.Equal(20, Conserved(world));

        transfer.ReadyAt = world.SimTime;
        var green = Position(world, "green-1", "Green", "central-exchange", 4, 1);
        green.ReadyAt = world.SimTime.AddMinutes(5);
        Kernel.Tick(world, [], 20);

        Assert.Equal(20, green.Load);
        Assert.Equal(20, world.Cohorts.Where(c => c.TrainId == green.Id).Sum(c => c.Count));
        Assert.Equal(20, Conserved(world));
    }

    [Fact]
    public void Linked_return_stays_conserved_without_waiting_or_boarding_before_ready()
    {
        var world = FutureReturnWorld();
        var scheduledReturn = world.Cohorts.Single(c => c.Id == "outbound:return");
        var waitingBefore = world.Metrics.WaitingPassengerSeconds;

        Kernel.Tick(world, [], 60);

        Assert.Equal("outside", scheduledReturn.State);
        Assert.False(Kernel.IsWaitingAt(scheduledReturn, world.SimTime));
        Assert.Equal(waitingBefore, world.Metrics.WaitingPassengerSeconds);
        Assert.Equal(20, Conserved(world));

        world.SimTime = scheduledReturn.ReadyAt;
        Kernel.Tick(world, [], 1);

        Assert.True(Kernel.IsWaitingAt(scheduledReturn, world.SimTime));
        Assert.Equal(10, world.Cohorts.Where(c => Kernel.IsWaitingAt(c, world.SimTime)).Sum(c => c.Count));
        Assert.Equal(10, world.Cohorts.Where(c => c.State is "outside" or "platform").Sum(c => c.Count));
        Assert.Equal(20, Conserved(world));
    }

    [Fact]
    public void Forecast_remaining_queue_excludes_future_scheduled_returns()
    {
        var world = FutureReturnWorld();
        var ready = new Cohort
        {
            Id = "ready-queue",
            Origin = "central-exchange",
            Destination = "union-terminal",
            StationId = "central-exchange",
            Count = 5,
            State = "outside",
            CreatedAt = world.SimTime,
            ReadyAt = world.SimTime,
            PatienceSeconds = 7200,
            Legs = [new() { Kind = "ride", Line = "Blue", From = "central-exchange", To = "union-terminal" }]
        };
        world.Cohorts.Add(ready);
        world.Metrics.Generated += ready.Count;
        Kernel.Apply(world, Command(world, "incident", "central-exchange", "station-closure", 3600));
        var samples = Enumerable.Range(0, 8).Select(index => new ForecastSample(40_000 + index, [], [])).ToArray();

        var result = Kernel.Compare(world, new("hold-train", "blue-1", Amount: 60), samples);

        Assert.Equal(5, result.Baseline.RemainingQueue.Mean);
        Assert.Equal(5, result.Intervention.RemainingQueue.Mean);
    }

    [Fact]
    public void Demand_ids_are_deduplicated_and_access_throughput_is_separate_from_platform_capacity()
    {
        var world = Kernel.Create();
        var batch = new DemandBatch("batch-1", "pinecrest", "eastgate", 300, false, world.SimTime, 1800);

        Kernel.Tick(world, [batch, batch], 60);

        Assert.Equal(300, world.Metrics.Generated);
        Assert.Equal(120, world.Cohorts.Where(c => c.State == "platform").Sum(c => c.Count));
        Assert.Equal(180, world.Cohorts.Where(c => c.State == "outside").Sum(c => c.Count));
        Assert.Equal(300, Conserved(world));
    }

    [Fact]
    public void Elevator_outage_removes_only_the_accessible_path()
    {
        var world = Kernel.Create();
        var opened = Kernel.Apply(world, Command(world, "incident", "central-exchange", "elevator-outage", 600));

        var accessible = Kernel.Plan(world, new("central-exchange", "union-terminal", true));
        var ordinary = Kernel.Plan(world, new("central-exchange", "union-terminal"));

        Assert.True(opened.Accepted);
        Assert.Empty(accessible.Options);
        Assert.NotNull(accessible.Error);
        Assert.NotEmpty(ordinary.Options);
    }

    [Fact]
    public void Airport_express_is_never_routed_to_aerocity()
    {
        var world = Kernel.Create();

        var journey = Kernel.Plan(world, new("airport", "aerocity"));

        Assert.NotEmpty(journey.Options);
        Assert.DoesNotContain(journey.Options.SelectMany(i => i.Legs), leg => leg.Line == "AX");
    }

    [Fact]
    public void Commands_are_idempotent_and_reject_stale_scenario_generation_and_version()
    {
        var world = Kernel.Create();
        var command = Command(world, "hold-train", "blue-1", amount: 90);

        var first = Kernel.Apply(world, command);
        var retry = Kernel.Apply(world, command);
        var wrongScenario = Kernel.Apply(world, command with { CommandId = "wrong-scenario", ScenarioId = "old" });
        var wrongGeneration = Kernel.Apply(world, command with { CommandId = "wrong-generation", Generation = world.Generation + 1 });
        var wrongVersion = Kernel.Apply(world, command with { CommandId = "wrong-version", ExpectedVersion = world.Version + 1 });

        Assert.True(first.Accepted);
        Assert.Same(first, retry);
        Assert.False(wrongScenario.Accepted);
        Assert.False(wrongGeneration.Accepted);
        Assert.False(wrongVersion.Accepted);
    }

    [Fact]
    public void Commands_tolerate_harmless_clock_version_drift_but_reject_material_staleness()
    {
        var world = Kernel.Create();
        world.Version = 500;

        var tolerated = Kernel.Apply(world, Command(world, "hold-train", "blue-1", amount: 90) with
        {
            CommandId = "tolerated",
            ExpectedVersion = 350
        });
        var stale = Kernel.Apply(world, Command(world, "release-train", "blue-1") with
        {
            CommandId = "stale",
            ExpectedVersion = 299
        });

        Assert.True(tolerated.Accepted);
        Assert.False(stale.Accepted);
        Assert.Contains("materially stale", stale.Message);
    }

    [Fact]
    public void Clearing_one_incident_preserves_unrelated_restrictions()
    {
        var world = Kernel.Create();
        var first = Kernel.Apply(world, Command(world, "incident", "central-exchange", "platform-closure", 600));
        var second = Kernel.Apply(world, Command(world, "incident", "central-exchange", "elevator-outage", 600));

        Kernel.Apply(world, Command(world, "clear-incident", first.EntityId!));

        Assert.Equal("cleared", world.Incidents.Single(i => i.Id == first.EntityId).State);
        Assert.Equal("active", world.Incidents.Single(i => i.Id == second.EntityId).State);
        Assert.Contains(Kernel.Board(world, "central-exchange").Restrictions, r => r.Contains("elevator", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Operational_actions_validate_and_apply_the_supported_lifecycle()
    {
        var world = Kernel.Create();
        world.Trains.Single(t => t.Id == "reserve-ba").ReadyAt = world.SimTime;

        var deploy = Kernel.Apply(world, Command(world, "deploy-reserve", "reserve-ba", "Blue"));
        var turn = Kernel.Apply(world, Command(world, "short-turn", "blue-1", "east-junction"));
        var headway = Kernel.Apply(world, Command(world, "set-headway", "Blue", amount: 600));
        var close = Kernel.Apply(world, Command(world, "incident", "west-junction>civic-square", "segment-closure", 60));
        var clear = Kernel.Apply(world, Command(world, "clear-incident", close.EntityId!));
        var withdraw = Kernel.Apply(world, Command(world, "withdraw-train", "amber-1"));
        var unknown = Kernel.Apply(world, Command(world, "teleport-train", "blue-1"));

        Assert.True(deploy.Accepted);
        Assert.Equal("Blue", world.Trains.Single(t => t.Id == "reserve-ba").Line);
        Assert.True(turn.Accepted);
        Assert.Equal("east-junction", world.Trains.Single(t => t.Id == "blue-1").ShortTurnAt);
        Assert.True(headway.Accepted);
        Assert.Equal(600, world.Headways["Blue"]);
        Assert.True(close.Accepted);
        Assert.True(clear.Accepted);
        Assert.Equal("cleared", world.Incidents.Single(i => i.Id == close.EntityId).State);
        Assert.True(withdraw.Accepted);
        Assert.Equal("withdrawn", world.Trains.Single(t => t.Id == "amber-1").State);
        Assert.False(unknown.Accepted);
    }

    [Fact]
    public void Timed_hold_releases_without_a_wall_clock_dependency()
    {
        var world = Kernel.Create();
        world.Trains.Single(t => t.Id == "blue-1").ReadyAt = world.SimTime;
        var result = Kernel.Apply(world, Command(world, "hold-train", "blue-1", amount: 2));

        Kernel.Tick(world, [], 2);

        Assert.True(result.Accepted);
        Assert.Equal("dwelling", world.Trains.Single(t => t.Id == "blue-1").State);
        Assert.Null(world.Trains.Single(t => t.Id == "blue-1").HoldReason);
    }

    [Fact]
    public void Simulated_clock_and_random_opportunities_are_callback_step_invariant()
    {
        var oneStep = Kernel.Create(42);
        var manySteps = Kernel.Create(42);
        var demandAt = oneStep.SimTime.AddSeconds(15);
        var demand = new DemandBatch("step-demand", "pinecrest", "eastgate", 50, false, demandAt, 1800);

        Kernel.Tick(oneStep, [demand], 600);
        for (var i = 0; i < 60; i++) Kernel.Tick(manySteps, [demand], 10);

        Assert.Equal(oneStep.SimTime, manySteps.SimTime);
        Assert.Equal(Snapshot(oneStep), Snapshot(manySteps));
        Assert.Equal(
            Kernel.DeterministicUnit(42, "train-failure", "blue-1", 12),
            Kernel.DeterministicUnit(42, "train-failure", "blue-1", 12));
        Assert.NotEqual(
            Kernel.DeterministicUnit(42, "train-failure", "blue-1", 12),
            Kernel.DeterministicUnit(42, "train-failure", "amber-1", 12));
    }

    [Fact]
    public void Board_uses_unknown_eta_for_uncertain_trains_and_schedule_labels_for_future_plans()
    {
        var world = Kernel.Create();
        var train = world.Trains.Single(t => t.Id == "blue-1");
        Position(world, train.Id, "Blue", "pinecrest", 0, 1);
        train.State = "uncertain";

        var board = Kernel.Board(world, "westhaven");
        var future = Kernel.Plan(world, new("pinecrest", "eastgate", DepartureTime: world.SimTime.AddHours(1)));

        Assert.Contains(board.Arrivals, a => a.TrainId == train.Id && a.Seconds is null && a.PredictionKind == "unknown");
        Assert.All(future.Options, option => Assert.Equal("schedule", option.EstimateKind));
    }

    [Fact]
    public void Board_does_not_publish_an_overdue_unconfirmed_grant_as_zero_seconds()
    {
        var world = Kernel.Create();
        Position(world, "blue-1", "Blue", "pinecrest", 0, 1);
        Register(world, "blue-1", "worker");
        var grant = Kernel.RequestMovement(world, Request(world, "blue-1", "worker", "overdue")).Grant!;
        world.SimTime = grant.ArrivesAt.AddSeconds(30);

        var arrival = Kernel.Board(world, "westhaven").Arrivals.Single(a => a.TrainId == "blue-1");

        Assert.Null(arrival.Seconds);
        Assert.Equal("unknown", arrival.PredictionKind);
        Assert.Contains("confirmation", arrival.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Live_routing_waits_at_actual_boarding_times_and_counts_intermediate_dwells()
    {
        var world = Kernel.Create(startTime: DateTimeOffset.Parse("2026-11-17T07:35:47Z"));
        var green = Position(world, "green-1", "Green", "university", 1, 1);
        green.ReadyAt = world.SimTime;
        world.Trains.Single(t => t.Id == "green-2").State = "withdrawn";
        var ax = Position(world, "ax-1", "AX", "central-exchange", 1, -1);
        ax.ReadyAt = world.SimTime;
        world.Trains.Single(t => t.Id == "ax-2").State = "withdrawn";

        var journey = Kernel.Plan(world, new("university", "airport", true));
        var itinerary = Assert.Single(journey.Options);
        var rides = itinerary.Legs.Where(l => l.Kind == "ride").ToArray();

        Assert.Equal(["Green", "AX"], rides.Select(r => r.Line));
        Assert.All(rides, ride => Assert.True(ride.WaitSeconds > 0));
        Assert.Contains(rides, ride =>
        {
            var travelOnly = ride.Stops.Zip(ride.Stops.Skip(1), (from, to) => Network.TravelSeconds(ride.Line!, from, to)).Sum();
            return ride.Stops.Count > 2 && ride.RideSeconds > travelOnly;
        });
    }

    [Fact]
    public void Live_routing_reports_stale_boards_and_unknown_train_positions()
    {
        var world = Kernel.Create();
        world.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var green = Position(world, "green-1", "Green", "university", 1, 1);
        green.State = "uncertain";
        world.Trains.Single(t => t.Id == "green-2").State = "withdrawn";

        var journey = Kernel.Plan(world, new("university", "medical-center", true));

        var itinerary = Assert.Single(journey.Options);
        Assert.Contains(itinerary.Warnings, warning => warning.Contains("stale", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(itinerary.Warnings, warning => warning.Contains("uncertain", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compare_clones_the_checkpoint_and_runs_eight_paired_thirty_minute_samples()
    {
        var world = Kernel.Create(123);
        var before = Wire.Clone(world);

        var result = Kernel.Compare(world, new("hold-train", "blue-1", Amount: 120));

        Assert.Equal(30, result.HorizonMinutes);
        Assert.Equal(8, result.Samples);
        Assert.Equal(before.SimTime, world.SimTime);
        Assert.Equal(before.Trains.Select(t => t.Version), world.Trains.Select(t => t.Version));
        Assert.True(result.Baseline.Completed.Min <= result.Baseline.Completed.Mean);
        Assert.True(result.Baseline.Completed.Mean <= result.Baseline.Completed.Max);
        Assert.Contains("synthetic", result.Method, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void Compare_from_a_paused_checkpoint_runs_the_full_authoritative_horizon()
    {
        var world = Kernel.Create(321);
        world.Paused = true;
        Kernel.Apply(world, Command(world, "incident", "central-exchange", "station-closure", 3600));
        var checkpointTime = world.SimTime;
        var samples = Enumerable.Range(0, 8)
            .Select(index => new ForecastSample(
                10_000 + index,
                [
                    new(
                        $"late-{index}",
                        "central-exchange",
                        "union-terminal",
                        10,
                        false,
                        checkpointTime.AddMinutes(29),
                        30)
                ],
                [new($"flight-{index}", "arrival", checkpointTime.AddMinutes(20), checkpointTime.AddMinutes(20), 100, false, "clear")]))
            .ToArray();

        var result = Kernel.Compare(world, new("hold-train", "blue-1", Amount: 60), samples);

        Assert.True(world.Paused);
        Assert.Equal(checkpointTime, world.SimTime);
        Assert.Equal(10, result.Baseline.Abandoned.Mean);
        Assert.True(result.Baseline.WaitingMinutes.Mean > 0);
        Assert.Contains("authoritative", result.Method, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("pure kernel", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compare_rejects_a_candidate_that_cannot_be_applied_at_the_checkpoint()
    {
        var world = Kernel.Create();
        var samples = Enumerable.Range(0, 8)
            .Select(index => new ForecastSample(index, [], []))
            .ToArray();

        var error = Assert.Throws<ArgumentException>(() =>
            Kernel.Compare(world, new("deploy-reserve", "reserve-ba", "AX"), samples));

        Assert.Contains("rejected", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compare_handles_authoritative_thirty_minute_forecast_volume()
    {
        var world = Kernel.Create(456);
        var stations = Network.Definition.Stations.Select(s => s.Id).ToArray();
        var samples = Enumerable.Range(0, 8)
            .Select(sample =>
            {
                var demand = new List<DemandBatch>(540);
                for (var minute = 0; minute < 30; minute++)
                {
                    for (var cohort = 0; cohort < 18; cohort++)
                    {
                        var originIndex = (minute * 18 + cohort) % stations.Length;
                        var destinationIndex = (originIndex + 5 + sample) % stations.Length;
                        if (destinationIndex == originIndex) destinationIndex = (destinationIndex + 1) % stations.Length;
                        demand.Add(new(
                            $"peak-{sample}-{minute}-{cohort}",
                            stations[originIndex],
                            stations[destinationIndex],
                            1,
                            cohort % 10 == 0,
                            world.SimTime.AddMinutes(minute).AddSeconds(1 + cohort % 59),
                            1800));
                    }
                }
                return new ForecastSample(20_000 + sample, demand, []);
            })
            .ToArray();

        var result = Kernel.Compare(world, new("hold-train", "blue-1", Amount: 60), samples);

        Assert.Equal(8, result.Samples);
        Assert.Equal(30, result.HorizonMinutes);
        Assert.True(result.Baseline.WaitingMinutes.Mean > 0);
    }

    private static TrainState Position(WorldState world, string id, string line, string station, int routeIndex, int direction)
    {
        var train = world.Trains.Single(t => t.Id == id);
        train.Line = line;
        train.State = "dwelling";
        train.StationId = station;
        train.RouteIndex = routeIndex;
        train.Direction = direction;
        train.NextStationId = null;
        train.ActiveGrantId = null;
        train.ReadyAt = world.SimTime;
        train.Version++;
        world.BlockOccupancy = world.BlockOccupancy.Where(x => x.Value != id).ToDictionary();
        world.PlatformOccupancy = world.PlatformOccupancy.Where(x => x.Value != id).ToDictionary();
        world.JunctionReservations = world.JunctionReservations.Where(x => x.Value != id).ToDictionary();
        world.PlatformOccupancy[Network.Platform(station, line, direction)] = id;
        return train;
    }

    private static (WorldState World, DateTimeOffset FirstDeparture) HeadwayCheckpoint()
    {
        var world = Kernel.Create();
        foreach (var other in world.Trains.Where(t => t.Id is not "blue-1" and not "blue-2")) other.State = "withdrawn";
        world.BlockOccupancy.Clear();
        world.PlatformOccupancy.Clear();
        world.JunctionReservations.Clear();
        Position(world, "blue-1", "Blue", "pinecrest", 0, 1).ReadyAt = world.SimTime;
        Register(world, "blue-1", "blue-1-worker");
        var first = Kernel.RequestMovement(world, Request(world, "blue-1", "blue-1-worker", "first")).Grant!;
        world.SimTime = first.ArrivesAt;
        Assert.True(Kernel.ConfirmMovement(world, new(
            "confirm:first",
            "blue-1",
            "blue-1-worker",
            world.ScenarioId,
            world.Generation,
            first.Id)).Granted);
        world.Trains.Single(t => t.Id == "blue-1").ReadyAt = world.SimTime;
        var onward = Kernel.RequestMovement(world, Request(world, "blue-1", "blue-1-worker", "onward")).Grant!;
        world.SimTime = onward.ArrivesAt;
        Assert.True(Kernel.ConfirmMovement(world, new(
            "confirm:onward",
            "blue-1",
            "blue-1-worker",
            world.ScenarioId,
            world.Generation,
            onward.Id)).Granted);
        Position(world, "blue-2", "Blue", "pinecrest", 0, 1).ReadyAt = world.SimTime;
        Register(world, "blue-2", "blue-2-worker");
        return (world, first.DepartsAt);
    }

    private static WorldState FutureReturnWorld()
    {
        var world = Kernel.Create();
        foreach (var other in world.Trains.Where(t => t.Id != "blue-1")) other.State = "withdrawn";
        world.BlockOccupancy.Clear();
        world.PlatformOccupancy.Clear();
        world.JunctionReservations.Clear();
        var train = Position(world, "blue-1", "Blue", "westhaven", 1, 1);
        train.Load = 10;
        train.ReadyAt = world.SimTime.AddMinutes(5);
        world.Cohorts.Add(new()
        {
            Id = "outbound",
            Origin = "pinecrest",
            Destination = "westhaven",
            StationId = "westhaven",
            Count = 10,
            State = "onboard",
            TrainId = train.Id,
            CreatedAt = world.SimTime,
            ReadyAt = world.SimTime,
            ReturnAt = world.SimTime.AddHours(2),
            Legs = [new() { Kind = "ride", Line = "Blue", From = "pinecrest", To = "westhaven" }]
        });
        world.Metrics.Generated = 10;
        Kernel.Tick(world, [], 1);
        return world;
    }

    private static void Register(WorldState world, string trainId, string worker) =>
        Assert.True(Kernel.Register(world, new(trainId, worker, world.ScenarioId, world.Generation)).Granted);

    private static MovementRequest Request(WorldState world, string trainId, string worker, string id)
    {
        var train = world.Trains.Single(t => t.Id == trainId);
        return new(id, trainId, worker, world.ScenarioId, world.Generation, train.Version);
    }

    private static CommandEnvelope Command(WorldState world, string kind, string? target = null, string? value = null, double? amount = null) =>
        new(Guid.NewGuid().ToString("N"), world.ScenarioId, world.Generation, world.Version, "test", "test", new(kind, target, value, amount));

    private static long Conserved(WorldState world) =>
        world.Cohorts.Where(c => c.State is not "completed" and not "abandoned").Sum(c => c.Count)
        + world.Metrics.Completed + world.Metrics.Abandoned;

    private static string Snapshot(WorldState world) =>
        string.Join("|", world.Trains.OrderBy(t => t.Id).Select(t => $"{t.Id}:{t.State}:{t.StationId}:{t.RouteIndex}:{t.Direction}:{t.ReadyAt:O}"))
        + "/" + string.Join("|", world.Cohorts.OrderBy(c => c.Id).Select(c => $"{c.Id}:{c.State}:{c.Count}:{c.TrainId}:{c.LegIndex}"))
        + "/" + string.Join("|", world.Incidents.OrderBy(i => i.Id).Select(i => $"{i.Kind}:{i.Target}:{i.StartedAt:O}:{i.EndsAt:O}"))
        + $"/{world.Metrics.Generated}:{world.Metrics.Completed}:{world.Metrics.Abandoned}:{world.History.Count}";
}
