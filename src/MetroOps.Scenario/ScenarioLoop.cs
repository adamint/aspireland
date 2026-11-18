using System.Diagnostics;
using MetroOps.Contracts;
using MetroOps.Domain;
using MetroOps.ServiceDefaults;
using MetroOps.Simulation.Persistence;
using Npgsql;

namespace MetroOps.Scenario;

public sealed class ScenarioLoop(WorldStore store, ServiceClients clients, RuntimeHealth health, ILogger<ScenarioLoop> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await store.Initialize(stoppingToken);
                break;
            }
            catch (NpgsqlException ex) when (!stoppingToken.IsCancellationRequested)
            {
                health.Error = "The simulation is waiting for its authoritative store to initialize.";
                logger.LogError(ex, "Authoritative store initialization unavailable; readiness remains unhealthy");
                await Task.Delay(2000, stoppingToken);
            }
        }
        stoppingToken.ThrowIfCancellationRequested();
        health.Error = null;
        health.Ready = true;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        var clock = Stopwatch.StartNew();
        double budget = 0;
        var previous = clock.Elapsed.TotalSeconds;
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var elapsed = clock.Elapsed.TotalSeconds;
                var wallStep = elapsed - previous;
                previous = elapsed;
                var snapshot = await store.Read(stoppingToken);
                health.Error = null;
                if (snapshot.Paused) { budget = 0; continue; }
                budget = Math.Min(120, budget + wallStep * snapshot.Speed);
                var seconds = Math.Min(20, (int)budget);
                if (seconds < 1) continue;
                var demand = await clients.Post<DemandResponse>("demand", "/demand",
                    new DemandRequest(snapshot.Seed, snapshot.StreamVersion, snapshot.Preset, snapshot.SimTime, snapshot.SimTime.AddSeconds(seconds)), stoppingToken);
                var advanced = await store.MutateActive(world =>
                {
                    if (world.Paused || world.ScenarioId != snapshot.ScenarioId || world.SimTime != snapshot.SimTime) return false;
                    Kernel.Tick(world, demand.Cohorts, seconds);
                    world.Flights = demand.Flights;
                    return true;
                }, stoppingToken);
                if (advanced) budget -= seconds;
            }
            catch (Exception ex) when (ex is HttpRequestException or UpstreamException or Npgsql.NpgsqlException or InvalidOperationException)
            {
                health.Error = "The simulation clock is waiting for its durable store or demand service.";
                budget = 0;
                logger.LogError(ex, "Simulation transition unavailable; no time or passenger transitions were skipped");
                await Task.Delay(1000, stoppingToken);
                previous = clock.Elapsed.TotalSeconds;
            }
        }
    }
}
