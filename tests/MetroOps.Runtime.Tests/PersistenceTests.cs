using MetroOps.Contracts;
using MetroOps.Domain;
using MetroOps.Information;
using MetroOps.Scenario;
using MetroOps.ServiceDefaults;
using MetroOps.Simulation.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Trace;
using RabbitMQ.Client;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit.Abstractions;

namespace MetroOps.Runtime.Tests;

public sealed class PersistenceTests(ITestOutputHelper output)
{
    [PersistenceFact]
    public async Task Scenario_exports_database_errors_and_request_children_but_not_successful_database_roots()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["ConnectionStrings:metro"] = TestConnectionString();
        builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] = null;
        builder.AddScenarioDefaults();
        builder.AddSimulationPersistence();
        var exporter = new TraceCapture();
        builder.Services.AddOpenTelemetry().WithTracing(t => t.AddProcessor(new BatchActivityExportProcessor(exporter)));
        await using var app = builder.Build();
        ScenarioRoutes.Map(app);
        await app.StartAsync();
        var traces = app.Services.GetRequiredService<TracerProvider>();
        var store = app.Services.GetRequiredService<WorldStore>();
        var dataSource = app.Services.GetRequiredService<NpgsqlDataSource>();
        var outbox = new SnapshotOutbox(dataSource);
        await store.Initialize(default);
        Assert.True(traces.ForceFlush());
        exporter.Spans.Clear();

        for (var round = 0; round < 10; round++)
        {
            await store.Read(default);
            await store.Mutate(world =>
            {
                world.SimTime = world.SimTime.AddSeconds(1);
                return true;
            }, default);
            Assert.True(await OutboxPublisher.PublishPending(outbox, (_, _) => Task.CompletedTask, default));
            Assert.Null(await outbox.ReadPending(default));
        }

        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/state");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var failure = dataSource.CreateCommand("SELECT 1 / 0");
        await Assert.ThrowsAsync<PostgresException>(() => failure.ExecuteScalarAsync());
        Assert.True(traces.ForceFlush());

        var request = Assert.Single(exporter.Spans, span => span.Kind == ActivityKind.Server);
        var database = exporter.Spans.Where(span => span.Source.Name == "Npgsql").ToArray();
        var successfulRoots = database.Count(span => span.ParentSpanId == default && span.Status != ActivityStatusCode.Error);
        var failedRoots = database.Count(span => span.ParentSpanId == default && span.Status == ActivityStatusCode.Error);
        var children = database.Where(span => span.ParentSpanId != default).ToArray();
        output.WriteLine($"Npgsql exports: {successfulRoots} successful roots, {failedRoots} failed roots, {children.Length} child spans");
        Assert.Equal(1, failedRoots);
        var child = Assert.Single(children);
        Assert.Equal(request.SpanId, child.ParentSpanId);
        Assert.Equal(request.TraceId, child.TraceId);
        Assert.Equal(0, successfulRoots);
    }

    [PersistenceFact]
    public async Task Fenced_scenario_reset_rejects_replay_without_changing_the_checkpoint()
    {
        await using var dataSource = TestDataSource();
        var store = new WorldStore(dataSource);
        await store.Initialize(default);
        var before = await store.Read(default);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(store);
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<ServiceClients>();
        var key = Guid.NewGuid().ToString("N");
        builder.Configuration["SERVICE_KEY"] = key;
        await using var app = builder.Build();
        ScenarioRoutes.Map(app);
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Service-Key", key);

        var startTime = new DateTimeOffset(2026, 11, 21, 17, 30, 0, TimeSpan.Zero);
        var request = new { before.ScenarioId, before.Generation, Seed = 20261117, StartTime = startTime };
        using var reset = await client.PostAsJsonAsync("/scenarios", request);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        var after = await store.Read(default);
        Assert.NotEqual(before.ScenarioId, after.ScenarioId);
        Assert.Equal(before.Generation + 1, after.Generation);
        Assert.Equal(startTime, after.SimTime);
        var audit = Assert.Single(await store.Events(0, default), e => e.Kind == "ScenarioReset");
        Assert.Equal((after.ScenarioId, after.Generation, startTime), (audit.ScenarioId, audit.Generation, audit.SimTime));

        using var replay = await client.PostAsJsonAsync("/scenarios", request);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);

        using var staleGeneration = await client.PostAsJsonAsync("/scenarios",
            new { after.ScenarioId, before.Generation, StartTime = startTime });
        Assert.Equal(HttpStatusCode.Conflict, staleGeneration.StatusCode);
        using var staleIdentity = await client.PostAsJsonAsync("/scenarios",
            new { before.ScenarioId, after.Generation, StartTime = startTime });
        Assert.Equal(HttpStatusCode.Conflict, staleIdentity.StatusCode);
        using var incomplete = await client.PostAsJsonAsync("/scenarios", new { after.ScenarioId, StartTime = startTime });
        Assert.Equal(HttpStatusCode.BadRequest, incomplete.StatusCode);

        Assert.Equal(after.Version, (await store.Read(default)).Version);
    }

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
    public async Task Transitions_preserve_terminal_totals_and_active_inventory_without_retired_rows()
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
                    CreatedAt = world.SimTime.AddMinutes(-2), PatienceSeconds = 60, ElapsedWaitSeconds = 59 }
            ];
            world.Metrics.Generated = 18;
            world.Metrics.Completed = 5;
            world.Metrics.Abandoned = 3;
            return true;
        }, default);
        var before = await store.Read(default);

        await store.Mutate(world =>
        {
            Assert.DoesNotContain(world.Cohorts, c => c.State is "completed" or "abandoned");
            Assert.Contains(world.Cohorts, c => c.Id == "unknown");
            world.Cohorts.Add(new() { Id = "new", Count = 4, ReadyAt = world.SimTime.AddHours(1) });
            world.Metrics.Generated += 4;
            Kernel.Tick(world, [], 1);
            return true;
        }, default);
        await store.Mutate(world =>
        {
            Assert.DoesNotContain(world.Cohorts, c => c.Id == "expires");
            Kernel.Tick(world, [], 1);
            return true;
        }, default);

        var after = await store.Read(default);
        Assert.Equal(13, after.Cohorts.Sum(c => c.Count));
        Assert.Equal(22, after.Metrics.Generated);
        Assert.Equal(5, after.Metrics.Completed);
        Assert.Equal(4, after.Metrics.Abandoned);
        Assert.Equal(before.SimTime.AddSeconds(2), after.SimTime);
        Assert.DoesNotContain(after.Cohorts, c => c.State is "completed" or "abandoned");
        Assert.Contains(after.Cohorts, c => c.Id == "future");
        await using var publication = dataSource.CreateCommand("""
            SELECT state->>'version'=(SELECT body->>'version' FROM metro_outbox)
            FROM metro_checkpoint WHERE id=1
            """);
        Assert.True((bool)(await publication.ExecuteScalarAsync())!);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.Mutate<bool>(world =>
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
            await store.Mutate(world =>
            {
                Kernel.Tick(world, demand, 20, driveTrains: true);
                return true;
            }, default);
            var actual = await store.Read(default);
            expected.Version++;
            expected.UpdatedAt = actual.UpdatedAt;
            var expectedView = Wire.Clone(expected);
            var actualView = Wire.Clone(actual);
            expectedView.Cohorts.RemoveAll(c => c.State is "completed" or "abandoned");
            if (expectedView.History.Count > 250)
                expectedView.History.RemoveRange(0, expectedView.History.Count - 250);
            expectedView.Cohorts.Sort((a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));
            actualView.Cohorts.Sort((a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));
            Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(expectedView, Wire.Json),
                JsonSerializer.SerializeToElement(actualView, Wire.Json)), $"Inventory differed after step {step}.");
            Assert.Equal(actual.Metrics.Generated, actual.Metrics.Completed + actual.Metrics.Abandoned + actual.Cohorts.Sum(c => c.Count));
            Assert.Equal(original.ScenarioId, actual.ScenarioId);
            Assert.Equal(original.Generation, actual.Generation);
            Assert.Equal(Fleet.Trains.Count, actual.Trains.Count);
            await using var publication = dataSource.CreateCommand("""
                SELECT state->>'version'=(SELECT body->>'version' FROM metro_outbox)
                FROM metro_checkpoint WHERE id=1
                """);
            Assert.True((bool)(await publication.ExecuteScalarAsync())!);
        }
    }

    [PersistenceFact]
    public async Task Unavailable_publisher_cannot_grow_pending_snapshots()
    {
        await using var dataSource = TestDataSource();
        var store = new WorldStore(dataSource);
        await store.Initialize(default);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => store.Mutate(world => world.SimTime = world.SimTime.AddSeconds(1), default)));
        var world = await store.Read(default);
        await using var query = dataSource.CreateCommand("SELECT count(*), max((body->>'version')::bigint) FROM metro_outbox");
        await using var reader = await query.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt64(0));
        Assert.Equal(world.Version, reader.GetInt64(1));
        Assert.Equal(20, world.Version);
    }

    [PersistenceFact]
    public async Task Late_confirmation_does_not_remove_newer_snapshot_or_block_writers()
    {
        await using var dataSource = TestDataSource();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        var store = new WorldStore(dataSource);
        await store.Initialize(ct);
        var initial = await store.Read(ct);
        var outbox = new SnapshotOutbox(dataSource);
        var inFlight = new TaskCompletionSource<ProjectionEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirm = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publishing = OutboxPublisher.PublishPending(outbox, (snapshot, token) =>
        {
            inFlight.SetResult(snapshot);
            return confirm.Task.WaitAsync(token);
        }, ct);
        Assert.Equal(initial.Version, (await inFlight.Task.WaitAsync(ct)).Version);
        await store.Mutate(w => w.SimTime = w.SimTime.AddMinutes(1), ct);
        var newer = await store.Read(ct);
        confirm.SetResult();
        await publishing;
        Assert.Equal(newer.Version, (await outbox.ReadPending(ct))?.Version);

        await Assert.ThrowsAsync<IOException>(() => OutboxPublisher.PublishPending(outbox,
            (_, _) => throw new IOException("Broker unavailable"), ct));
        Assert.Equal(newer.Version, (await outbox.ReadPending(ct))?.Version);

        await OutboxPublisher.PublishPending(outbox, (snapshot, _) =>
        {
            Assert.Equal(newer.Version, snapshot.Version);
            return Task.CompletedTask;
        }, ct);
        Assert.Null(await outbox.ReadPending(ct));
    }

    [PublicationFact]
    public async Task Publisher_and_information_deliver_the_newest_snapshot_and_restore_freshness()
    {
        await using var dataSource = TestDataSource();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        var store = new WorldStore(dataSource);
        await store.Initialize(ct);
        var bootstrap = await store.Read(ct);
        bootstrap.UpdatedAt -= TimeSpan.FromMinutes(1);
        var factory = new ConnectionFactory
        {
            HostName = "127.0.0.1",
            Port = int.Parse(Environment.GetEnvironmentVariable("METROOPS_TEST_BROKER_PORT")!)
        };
        await using var receiving = await factory.CreateConnectionAsync(ct);
        await using var sending = await factory.CreateConnectionAsync(ct);
        await using var redis = await ConnectionMultiplexer.ConnectAsync($"127.0.0.1:{Environment.GetEnvironmentVariable("METROOPS_TEST_REDIS_PORT")}");
        await using var scenario = new ServiceCollection().AddSingleton(sending).BuildServiceProvider();
        await using var information = new ServiceCollection().AddSingleton(receiving).AddSingleton<IConnectionMultiplexer>(redis).BuildServiceProvider();
        using var client = new HttpClient(new ScenarioBootstrap(bootstrap));
        using var subscriber = new InformationProjection(information, new ServiceClients(new BootstrapClientFactory(client)),
            NullLogger<InformationProjection>.Instance);
        using var publisher = new OutboxPublisher(dataSource, scenario, new RuntimeHealth { Ready = true }, NullLogger<OutboxPublisher>.Instance);
        await subscriber.StartAsync(ct);
        try
        {
            while (subscriber.Latest is null) await Task.Delay(25, ct);
            Assert.Equal(HealthStatus.Unhealthy, (await subscriber.CheckHealthAsync(new(), ct)).Status);

            await store.Mutate(world => world.SimTime = world.SimTime.AddMinutes(1), ct);
            var expected = await store.Read(ct);
            await publisher.StartAsync(ct);
            while (subscriber.Latest?.Version != expected.Version || await new SnapshotOutbox(dataSource).ReadPending(ct) is not null)
                await Task.Delay(25, ct);
            Assert.Equal(HealthStatus.Healthy, (await subscriber.CheckHealthAsync(new(), ct)).Status);
            var payload = (string?)await redis.GetDatabase().StringGetAsync("metro:public-world");
            Assert.Equal(expected.Version, JsonSerializer.Deserialize<RiderWorld>(payload!, Wire.Json)!.Version);
        }
        finally
        {
            await publisher.StopAsync(CancellationToken.None);
            await subscriber.StopAsync(CancellationToken.None);
        }
    }

    [PersistenceFact]
    public async Task Legacy_mature_checkpoint_and_backlog_migrate_without_resetting_world_or_command_receipts()
    {
        await using var dataSource = TestDataSource();
        var store = new WorldStore(dataSource);
        var world = Kernel.Create();
        world.Generation = 10;
        world.Version = 57_214;
        world.SimTime = world.SimTime.AddHours(11);
        world.Cohorts = Enumerable.Range(0, 21_509).Select(i => new Cohort
        {
            Id = $"retired:{i}", Count = 2,
            State = i % 2 == 0 ? "completed" : "abandoned",
            Legs = [new() { From = new string('x', 500) }]
        }).ToList();
        world.Cohorts.Add(new() { Id = "future", ReadyAt = world.SimTime.AddHours(1), Count = 5 });
        world.Metrics = new() { Generated = 43_023, Completed = 21_510, Abandoned = 21_508 };
        world.History = Enumerable.Range(1, 260).Select(i => new HistoryEvent { Id = $"checkpoint:{i}" }).ToList();
        var envelope = new CommandEnvelope("retain-command", world.ScenarioId, 10, null, "operator", "test",
            new("hold-train", "blue-1"));
        var prior = new CommandResult(true, "applied", "Recorded.", world.Version);
        await using (var seed = dataSource.CreateCommand("""
            CREATE TABLE metro_checkpoint (id integer PRIMARY KEY CHECK(id=1), state jsonb NOT NULL);
            CREATE TABLE metro_outbox (sequence bigserial PRIMARY KEY, event_id text NOT NULL UNIQUE,
                body jsonb NOT NULL, published boolean NOT NULL DEFAULT false);
            CREATE TABLE metro_events (sequence bigserial PRIMARY KEY, event_id text NOT NULL UNIQUE,
                scenario_id text NOT NULL, generation integer NOT NULL, version bigint NOT NULL, sim_time timestamptz NOT NULL, body jsonb NOT NULL);
            CREATE TABLE metro_commands (command_id text PRIMARY KEY, scenario_id text NOT NULL,
                generation integer NOT NULL, body jsonb NOT NULL, result jsonb NOT NULL, recorded_at timestamptz NOT NULL DEFAULT now());
            INSERT INTO metro_checkpoint VALUES (1,@state::jsonb);
            INSERT INTO metro_outbox(event_id,body) SELECT 'old:'||n,@state::jsonb FROM generate_series(1,3) AS n;
            INSERT INTO metro_events(event_id,scenario_id,generation,version,sim_time,body)
              SELECT 'event:'||n,'legacy',CASE WHEN n<6000 THEN 9 ELSE 10 END,n,now(),'{}'
              FROM generate_series(1,10005) AS n ORDER BY n;
            INSERT INTO metro_commands(command_id,scenario_id,generation,body,result)
              VALUES (@id,'legacy',10,@command::jsonb,@result::jsonb);
            """))
        {
            seed.Parameters.AddWithValue("state", JsonSerializer.Serialize(world, Wire.Json));
            seed.Parameters.AddWithValue("id", envelope.CommandId);
            seed.Parameters.AddWithValue("command", JsonSerializer.Serialize(envelope, Wire.Json));
            seed.Parameters.AddWithValue("result", JsonSerializer.Serialize(prior, Wire.Json));
            await seed.ExecuteNonQueryAsync();
        }

        await store.Initialize(default);
        var after = await store.Read(default);
        Assert.Equal(world.ScenarioId, after.ScenarioId);
        Assert.Equal(world.Generation, after.Generation);
        Assert.Equal(world.Version, after.Version);
        Assert.Equal(world.SimTime, after.SimTime);
        Assert.Equal(world.UpdatedAt, after.UpdatedAt);
        Assert.Equal(world.Metrics.Completed, after.Metrics.Completed);
        Assert.Equal(world.Metrics.Abandoned, after.Metrics.Abandoned);
        Assert.Equal("future", Assert.Single(after.Cohorts).Id);
        Assert.Equal(250, after.History.Count);
        Assert.Equal("checkpoint:11", after.History[0].Id);
        Assert.Equal(prior, await store.Mutate<CommandResult>(_ => throw new InvalidOperationException("Replayed command ran again"), default, envelope));

        await store.Mutate(current =>
        {
            current.History.Add(new() { Kind = "new", SimTime = current.SimTime });
            return true;
        }, default);
        await store.Initialize(default);
        await using var query = dataSource.CreateCommand("""
            SELECT count(*), min(sequence), count(DISTINCT generation),
                (SELECT count(*) FROM metro_outbox), (SELECT max((body->>'version')::bigint) FROM metro_outbox)
            FROM metro_events
            """);
        await using var reader = await query.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(10_000, reader.GetInt64(0));
        Assert.Equal(7, reader.GetInt64(1));
        Assert.Equal(2, reader.GetInt64(2));
        Assert.Equal(1, reader.GetInt64(3));
        Assert.Equal(world.Version + 1, reader.GetInt64(4));
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

    private static NpgsqlDataSource TestDataSource() => NpgsqlDataSource.Create(TestConnectionString());

    private static string TestConnectionString()
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("METROOPS_TEST_POSTGRES"));
        if (connection.Database?.StartsWith("metroops_test_", StringComparison.Ordinal) != true)
            throw new InvalidOperationException("Persistence checks require a separate metroops_test_* database.");
        var schema = $"test_{Guid.NewGuid():N}";
        using (var bootstrap = NpgsqlDataSource.Create(connection.ConnectionString))
        using (var create = bootstrap.CreateCommand($"CREATE SCHEMA {schema}"))
            create.ExecuteNonQuery();
        connection.SearchPath = schema;
        return connection.ConnectionString;
    }

    private sealed class TraceCapture : BaseExporter<Activity>
    {
        public ConcurrentQueue<Activity> Spans { get; } = new();

        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var span in batch) Spans.Enqueue(span);
            return ExportResult.Success;
        }
    }

    private sealed class BootstrapClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class ScenarioBootstrap(WorldState world) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("/rider-state", request.RequestUri?.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(Kernel.ProjectRiders(world), options: Wire.Json)
            });
        }
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

public sealed class PublicationFactAttribute : FactAttribute
{
    public PublicationFactAttribute()
    {
        if (new[] { "METROOPS_TEST_POSTGRES", "METROOPS_TEST_BROKER_PORT", "METROOPS_TEST_REDIS_PORT" }
            .Any(name => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))))
            Skip = "Set the PostgreSQL fixture and dedicated loopback RabbitMQ and Redis test ports.";
    }
}
