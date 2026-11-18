using System.Text.Json;
using MetroOps.Contracts;
using Npgsql;
using RabbitMQ.Client;

namespace MetroOps.Scenario;

public sealed class OutboxPublisher(NpgsqlDataSource dataSource, IServiceProvider services, ILogger<OutboxPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var broker = services.GetRequiredService<IConnection>();
                await using var channel = await broker.CreateChannelAsync(new CreateChannelOptions(true, true), stoppingToken);
                await channel.ExchangeDeclareAsync("metro.world", ExchangeType.Fanout, durable: true, cancellationToken: stoppingToken);
                while (!stoppingToken.IsCancellationRequested)
                {
                    await using var connection = await dataSource.OpenConnectionAsync(stoppingToken);
                    await using var transaction = await connection.BeginTransactionAsync(stoppingToken);
                    await using var query = new NpgsqlCommand("SELECT sequence,event_id,body::text FROM metro_outbox WHERE NOT published ORDER BY sequence DESC LIMIT 1 FOR UPDATE SKIP LOCKED", connection, transaction);
                    var pending = new List<(long Sequence, string Id, string State)>();
                    await using (var reader = await query.ExecuteReaderAsync(stoppingToken))
                        while (await reader.ReadAsync(stoppingToken))
                            pending.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
                    foreach (var row in pending)
                    {
                        var world = JsonSerializer.Deserialize<WorldState>(row.State, Wire.Json)!;
                        var body = new ProjectionEnvelope(row.Sequence, row.Id, world.ScenarioId, world.Generation, world.Version, world.SimTime, world);
                        var properties = new BasicProperties { MessageId = row.Id, ContentType = "application/json", Persistent = true };
                        await channel.BasicPublishAsync("metro.world", "", false, properties, JsonSerializer.SerializeToUtf8Bytes(body, Wire.Json), stoppingToken);
                        await using var update = new NpgsqlCommand("DELETE FROM metro_outbox WHERE sequence<=@id", connection, transaction);
                        update.Parameters.AddWithValue("id", row.Sequence);
                        await update.ExecuteNonQueryAsync(stoppingToken);
                    }
                    await transaction.CommitAsync(stoppingToken);
                    await Task.Delay(150, stoppingToken);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Domain event publisher disconnected; durable outbox retained");
                await Task.Delay(2000, stoppingToken);
            }
        }
    }
}
