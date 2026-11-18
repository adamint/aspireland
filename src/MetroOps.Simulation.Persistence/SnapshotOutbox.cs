using System.Text.Json;
using MetroOps.Contracts;
using Npgsql;

namespace MetroOps.Simulation.Persistence;

public sealed class SnapshotOutbox(NpgsqlDataSource dataSource)
{
    public async Task<ProjectionEnvelope?> ReadPending(CancellationToken ct)
    {
        await using var query = dataSource.CreateCommand("SELECT sequence,event_id,body::text FROM metro_outbox WHERE slot=1");
        await using var reader = await query.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var world = JsonSerializer.Deserialize<RiderWorld>(reader.GetString(2), Wire.Json)
            ?? throw new JsonException("Invalid pending rider snapshot.");
        return new(reader.GetInt64(0), reader.GetString(1), world.ScenarioId, world.Generation, world.Version, world.SimTime, world);
    }

    public async Task Confirm(long sequence, CancellationToken ct)
    {
        await using var delete = dataSource.CreateCommand("DELETE FROM metro_outbox WHERE slot=1 AND sequence=@sequence");
        delete.Parameters.AddWithValue("sequence", sequence);
        await delete.ExecuteNonQueryAsync(ct);
    }
}
