#:package Aspire.Hosting.AppHost@17.0.0
#:package Aspire.Hosting.Dotnet@17.0.0
#:package Aspire.Hosting.Foundry@17.0.0-preview.1.26567.5
#:package Aspire.Hosting.JavaScript@17.0.0
#:package Aspire.Hosting.PostgreSQL@17.0.0
#:package Aspire.Hosting.Python@17.0.0
#:package Aspire.Hosting.RabbitMQ@17.0.0
#:package Aspire.Hosting.Redis@17.0.0
#:property AspireUseCliBundle=true
#:property UserSecretsId=aspireland-metroops-local
#:property TreatProjectReferencesAsResources=false
#:project src/MetroOps.Contracts/MetroOps.Contracts.csproj

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dotnet;
using Aspire.Hosting.Foundry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MetroOps.Contracts;

var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
{
    Args = args,
    DashboardApplicationName = "Aspireland MetroOps"
});
var privateModelInputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "ConnectionStrings:chat", "ConnectionStrings:transcription", "ConnectionStrings:speech", "Azure:TenantId"
};
// Configuration has captured these inputs; child processes must not inherit them.
foreach (var name in Environment.GetEnvironmentVariables().Keys.Cast<string>())
    if (privateModelInputs.Contains(name.Replace("__", ":")))
        Environment.SetEnvironmentVariable(name, null);
const string demoCommandClient = "demo-read";
builder.Services.AddHttpClient(demoCommandClient, client => client.Timeout = TimeSpan.FromSeconds(5));

#pragma warning disable ASPIRERESOURCETRAITS001 // WithTags is experimental in Aspire 17.0.0.

var serviceKey = builder.AddParameter("service-key",
    () => builder.Configuration["Parameters:service-key"] ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), secret: true)
    .WithTags("config");
var workerKey = builder.AddParameter("worker-key",
    () => builder.Configuration["Parameters:worker-key"] ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), secret: true)
    .WithTags("config");
var operatorKey = builder.AddParameter("operator-key",
    () => builder.Configuration["Parameters:operator-key"] ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), secret: true)
    .WithTags("config");
var agentKey = builder.AddParameter("agent-key",
    () => builder.Configuration["Parameters:agent-key"] ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), secret: true)
    .WithTags("config");

var postgres = builder.AddPostgres("postgres")
    .WithTags("infrastructure")
    .WithDataVolume(builder.Configuration["MetroOps:PostgresVolume"] ?? "aspireland-metroops-postgres");
var database = postgres.AddDatabase("metro").WithTags("infrastructure");
var broker = builder.AddRabbitMQ("events").WithTags("infrastructure");
var cache = builder.AddRedis("cache").WithTags("infrastructure");
var demand = builder.AddPythonApp("demand", "python/demand", "main.py")
    .WithTags("simulation")
    .WithUv().WithHttpEndpoint(env: "PORT").WithHttpHealthCheck("/health");
var analytics = builder.AddPythonApp("analytics", "python/analytics", "main.py")
    .WithTags("simulation")
    .WithUv().WithHttpEndpoint(env: "PORT").WithHttpHealthCheck("/health")
    .WithHttpCommand("/inventory", "Check passenger inventory", endpointName: "http", commandName: "check-passenger-inventory",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Get, HttpClientName = demoCommandClient, ResultMode = HttpCommandResultMode.Json
        });

var scenario = builder.AddDotnetProject("scenario", "src/MetroOps.Scenario/MetroOps.Scenario.csproj")
    .WithTags("simulation")
    .WithHttpEndpoint().WithHttpHealthCheck("/health")
    .WithReference(database).WithReference(broker).WithReference(demand)
    .WithEnvironment("SERVICE_KEY", serviceKey)
    .WaitFor(database).WaitFor(broker).WaitFor(demand);
analytics.WithEnvironment("SCENARIO_URL", scenario.GetEndpoint("http")).WaitFor(scenario);

var control = builder.AddDotnetProject("control", "src/MetroOps.Control/MetroOps.Control.csproj")
    .WithTags("fleet")
    .WithHttpEndpoint().WithHttpHealthCheck("/health")
    .WithReference(database).WithEnvironment("WORKER_KEY", workerKey)
    .WaitFor(database).WaitFor(scenario);
