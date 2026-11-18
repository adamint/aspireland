using System.Net.Http.Json;
using MetroOps.Contracts;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Services.AddServiceDiscovery();
builder.Services.ConfigureHttpClientDefaults(http => http.AddServiceDiscovery());
builder.Services.AddHttpClient("control", c =>
{
    c.BaseAddress = new Uri("https+http://control");
    c.DefaultRequestHeaders.Add("X-Service-Key", builder.Configuration["WORKER_KEY"]);
    c.Timeout = TimeSpan.FromSeconds(10);
});
// This filter runs before the response; failures still reach the worker's warning/backoff.
var telemetry = builder.Services.AddOpenTelemetry().WithTracing(t => t.AddHttpClientInstrumentation(o =>
    o.FilterHttpRequestMessage = request => request.Method != HttpMethod.Get ||
        request.RequestUri is not { AbsolutePath: var path } ||
        !path.StartsWith("/trains/", StringComparison.Ordinal) ||
        path.AsSpan("/trains/".Length).TrimEnd('/').Contains('/')));
if (builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] is not null) telemetry.WithTracing(t => t.AddOtlpExporter());
builder.Services.AddHostedService<TrainWorker>();
var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { train = builder.Configuration["TRAIN_ID"] }));
app.Run();

sealed class TrainWorker(IHttpClientFactory factory, IConfiguration configuration, ILogger<TrainWorker> logger) : BackgroundService
{
    private readonly string trainId = configuration["TRAIN_ID"] ?? throw new InvalidOperationException("TRAIN_ID is required.");
    private readonly string instance = Guid.NewGuid().ToString("N");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var client = factory.CreateClient("control");
        var nextHeartbeat = DateTimeOffset.MinValue;
        string? confirmationFailure = null;
        string? movementReason = null;
        (string ScenarioId, int Generation, DateTimeOffset SimTime, long TrainVersion)? lastMovementAttempt = null;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var view = await client.GetFromJsonAsync<TrainControlView>($"/trains/{Uri.EscapeDataString(trainId)}", Wire.Json, stoppingToken)
                    ?? throw new InvalidOperationException("Controller returned no train state.");
                if (DateTimeOffset.UtcNow >= nextHeartbeat || view.Train.WorkerInstanceId != instance)
                {
                    var registration = await Post(client, "/register", new TrainRegistration(trainId, instance, view.ScenarioId, view.Generation), stoppingToken);
                    if (registration.Train?.WorkerInstanceId != instance) continue;
                    nextHeartbeat = DateTimeOffset.UtcNow.AddSeconds(10);
                }
                if (view.Train.WorkerInstanceId != instance || view.Paused) continue;
                if (view.Grant is { Completed: false } grant)
                {
                    if (view.SimTime >= grant.ArrivesAt)
                    {
                        var confirmation = await Post(client, "/confirm", new MovementConfirmation(grant.RequestId, trainId, instance, view.ScenarioId, view.Generation, grant.Id), stoppingToken);
                        if (!confirmation.Granted && confirmation.Reason != confirmationFailure)
                            logger.LogWarning("Train {TrainId} arrival was not confirmed: {Reason}", trainId, confirmation.Reason);
                        confirmationFailure = confirmation.Granted ? null : confirmation.Reason;
                    }
                }
                else if (view.Train.State is not ("reserve" or "withdrawn") && view.SimTime >= view.Train.ReadyAt)
                {
                    var observation = (view.ScenarioId, view.Generation, view.SimTime, view.Train.Version);
                    if (lastMovementAttempt == observation) continue;
                    var movement = await Post(client, "/movement", new MovementRequest($"{view.ScenarioId}:{trainId}:{view.Train.Version}", trainId, instance,
                        view.ScenarioId, view.Generation, view.Train.Version), stoppingToken);
                    lastMovementAttempt = observation;
                    if ((!movement.Granted || movement.Grant?.Completed == true) && movement.Reason != movementReason)
                        logger.LogInformation("Train {TrainId} is waiting: {Reason}", trainId, movement.Reason);
                    movementReason = movement.Granted && movement.Grant?.Completed != true ? null : movement.Reason;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException && !stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Train {TrainId} cannot communicate with control; no local movement will be inferred", trainId);
                await Task.Delay(2000, stoppingToken);
            }
        }
    }

    private static async Task<MovementResult> Post(HttpClient client, string path, object body, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync(path, body, Wire.Json, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MovementResult>(Wire.Json, ct)
            ?? throw new InvalidOperationException("Controller did not acknowledge the request.");
    }
}
