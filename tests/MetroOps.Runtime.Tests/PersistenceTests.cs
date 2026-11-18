using MetroOps.Contracts;
using MetroOps.Domain;
using MetroOps.Simulation.Persistence;
using Npgsql;
using System.Text.Json;

namespace MetroOps.Runtime.Tests;

public sealed class PersistenceTests
{
    [PersistenceFact]
    public async Task Unchanged_transition_does_not_rewrite_or_publish_the_checkpoint()
    {
        await using var dataSource = TestDataSource();
        var store = new WorldStore(dataSource);
        await store.Initialize(default);
        var before = await store.Read(default);
        await using var count = dataSource.CreateCommand("SELECT count(*) FROM metro_outbox");
        var publications = (long)(await count.ExecuteScalarAsync())!;

        Assert.False(await store.Mutate(_ => false, default));

        var after = await store.Read(default);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(publications, (long)(await count.ExecuteScalarAsync())!);
    }

    [PersistenceFact]
    public async Task Active_inventory_transition_preserves_retired_records_and_atomic_publication()
    {
        await using var dataSource = TestDataSource();
        var store = new WorldStore(dataSource);
        await store.Initialize(default);
        await store.Mutate(world =>
        {
            world.Cohorts =
            [
                new() { Id = "completed", State = "completed", Count = 5, Legs = [new() { From = new string('x', 5000) }] },
                new() { Id = "abandoned", State = "abandoned", Count = 3 },
                new() { Id = "future", State = "outside", Count = 7, ReadyAt = world.SimTime.AddHours(1) },
                new() { Id = "unknown", State = "unknown", Count = 2 },
                new() { Id = "expires", State = "outside", Count = 1, ReadyAt = world.SimTime,
                    CreatedAt = world.SimTime.AddMinutes(-2), PatienceSeconds = 60 }
            ];
            world.Metrics.Generated = 18;
            world.Metrics.Completed = 5;
            world.Metrics.Abandoned = 3;
            return true;
        }, default);
        var before = await store.Read(default);

        await store.MutateActive(world =>
        {
            Assert.DoesNotContain(world.Cohorts, c => c.State is "completed" or "abandoned");
            Assert.Contains(world.Cohorts, c => c.Id == "unknown");
            world.Cohorts.Add(new() { Id = "new", Count = 4, ReadyAt = world.SimTime.AddHours(1) });
            world.Metrics.Generated += 4;
            Kernel.Tick(world, [], 1);
            return true;
        }, default);
        await store.MutateActive(world =>
        {
            Assert.DoesNotContain(world.Cohorts, c => c.Id == "expires");
            Kernel.Tick(world, [], 1);
            return true;
        }, default);

        var after = await store.Read(default);
        Assert.Equal(22, after.Cohorts.Sum(c => c.Count));
        Assert.Equal(after.Metrics.Generated, after.Cohorts.Sum(c => c.Count));
        Assert.Equal(before.SimTime.AddSeconds(2), after.SimTime);
        Assert.Equal("abandoned", Assert.Single(after.Cohorts, c => c.Id == "expires").State);
        foreach (var id in new[] { "completed", "abandoned" })
            Assert.Equal(JsonSerializer.Serialize(before.Cohorts.Single(c => c.Id == id), Wire.Json),
                JsonSerializer.Serialize(after.Cohorts.Single(c => c.Id == id), Wire.Json));
        await using var publication = dataSource.CreateCommand("""
            SELECT state=(SELECT body FROM metro_outbox ORDER BY sequence DESC LIMIT 1)
            FROM metro_checkpoint WHERE id=1
            """);
        Assert.True((bool)(await publication.ExecuteScalarAsync())!);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.MutateActive<bool>(world =>
        {
            world.Cohorts.Clear();
            throw new InvalidOperationException("Abort this transition.");
        }, default));
        Assert.Equal(JsonSerializer.Serialize(after, Wire.Json), JsonSerializer.Serialize(await store.Read(default), Wire.Json));
        Assert.True((bool)(await publication.ExecuteScalarAsync())!);
    }

    [PersistenceFact]
    public async Task Active_inventory_matches_full_kernel_transitions()
    {
        await using var dataSource = TestDataSource();
        var store = new WorldStore(dataSource);
        await store.Initialize(default);
        var checkpoint = Environment.GetEnvironmentVariable("METROOPS_TEST_CHECKPOINT");
        var original = checkpoint is null ? Kernel.Create() :
            JsonSerializer.Deserialize<WorldState>(await File.ReadAllTextAsync(checkpoint), Wire.Json)!;
        foreach (var train in original.Trains)
        {
            train.WorkerInstanceId ??= $"equivalence:{train.Id}";
            train.LastHeartbeatAt = DateTimeOffset.UtcNow;
        }
        original.Cohorts.Add(new()
        {
            Id = "equivalence-retired", State = "completed", Count = 5, ReturnScheduled = true
        });
        original.Cohorts.Add(new()
        {
            Id = "equivalence-future", Origin = "pinecrest", Destination = "eastgate",
            StationId = "pinecrest", Count = 3, CreatedAt = original.SimTime,
            ReadyAt = original.SimTime.AddSeconds(100), ReturnAt = original.SimTime.AddHours(1)
        });
        original.Metrics.Generated += 8;
        original.Metrics.Completed += 5;
        await using (var seed = dataSource.CreateCommand("UPDATE metro_checkpoint SET state=@state::jsonb WHERE id=1"))
        {
            seed.Parameters.AddWithValue("state", JsonSerializer.Serialize(original, Wire.Json));
            await seed.ExecuteNonQueryAsync();
        }
        var expected = await store.Read(default);

        for (var step = 0; step < 8; step++)
        {
            DemandBatch[] demand =
            [
                new($"equivalence:{step}", "pinecrest", "eastgate", 30, false,
                    expected.SimTime, 1800, expected.SimTime.AddHours(1))
            ];
            Kernel.Tick(expected, demand, 20, driveTrains: true);
            await store.MutateActive(world =>
            {
                Kernel.Tick(world, demand, 20, driveTrains: true);
                return true;
            }, default);
            var actual = await store.Read(default);
            expected.Version++;
            expected.UpdatedAt = actual.UpdatedAt;
            var expectedView = Wire.Clone(expected);
            var actualView = Wire.Clone(actual);
            expectedView.Cohorts.Sort((a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));
            actualView.Cohorts.Sort((a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));
            Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(expectedView, Wire.Json),
                JsonSerializer.SerializeToElement(actualView, Wire.Json)), $"Inventory differed after step {step}.");
            Assert.Equal(actual.Metrics.Generated, actual.Cohorts.Sum(c => c.Count));
            Assert.Equal(original.ScenarioId, actual.ScenarioId);
            Assert.Equal(original.Generation, actual.Generation);
            Assert.Equal(Fleet.Trains.Count, actual.Trains.Count);
            await using var publication = dataSource.CreateCommand("""
                SELECT state=(SELECT body FROM metro_outbox ORDER BY sequence DESC LIMIT 1)
                FROM metro_checkpoint WHERE id=1
                """);
            Assert.True((bool)(await publication.ExecuteScalarAsync())!);
        }
    }

    [PersistenceFact]
    public async Task Compact_train_read_preserves_active_grant_and_missing_train_semantics()
    {
        await using var dataSource = TestDataSource();
        var store = new WorldStore(dataSource);
        await store.Initialize(default);
        await store.Mutate(world =>
        {
            var train = world.Trains.Single(t => t.Id == "blue-1");
            train.ActiveGrantId = "grant";
            world.Grants["request"] = new MovementGrant { Id = "grant", TrainId = train.Id };
            return true;
        }, default);
        var world = await store.Read(default);
        var view = await store.ReadTrain("blue-1", default);

        Assert.NotNull(view);
        Assert.Equal(world.ScenarioId, view.ScenarioId);
        Assert.Equal(world.Generation, view.Generation);
        Assert.Equal(world.SimTime, view.SimTime);
        Assert.Equal("grant", view.Grant?.Id);
        Assert.Equal(view.Train.ActiveGrantId, view.Grant?.Id);
        Assert.Null(await store.ReadTrain("not-a-train", default));
    }

    private static NpgsqlDataSource TestDataSource()
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("METROOPS_TEST_POSTGRES"));
        if (connection.Database?.StartsWith("metroops_test_", StringComparison.Ordinal) != true)
            throw new InvalidOperationException("Persistence checks require a separate metroops_test_* database.");
        return NpgsqlDataSource.Create(connection.ConnectionString);
    }
}

public sealed class PersistenceFactAttribute : FactAttribute
{
    public PersistenceFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("METROOPS_TEST_POSTGRES")))
            Skip = "Set METROOPS_TEST_POSTGRES to a disposable metroops_test_* PostgreSQL database.";
    }
}