var dispatch = builder.AddDotnetProject("dispatch", "src/MetroOps.Dispatch/MetroOps.Dispatch.csproj")
    .WithTags("services")
    .WithHttpEndpoint().WithHttpHealthCheck("/health")
    .WithReference(database).WithEnvironment("SERVICE_KEY", serviceKey).WithEnvironment("AGENT_KEY", agentKey)
    .WaitFor(database).WaitFor(scenario);
var incidents = builder.AddDotnetProject("incidents", "src/MetroOps.Incidents/MetroOps.Incidents.csproj")
    .WithTags("services")
    .WithHttpEndpoint().WithHttpHealthCheck("/health")
    .WithReference(database).WithEnvironment("SERVICE_KEY", serviceKey)
    .WaitFor(database).WaitFor(scenario);
var planner = builder.AddDotnetProject("planner", "src/MetroOps.Planner/MetroOps.Planner.csproj")
    .WithTags("services")
    .WithHttpEndpoint().WithHttpHealthCheck("/health")
    .WithReference(scenario).WaitFor(scenario);
var information = builder.AddDotnetProject("information", "src/MetroOps.Information/MetroOps.Information.csproj")
    .WithTags("services")
    .WithHttpEndpoint().WithHttpHealthCheck("/health")
    .WithReference(scenario).WithReference(broker).WithReference(cache)
    .WaitFor(scenario).WaitFor(broker).WaitFor(cache)
    .WithHttpCommand("/stations/airport/arrivals", "Inspect Airport arrivals", endpointName: "http", commandName: "inspect-airport-arrivals",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Get, HttpClientName = demoCommandClient, ResultMode = HttpCommandResultMode.Json
        });
var ai = builder.AddDotnetProject("ai-operations", "src/MetroOps.AiHost/MetroOps.AiHost.csproj")
    .WithTags("ai")
    .WithHttpEndpoint().WithHttpHealthCheck("/health")
    .WithReference(scenario).WithReference(planner).WithReference(information).WithReference(dispatch)
    .WithEnvironment("AGENT_KEY", agentKey).WithEnvironment("OPERATOR_KEY", operatorKey)
    .WaitFor(scenario).WaitFor(planner).WaitFor(information).WaitFor(dispatch);
if (builder.Configuration.GetValue("MetroOps:ProvisionFoundry", false))
{
    var foundry = builder.AddFoundry("aspireland-foundry").WithTags("ai");
    var chat = foundry.AddDeployment("chat", FoundryModel.OpenAI.Gpt5Mini)
        .WithTags("ai")
        .WithProperties(deployment => deployment.SkuName = "GlobalStandard");
    var transcription = foundry.AddDeployment("transcription", FoundryModel.OpenAI.Gpt4oMiniTranscribe)
        .WithTags("ai")
        .WithProperties(deployment => deployment.SkuName = "GlobalStandard");
    var speech = foundry.AddDeployment("speech", FoundryModel.OpenAI.Tts)
        .WithTags("ai")
        .WithProperties(deployment => deployment.SkuName = "Standard");
    ai.WithReference(chat).WithReference(transcription).WithReference(speech)
        .WithEnvironment("AZURE_TENANT_ID", builder.Configuration["Azure:TenantId"])
        .WaitFor(foundry);
}
else
{
    foreach (var connection in new[] { "chat", "transcription", "speech" })
        if (!string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString(connection)))
            ai.WithReference(builder.AddConnectionString(connection).WithTags("ai", "config"));
    ai.WithEnvironment("AZURE_TENANT_ID", builder.Configuration["Azure:TenantId"]);
}

var operations = builder.AddDotnetProject("operations-api", "src/MetroOps.OperationsApi/MetroOps.OperationsApi.csproj")
    .WithTags("services")
    .WithHttpEndpoint().WithHttpHealthCheck("/health")
    .WithReference(scenario).WithReference(planner).WithReference(information).WithReference(analytics)
    .WithReference(ai).WithReference(dispatch).WithReference(incidents)
    .WithEnvironment("SERVICE_KEY", serviceKey).WithEnvironment("OPERATOR_KEY", operatorKey)
    .WaitFor(ai).WaitFor(incidents).WaitFor(analytics);

