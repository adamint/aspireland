using System.Text.Json;
using MetroOps.Contracts;
using MetroOps.Domain;
using Npgsql;

namespace MetroOps.Simulation.Persistence;

public sealed class WorldStore(NpgsqlDataSource dataSource)
{
    private const int CheckpointHistoryLimit = 250;
    private const int EventHistoryLimit = 10_000;

    public async Task Initialize(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS metro_checkpoint (id integer PRIMARY KEY CHECK(id=1), state jsonb NOT NULL);
            CREATE TABLE IF NOT EXISTS metro_events (
                sequence bigserial PRIMARY KEY, event_id text NOT NULL UNIQUE,
                scenario_id text NOT NULL, generation integer NOT NULL,
                version bigint NOT NULL, sim_time timestamptz NOT NULL, body jsonb NOT NULL);
            CREATE TABLE IF NOT EXISTS metro_outbox (
                sequence bigserial PRIMARY KEY, event_id text NOT NULL UNIQUE,
                body jsonb NOT NULL, published boolean NOT NULL DEFAULT false);
            ALTER TABLE metro_checkpoint ALTER COLUMN state SET COMPRESSION lz4;
            ALTER TABLE metro_outbox ALTER COLUMN body SET COMPRESSION lz4;
            CREATE INDEX IF NOT EXISTS metro_outbox_pending ON metro_outbox(sequence) WHERE NOT published;
            CREATE TABLE IF NOT EXISTS metro_commands (
                command_id text PRIMARY KEY, scenario_id text NOT NULL, generation integer NOT NULL,
                body jsonb NOT NULL, result jsonb NOT NULL, recorded_at timestamptz NOT NULL DEFAULT now());
            INSERT INTO metro_checkpoint(id,state) VALUES(1,@state::jsonb) ON CONFLICT DO NOTHING;
            SELECT NOT EXISTS (
                SELECT FROM pg_attribute
                WHERE attrelid='metro_outbox'::regclass AND attname='slot' AND NOT attisdropped);
            """, connection, transaction);
        command.Parameters.AddWithValue("state", JsonSerializer.Serialize(Kernel.Create(), Wire.Json));
        if ((bool)(await command.ExecuteScalarAsync(ct))!)
        {
            // Legacy rows are snapshots, not events. Rebuild the slot from the checkpoint, not the queue.
            await using var migrate = new NpgsqlCommand("""
                ALTER TABLE metro_outbox ADD COLUMN slot integer CHECK (slot=1);
                DELETE FROM metro_outbox;
                ALTER TABLE metro_outbox ALTER COLUMN slot SET NOT NULL;
                ALTER TABLE metro_outbox ADD UNIQUE (slot);
                """, connection, transaction);
            await migrate.ExecuteNonQueryAsync(ct);
            var world = await ReadForUpdate(connection, transaction, ct);
            await TrimEventHistory(connection, transaction, ct);
            await Save(world, connection, transaction, ct);
        }
        await transaction.CommitAsync(ct);
    }

    public async Task<WorldState> Read(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("SELECT state::text FROM metro_checkpoint WHERE id=1", connection);
        var state = await command.ExecuteScalarAsync(ct) as string
            ?? throw new InvalidOperationException("The scenario checkpoint is not initialized.");
        return JsonSerializer.Deserialize<WorldState>(state, Wire.Json)!;
    }

    public async Task<TrainControlView?> ReadTrain(string id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            WITH snapshot AS MATERIALIZED (
                SELECT view.* FROM metro_checkpoint
                CROSS JOIN LATERAL jsonb_to_record(state) AS view(
                    "scenarioId" text,generation integer,"simTime" text,paused boolean,trains jsonb,grants jsonb)
                WHERE id=1
            )
            SELECT CASE WHEN train.value IS NULL THEN NULL ELSE jsonb_build_object(
                'scenarioId',snapshot."scenarioId",'generation',snapshot.generation,
                'simTime',snapshot."simTime",'paused',snapshot.paused,
                'train',train.value,'grant',movement.value)::text END
            FROM snapshot
            LEFT JOIN LATERAL (
                SELECT value FROM jsonb_array_elements(snapshot.trains) WHERE value->>'id'=@id
            ) train ON true
            LEFT JOIN LATERAL (
                SELECT value FROM jsonb_each(snapshot.grants)
                WHERE value->>'id'=train.value->>'activeGrantId'
            ) movement ON true
            """, connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("The scenario checkpoint is not initialized.");
        return reader.IsDBNull(0) ? null : JsonSerializer.Deserialize<TrainControlView>(reader.GetString(0), Wire.Json)!;
    }

