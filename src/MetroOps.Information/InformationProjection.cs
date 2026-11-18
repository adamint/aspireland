using System.Text.Json;
using MetroOps.Contracts;
using MetroOps.Domain;
using MetroOps.ServiceDefaults;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using StackExchange.Redis;

namespace MetroOps.Information;

public sealed class InformationProjection(IServiceProvider services, ServiceClients clients, ILogger<InformationProjection> logger) : BackgroundService, IHealthCheck
{
    private WorldState? latest;
    private readonly SemaphoreSlim acceptGate = new(1, 1);
    private const string QueueName = "metro.information.snapshots-v2";
    public WorldState? Latest => Volatile.Read(ref latest);

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(Latest is { } world && DateTimeOffset.UtcNow - world.UpdatedAt <= TimeSpan.FromSeconds(10)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Passenger information is awaiting a fresh projection."));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = new FailureLog();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var redis = services.GetRequiredService<IConnectionMultiplexer>();
                var broker = services.GetRequiredService<IConnection>();
                await using var channel = await broker.CreateChannelAsync(cancellationToken: stoppingToken);
                await channel.ExchangeDeclareAsync("metro.world", ExchangeType.Fanout, durable: true, cancellationToken: stoppingToken);
                // Retire the unbounded v1 cache queue; authoritative event history remains in PostgreSQL.
                await channel.QueueDeclareAsync("metro.information", durable: true, exclusive: false, autoDelete: false, cancellationToken: stoppingToken);
                await channel.QueueDeleteAsync("metro.information", ifUnused: true, ifEmpty: false, cancellationToken: stoppingToken);
                await channel.QueueDeclareAsync(QueueName, durable: true, exclusive: false, autoDelete: false,
                    arguments: new Dictionary<string, object?> { ["x-max-length"] = 1, ["x-overflow"] = "drop-head" }, cancellationToken: stoppingToken);
                await channel.QueueBindAsync(QueueName, "metro.world", "", cancellationToken: stoppingToken);
                await channel.BasicQosAsync(0, 1, false, stoppingToken);
                var consumerFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += async (_, delivery) =>
                {
                    try
                    {
                        var envelope = JsonSerializer.Deserialize<ProjectionEnvelope>(delivery.Body.Span, Wire.Json)
                            ?? throw new JsonException("Invalid world event.");
                        // The same envelope can still carry an old full-world delivery after a restart.
                        await Accept(envelope.World, redis, stoppingToken, normalize: true);
                        await channel.BasicAckAsync(delivery.DeliveryTag, false, stoppingToken);
                    }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                    {
                        consumerFailure.TrySetException(ex);
                    }
                };
                await channel.BasicConsumeAsync(QueueName, false, consumer, stoppingToken);
                // Subscribe before reading the authoritative checkpoint so events cannot be lost in the handoff.
                await Accept(await clients.Get<RiderWorld>("scenario", "/rider-state", stoppingToken), redis, stoppingToken);
                failures.Recover(() => logger.LogInformation("Passenger-information subscription recovered"));
                while (channel.IsOpen && broker.IsOpen && !stoppingToken.IsCancellationRequested)
                {
                    await Task.WhenAny(Task.Delay(1000, stoppingToken), consumerFailure.Task);
                    if (consumerFailure.Task.IsCompleted) await consumerFailure.Task;
                }
                if (!stoppingToken.IsCancellationRequested)
                    throw new InvalidOperationException("The passenger snapshot channel or connection closed.");
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                var reason = ex.GetBaseException();
                failures.Report($"{reason.GetType()}:{reason.Message}", () =>
                    logger.LogError(ex, "Passenger-information projection disconnected; existing boards will become visibly stale"));
                await Task.Delay(2000, stoppingToken);
            }
        }
    }

    private async Task Accept(RiderWorld world, IConnectionMultiplexer redis, CancellationToken ct, bool normalize = false)
    {
        await acceptGate.WaitAsync(ct);
        try
        {
            if (Latest is { } current && current.Version >= world.Version) return;
            if (normalize) world = Kernel.ProjectRiders(world.ToWorldState());
            await redis.GetDatabase().StringSetAsync("metro:public-world", JsonSerializer.Serialize(world, Wire.Json), TimeSpan.FromMinutes(10));
            Volatile.Write(ref latest, world.ToWorldState());
        }
        finally { acceptGate.Release(); }
    }

    public override void Dispose()
    {
        acceptGate.Dispose();
        base.Dispose();
    }
}