var timePresets = new Dictionary<string, (string Label, DateTimeOffset StartTime)>
{
    ["morning-rush"] = ("Morning rush", new(2026, 11, 17, 12, 30, 0, TimeSpan.Zero)),
    ["weekday-midday"] = ("Weekday midday", new(2026, 11, 17, 17, 30, 0, TimeSpan.Zero)),
    ["evening-rush"] = ("Evening rush", new(2026, 11, 17, 22, 15, 0, TimeSpan.Zero)),
    ["weekend"] = ("Weekend leisure", new(2026, 11, 21, 17, 30, 0, TimeSpan.Zero)),
    ["overnight"] = ("Quiet overnight", new(2026, 11, 22, 7, 30, 0, TimeSpan.Zero))
};
const string timeCommandClient = "simulation-time-command";
builder.Services.AddHttpClient(timeCommandClient, client => client.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
var scenarioEndpoint = scenario.GetEndpoint("http");
var operationsEndpoint = operations.GetEndpoint("http");
operations.WithCommand("set-simulation-time", "Set simulation time", async context =>
{
    if (context.Arguments.GetString("time-preset") is not { } choice || !timePresets.TryGetValue(choice, out var preset))
        return CommandResults.Failure("Choose one of the simulation time presets.");

    var ct = context.CancellationToken;
    var scenarioUrl = await scenarioEndpoint.GetValueAsync(ct);
    var operationsUrl = await operationsEndpoint.GetValueAsync(ct);
    if (scenarioUrl is null || operationsUrl is null)
        return CommandResults.Failure("Wait for Scenario and Operations API to start.");

    var key = await operatorKey.Resource.GetValueAsync(ct);
    if (string.IsNullOrEmpty(key))
        return CommandResults.Failure("The operator key is unavailable.");

    using var client = context.Services.GetRequiredService<IHttpClientFactory>().CreateClient(timeCommandClient);
    try
    {
        var current = await client.GetFromJsonAsync<ScenarioContext>($"{scenarioUrl}/context", Wire.Json, ct);
        if (current is null || string.IsNullOrEmpty(current.ScenarioId))
            return CommandResults.Failure("Scenario did not report its current identity. No reset was sent.");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{operationsUrl}/api/scenarios")
        {
            Content = JsonContent.Create(new ScenarioRequest(20261117, StartTime: preset.StartTime,
                ScenarioId: current.ScenarioId, Generation: current.Generation), options: Wire.Json)
        };
        request.Headers.Add("X-Operator-Key", key);
        using var response = await client.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Conflict)
            return CommandResults.Failure("The scenario changed before the reset. Check Dispatch before trying again.");
        if (!response.IsSuccessStatusCode)
            return CommandResults.Failure($"Operations API returned HTTP {(int)response.StatusCode}. Check Dispatch for the current scenario.");

        var result = await response.Content.ReadFromJsonAsync<ScenarioResetResult>(Wire.Json, ct);
        if (result is null)
            return CommandResults.Failure("Operations API did not confirm the reset. Check Dispatch for the current scenario.");

        var summary = $"Started a fresh {preset.Label.ToLowerInvariant()} scenario (generation {result.Generation}).";
        return CommandResults.Success(summary, summary + "\n\nOpen Dispatch to see the new clock and rider demand.",
            CommandResultFormat.Text, displayImmediately: true);
    }
    catch (Exception ex) when (ex is HttpRequestException or JsonException ||
        ex is OperationCanceledException && !ct.IsCancellationRequested)
    {
        return CommandResults.Failure("Could not confirm the scenario reset. Check Dispatch for the current scenario before retrying.");
    }
}, new CommandOptions
{
    Description = "Start a fresh scenario at a different point in the week.",
    ConfirmationMessage = "Start a fresh scenario? This resets trains, riders, incidents, and dispatch settings.",
    Arguments =
    [
        new InteractionInput
        {
            Name = "time-preset",
            Label = "Time preset",
            Description = "Starts a fresh scenario and resets trains, riders, incidents, and dispatch settings.",
            InputType = InputType.Choice,
            Required = true,
            Options = timePresets.Select(p => KeyValuePair.Create(p.Key, p.Value.Label)).ToArray()
        }
    ]
});
operations.WithCommand("trigger-random-service-alert", "Trigger random service alert", async context =>
{
    var ct = context.CancellationToken;
    var scenarioUrl = await scenarioEndpoint.GetValueAsync(ct);
    var operationsUrl = await operationsEndpoint.GetValueAsync(ct);
    if (scenarioUrl is null || operationsUrl is null)
        return CommandResults.Failure("Wait for Scenario and Operations API to start.");

    var key = await operatorKey.Resource.GetValueAsync(ct);
    if (string.IsNullOrEmpty(key))
        return CommandResults.Failure("The operator key is unavailable.");

    using var client = context.Services.GetRequiredService<IHttpClientFactory>().CreateClient(timeCommandClient);
    try
    {
        var current = await client.GetFromJsonAsync<ScenarioContext>($"{scenarioUrl}/context", Wire.Json, ct);
        if (current is null || string.IsNullOrEmpty(current.ScenarioId))
            return CommandResults.Failure("Scenario did not report its current identity. No service alert was sent.");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{operationsUrl}/api/actions")
        {
            Content = JsonContent.Create(new ActionRequest(
                new ServiceAction("random-incident"),
                current.ScenarioId,
                current.Generation,
                current.Version,
                Guid.NewGuid().ToString("N"),
                "Aspire dashboard random service alert"), options: Wire.Json)
        };
        request.Headers.Add("X-Operator-Key", key);
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return CommandResults.Failure($"Operations API returned HTTP {(int)response.StatusCode}. No service alert was confirmed.");

        var result = await response.Content.ReadFromJsonAsync<CommandResult>(Wire.Json, ct);
        if (result is null)
            return CommandResults.Failure("Operations API did not confirm a service alert.");
        if (!result.Accepted)
            return CommandResults.Failure(result.Message);

        return CommandResults.Success(result.Message, result.Message,
            CommandResultFormat.Text, displayImmediately: true);
    }
    catch (Exception ex) when (ex is HttpRequestException or JsonException ||
        ex is OperationCanceledException && !ct.IsCancellationRequested)
    {
        return CommandResults.Failure("Could not confirm a service alert. Check Dispatch before retrying.");
    }
}, new CommandOptions
{
    Description = "Open one random service alert through the authoritative incident service.",
    ConfirmationMessage = "Trigger a random service alert?"
});