    public async Task<T> Mutate<T>(Func<WorldState, T> transition, CancellationToken ct, CommandEnvelope? envelope = null)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var world = await ReadForUpdate(connection, transaction, ct);
        DropTerminalCohorts(world);
        if (envelope is not null)
        {
            await using var existing = new NpgsqlCommand("SELECT result::text,body::text FROM metro_commands WHERE command_id=@id", connection, transaction);
            existing.Parameters.AddWithValue("id", envelope.CommandId);
            await using var reader = await existing.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var previous = JsonSerializer.Deserialize<CommandEnvelope>(reader.GetString(1), Wire.Json)!;
                if (previous != envelope)
                    throw new InvalidOperationException("This idempotency key already belongs to a different command.");
                return JsonSerializer.Deserialize<T>(reader.GetString(0), Wire.Json)!;
            }
        }

        var before = envelope is null ? JsonSerializer.Serialize(world, Wire.Json) : null;
        var historyIds = world.History.Select(e => e.Id).ToHashSet();
        var result = transition(world);
        if (before is not null && before == JsonSerializer.Serialize(world, Wire.Json))
            return result;
        world.Version++;
        world.UpdatedAt = DateTimeOffset.UtcNow;
        if (result is CommandResult outcome)
            result = (T)(object)(outcome with { Version = world.Version });
        var events = world.History.Where(e => !historyIds.Contains(e.Id)).ToList();
        foreach (var ev in events)
        {
            ev.Version = world.Version;
            ev.ScenarioId = world.ScenarioId;
            ev.Generation = world.Generation;
            await using var record = new NpgsqlCommand("""
                INSERT INTO metro_events(event_id,scenario_id,generation,version,sim_time,body)
                VALUES(@id,@scenario,@generation,@version,@time,@body::jsonb) ON CONFLICT DO NOTHING
                """, connection, transaction);
            record.Parameters.AddWithValue("id", ev.Id);
            record.Parameters.AddWithValue("scenario", ev.ScenarioId);
            record.Parameters.AddWithValue("generation", ev.Generation);
            record.Parameters.AddWithValue("version", ev.Version);
            record.Parameters.AddWithValue("time", ev.SimTime);
            record.Parameters.AddWithValue("body", JsonSerializer.Serialize(ev, Wire.Json));
            await record.ExecuteNonQueryAsync(ct);
        }
        if (events.Count > 0) await TrimEventHistory(connection, transaction, ct);
        await Save(world, connection, transaction, ct);
        if (envelope is not null)
        {
            await using var record = new NpgsqlCommand("""
                INSERT INTO metro_commands(command_id,scenario_id,generation,body,result)
                VALUES(@id,@scenario,@generation,@body::jsonb,@result::jsonb)
                """, connection, transaction);
            record.Parameters.AddWithValue("id", envelope.CommandId);
            record.Parameters.AddWithValue("scenario", envelope.ScenarioId);
            record.Parameters.AddWithValue("generation", envelope.Generation);
            record.Parameters.AddWithValue("body", JsonSerializer.Serialize(envelope, Wire.Json));
            record.Parameters.AddWithValue("result", JsonSerializer.Serialize(result, Wire.Json));
            await record.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return result;
    }

    private static async Task<WorldState> ReadForUpdate(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using var query = new NpgsqlCommand("SELECT state::text FROM metro_checkpoint WHERE id=1 FOR UPDATE", connection, transaction);
        var state = await query.ExecuteScalarAsync(ct) as string
            ?? throw new InvalidOperationException("No authoritative checkpoint is available.");
        return JsonSerializer.Deserialize<WorldState>(state, Wire.Json)!;
    }

    // Completion and abandonment already update the cumulative counters; completed round trips schedule returns immediately.
    private static void DropTerminalCohorts(WorldState world) =>
        world.Cohorts.RemoveAll(c => c.State is "completed" or "abandoned");

    private static async Task Save(WorldState world, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        DropTerminalCohorts(world);
        if (world.History.Count > CheckpointHistoryLimit)
            world.History.RemoveRange(0, world.History.Count - CheckpointHistoryLimit);
        await using var save = new NpgsqlCommand("""
            UPDATE metro_checkpoint SET state=@state::jsonb WHERE id=1;
            INSERT INTO metro_outbox(slot,event_id,body) VALUES(1,@event,@rider::jsonb)
            ON CONFLICT(slot) DO UPDATE SET sequence=EXCLUDED.sequence,event_id=EXCLUDED.event_id,body=EXCLUDED.body;
            """, connection, transaction);
        save.Parameters.AddWithValue("state", JsonSerializer.Serialize(world, Wire.Json));
        save.Parameters.AddWithValue("rider", JsonSerializer.Serialize(Kernel.ProjectRiders(world), Wire.Json));
        save.Parameters.AddWithValue("event", Guid.NewGuid().ToString("N"));
        await save.ExecuteNonQueryAsync(ct);
    }

    private static async Task TrimEventHistory(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using var trim = new NpgsqlCommand("""
            DELETE FROM metro_events WHERE sequence < (
                SELECT sequence FROM metro_events ORDER BY sequence DESC OFFSET @offset LIMIT 1);
            """, connection, transaction);
        trim.Parameters.AddWithValue("offset", EventHistoryLimit - 1);
        await trim.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<HistoryEvent>> Events(long after, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("SELECT body::text FROM metro_events WHERE sequence>@after ORDER BY sequence LIMIT 300", connection);
        command.Parameters.AddWithValue("after", after);
        var events = new List<HistoryEvent>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            events.Add(JsonSerializer.Deserialize<HistoryEvent>(reader.GetString(0), Wire.Json)!);
        return events;
    }
}
