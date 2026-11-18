using System.Text.Json;
using MetroOps.Contracts;
using MetroOps.ServiceDefaults;
using MetroOps.Simulation.Persistence;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace MetroOps.Scenario;

public sealed class OutboxPublisher(NpgsqlDataSource dataSource, IServiceProvider services, RuntimeHealth health, ILogger<OutboxPublisher> logger) : BackgroundService
{
    internal static async Task<bool> PublishPending(SnapshotOutbox outbox,
        Func<ProjectionEnvelope, CancellationToken, Task> publishConfirmed, CancellationToken ct)
    {
        if (await outbox.ReadPending(ct) is not { } pending) return false;
        // A newer checkpoint can replace the slot while this publication waits for confirmation.
        await publishConfirmed(pending, ct);
        await outbox.Confirm(pending.Sequence, ct);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!health.Ready)
            await Task.Delay(150, stoppingToken);

        var outbox = new SnapshotOutbox(dataSource);
        var failures = new FailureLog();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var broker = services.GetRequiredService<IConnection>();
                await using var channel = await broker.CreateChannelAsync(new CreateChannelOptions(true, true), stoppingToken);
                await channel.ExchangeDeclareAsync("metro.world", ExchangeType.Fanout, durable: true, cancellationToken: stoppingToken);
                while (!stoppingToken.IsCancellationRequested)
                {
                    var sent = await PublishPending(outbox, async (envelope, ct) =>
                    {
                        var properties = new BasicProperties { MessageId = envelope.EventId, ContentType = "application/json", Persistent = true };
                        await channel.BasicPublishAsync("metro.world", "", false, properties, JsonSerializer.SerializeToUtf8Bytes(envelope, Wire.Json), ct);
                    }, stoppingToken);
                    if (sent) failures.Recover(() => logger.LogInformation("Passenger snapshot publication recovered"));
                    await Task.Delay(150, stoppingToken);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                var reason = ex is OperationInterruptedException { ShutdownReason: { } shutdown }
                    ? System.Text.RegularExpressions.Regex.Replace(shutdown.ReplyText, @"\b\d+\b", "#")
                    : ex.GetBaseException().Message;
                failures.Report($"{ex.GetType()}:{reason}", () =>
                    logger.LogError(ex, "Passenger snapshot publication failed; latest update remains pending"));
                await Task.Delay(2000, stoppingToken);
            }
        }
    }
}