var passenger = builder.AddDotnetProject("passenger-api", "src/MetroOps.PassengerApi/MetroOps.PassengerApi.csproj")
    .WithTags("services")
    .WithHttpEndpoint().WithHttpHealthCheck("/health")
    .WithReference(planner).WithReference(information).WithReference(analytics)
    .WithReference(ai)
    .WaitFor(planner).WaitFor(information).WaitFor(analytics).WaitFor(ai);

foreach (var train in Fleet.Trains)
{
    builder.AddDotnetProject($"train-{train.Id}", "src/MetroOps.TrainWorker/MetroOps.TrainWorker.csproj")
        .WithTags("fleet")
        .WithHttpEndpoint().WithHttpHealthCheck("/health")
        .WithEnvironment("TRAIN_ID", train.Id).WithEnvironment("WORKER_KEY", workerKey)
        .WithReference(control)
        .WaitFor(control);
}

builder.AddViteApp("operations-web", "web").WithEnvironment("VITE_SITE", "operations")
    .WithEnvironment("API_URL", operations.GetEndpoint("http")).WithEnvironment("OPERATOR_KEY", operatorKey)
    .WaitFor(operations).WithExternalHttpEndpoints()
    .WithUrlForEndpoint("http", url => url.DisplayText = "Dispatch console")
    .WithTags("interactive");
builder.AddViteApp("passenger-web", "web").WithEnvironment("VITE_SITE", "passenger")
    .WithEnvironment("API_URL", passenger.GetEndpoint("http"))
    .WaitFor(passenger).WithExternalHttpEndpoints()
    .WithUrlForEndpoint("http", url => url.DisplayText = "Rider site")
    .WithTags("interactive");

var attendeeUrl = builder.Configuration["MetroOps:AttendeeUrl"];
if (!string.IsNullOrWhiteSpace(attendeeUrl))
{
    if (Uri.TryCreate(attendeeUrl, UriKind.Absolute, out var attendeeUri) &&
        attendeeUri.Scheme == Uri.UriSchemeHttps && attendeeUri.Host.Length > 0 &&
        attendeeUri.UserInfo.Length == 0 && attendeeUri.Query.Length == 0 && attendeeUri.Fragment.Length == 0)
    {
        builder.AddExternalService("attendee-site", attendeeUri)
            .WithUrls(context =>
            {
                foreach (var url in context.Urls)
                    url.DisplayText = "Attendee site (Dev Tunnel)";
            })
            .WithTags("interactive");
    }
    else
    {
        Console.Error.WriteLine("Skipping the attendee site link: MetroOps:AttendeeUrl must be an absolute HTTPS URL without credentials, a query, or a fragment.");
    }
}

#pragma warning restore ASPIRERESOURCETRAITS001

builder.Build().Run();
