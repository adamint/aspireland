using MetroOps.Contracts;
using MetroOps.Domain;
using MetroOps.ServiceDefaults;
using MetroOps.Simulation.Persistence;

namespace MetroOps.Scenario;

public static class ScenarioRoutes
{
    private static readonly SemaphoreSlim comparisonGate = new(1, 1);
    public static void Map(WebApplication app)
    {
        app.MapGet("/state", (WorldStore store, CancellationToken ct) => store.Read(ct));
        app.MapGet("/rider-state", async (WorldStore store, CancellationToken ct) => Kernel.ProjectRiders(await store.Read(ct)));
        app.MapGet("/analytics-state", async (WorldStore store, CancellationToken ct) => AnalyticsState(await store.Read(ct)));
        app.MapGet("/context", async (WorldStore store, CancellationToken ct) =>
        {
            var world = await store.Read(ct);
            return new ScenarioContext(world.ScenarioId, world.Generation, world.Version, world.SimTime, world.UpdatedAt, world.AuthorityMode);
        });
        app.MapGet("/status", async (WorldStore store, CancellationToken ct) =>
        {
            var world = await store.Read(ct);
            var incidents = world.Incidents.Where(i => i.State == "active").ToArray();
            var flights = world.Flights.Where(f => f.ScheduledAt >= world.SimTime.AddMinutes(-10)
                && f.ScheduledAt <= world.SimTime.AddHours(2)).OrderBy(f => f.ScheduledAt).ToArray();
            return new
            {
                world.ScenarioId, world.Generation, world.Version, world.SimTime, world.UpdatedAt,
                world.AuthorityMode, world.Paused, world.Speed, world.Preset,
                trains = world.Trains,
                activeIncidentCount = incidents.Length,
                incidents = incidents.Take(64),
                incidentsTruncated = incidents.Length > 64,
                world.Metrics, world.Headways, world.BlockOccupancy, world.PlatformOccupancy, world.JunctionReservations,
                stations = SummarizeQueues(world),
                upcomingFlightCount = flights.Length,
                upcomingFlights = flights.Take(32),
                flightsTruncated = flights.Length > 32
            };
        });
        app.MapGet("/events", (long? after, WorldStore store, CancellationToken ct) => store.Events(after ?? 0, ct));
        app.MapPost("/clock", async (ClockRequest request, HttpContext context, WorldStore store, IConfiguration config, CancellationToken ct) =>
        {
            if (!ServiceKey.Matches(context, config, "SERVICE_KEY")) return Results.Unauthorized();
            if (request.Speed is { } speed && (!double.IsFinite(speed) || speed < 1 || speed > 120))
                return Results.BadRequest(new { error = "Clock speed must be between 1 and 120." });
            return Results.Ok(await store.Mutate(world =>
            {
                if (request.Paused is { } paused) world.Paused = paused;
                if (request.Speed is { } value) world.Speed = value;
                Record(world, "ClockChanged", $"Clock {(world.Paused ? "paused" : "running")} at {world.Speed}x.");
                return new { world.Paused, world.Speed };
            }, ct));
        });
        app.MapPost("/authority", async (AuthorityRequest request, HttpContext context, WorldStore store, IConfiguration config, CancellationToken ct) =>
        {
            if (!ServiceKey.Matches(context, config, "SERVICE_KEY")) return Results.Unauthorized();
            if (request.Mode is not ("advise" or "approval" or "bounded")) return Results.BadRequest(new { error = "Unknown authority mode." });
            return Results.Ok(await store.Mutate(world =>
            {
                world.AuthorityMode = request.Mode;
                Record(world, "AuthorityChanged", $"Dispatcher authority changed to {request.Mode} by an operator.");
                return new { mode = world.AuthorityMode };
            }, ct));
        });
        app.MapPost("/scenarios", async (ScenarioRequest request, HttpContext context, WorldStore store, IConfiguration config, CancellationToken ct) =>
        {
            if (!ServiceKey.Matches(context, config, "SERVICE_KEY")) return Results.Unauthorized();
            var seed = request.Seed ?? System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, int.MaxValue);
            return Results.Ok(await store.Mutate(world =>
            {
                var fresh = Kernel.Create(seed, request.Preset, request.StartTime);
                var nextGeneration = world.Generation + 1;
                var version = world.Version;
                Reset(world, fresh);
                world.Generation = nextGeneration;
                world.Version = version;
                Record(world, "ScenarioReset", $"New {request.Preset} scenario with seed {seed}.");
                return new { world.ScenarioId, world.Generation, world.Seed, world.SimTime };
            }, ct));
        });
        app.MapPost("/compare", async (CompareRequest request, WorldStore store, ServiceClients clients, CancellationToken ct) =>
        {
            if (!await comparisonGate.WaitAsync(0, ct))
                return Results.Json(new { error = "A comparison is already running. Try again shortly." }, statusCode: 429);
            try
            {
                var checkpoint = await store.Read(ct);
                if (request.ScenarioId != checkpoint.ScenarioId || request.Generation != checkpoint.Generation)
                    return Results.Conflict(new { error = "The scenario changed. Refresh the comparison." });
                var futures = await Task.WhenAll(Enumerable.Range(0, 8).Select(async sample =>
                {
                    var seedBytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                        $"metroops-forecast-v1:{checkpoint.Seed}:{checkpoint.Version}:{sample}"));
                    var seed = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(seedBytes) & long.MaxValue;
                    var future = await clients.Post<DemandResponse>("demand", "/demand",
                        new DemandRequest(seed, checkpoint.StreamVersion, checkpoint.Preset, checkpoint.SimTime, checkpoint.SimTime.AddMinutes(30)), ct);
                    return new ForecastSample(seed, future.Cohorts, future.Flights);
                }));
                return Results.Ok(await Task.Run(() => Kernel.Compare(checkpoint, request.Action, futures), ct));
            }
            finally { comparisonGate.Release(); }
        });
    }

    public static object AnalyticsState(WorldState world) => new
    {
        world.ScenarioId, world.Generation, world.Version, world.SimTime, world.UpdatedAt,
        world.Metrics, world.Trains, world.History, world.Incidents,
        cohorts = world.Cohorts.Where(c => c.State is not ("completed" or "abandoned"))
            .GroupBy(c => new { c.State, c.StationId, c.Accessible, Future = c.ReadyAt > world.SimTime })
            .Select(g => new
            {
                g.Key.State, g.Key.StationId, g.Key.Accessible,
                Count = g.Sum(c => c.Count), ReadyAt = g.Min(c => c.ReadyAt)
            }).ToArray()
    };

    public static IReadOnlyList<StationQueueSummary> SummarizeQueues(WorldState world)
    {
        var queues = world.Cohorts.Where(c => Kernel.IsWaitingAt(c, world.SimTime)).ToLookup(c => c.StationId);
        return Network.Definition.Stations.Select(station =>
        {
            var cohorts = queues[station.Id].ToArray();
            return new StationQueueSummary(station.Id, station.Name,
                cohorts.Sum(c => c.Count),
                cohorts.Where(c => c.State == "platform").Sum(c => c.Count),
                cohorts.Where(c => c.State == "outside").Sum(c => c.Count),
                cohorts.Where(c => c.Accessible).Sum(c => c.Count),
                cohorts.GroupBy(c => c.State).ToDictionary(g => g.Key, g => g.Sum(c => c.Count)),
                cohorts.Length == 0 ? 0 : Math.Max(0, (world.SimTime - cohorts.Min(c => c.ReadyAt)).TotalSeconds));
        }).ToArray();
    }

    public static void Record(WorldState world, string kind, string description) => world.History.Add(new HistoryEvent
    {
        ScenarioId = world.ScenarioId, Generation = world.Generation, Version = world.Version,
        SimTime = world.SimTime, Kind = kind, EntityId = world.ScenarioId, Source = "manual",
        Description = description, CorrelationId = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? ""
    });

    private static void Reset(WorldState target, WorldState fresh)
    {
        target.ScenarioId = fresh.ScenarioId; target.Seed = fresh.Seed; target.StreamVersion = fresh.StreamVersion;
        target.SimTime = fresh.SimTime; target.Paused = fresh.Paused; target.Speed = fresh.Speed; target.Preset = fresh.Preset;
        target.AuthorityMode = "advise"; target.Trains = fresh.Trains; target.Cohorts = fresh.Cohorts;
        target.Incidents = fresh.Incidents; target.History = fresh.History; target.BlockOccupancy = fresh.BlockOccupancy;
        target.PlatformOccupancy = fresh.PlatformOccupancy; target.JunctionReservations = fresh.JunctionReservations;
        target.Grants = fresh.Grants; target.Commands = fresh.Commands; target.Headways = fresh.Headways;
        target.Metrics = fresh.Metrics; target.Flights = fresh.Flights; target.DemandCursor = fresh.DemandCursor;
        target.LastAgentActionAt = null;
    }
}
