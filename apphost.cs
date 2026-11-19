#:package Aspire.Hosting.AppHost@17.0.0
#:package Aspire.Hosting.DevTunnels@17.0.0
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
using Microsoft.Extensions.Hosting;
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

#pragma warning disable ASPIRERESOURCETRAITS001 // Resource tags and display ordering are experimental in Aspire 17.0.0.

const int riderOrder = 730;
const int dispatchConsoleOrder = 720;
const int attendeeOrder = 710;
const int aiOrder = 600;
const int simulationOrder = 500;
const int fleetOrder = 400;
const int servicesOrder = 300;
const int infrastructureOrder = 200;
const int configOrder = 100;

var serviceKey = builder.AddParameter("service-key",
    () => builder.Configuration["Parameters:service-key"] ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), secret: true)
    .WithTags("config").WithDisplayOrder(configOrder);
var workerKey = builder.AddParameter("worker-key",
    () => builder.Configuration["Parameters:worker-key"] ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), secret: true)
    .WithTags("config").WithDisplayOrder(configOrder);
var operatorKey = builder.AddParameter("operator-key",
    () => builder.Configuration["Parameters:operator-key"] ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), secret: true)
    .WithTags("config").WithDisplayOrder(configOrder);
var agentKey = builder.AddParameter("agent-key",
    () => builder.Configuration["Parameters:agent-key"] ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), secret: true)
    .WithTags("config").WithDisplayOrder(configOrder);

var postgres = builder.AddPostgres("postgres")
    .WithTags("infrastructure").WithDisplayOrder(infrastructureOrder)
    .WithDataVolume(builder.Configuration["MetroOps:PostgresVolume"]);
var database = postgres.AddDatabase("metro").WithTags("infrastructure").WithDisplayOrder(infrastructureOrder);
var broker = builder.AddRabbitMQ("events").WithTags("infrastructure").WithDisplayOrder(infrastructureOrder);
var cache = builder.AddRedis("cache").WithTags("infrastructure").WithDisplayOrder(infrastructureOrder);
var demand = builder.AddPythonApp("demand", "python/demand", "main.py")
    .WithTags("simulation").WithDisplayOrder(simulationOrder)
    .WithUv().WithHttpEndpoint(env: "PORT").WithHttpHealthCheck("/health");
var analytics = builder.AddPythonApp("analytics", "python/analytics", "main.py")
    .WithTags("simulation").WithDisplayOrder(simulationOrder)
    .WithUv().WithHttpEndpoint(env: "PORT").WithHttpHealthCheck("/health")
    .WithHttpCommand("/inventory", "Check passenger inventory", endpointName: "http", commandName: "check-passenger-inventory",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Get, HttpClientName = demoCommandClient, ResultMode = HttpCommandResultMode.Json
        });

var scenario = builder.AddDotnetProject("scenario", "src/MetroOps.Scenario/MetroOps.Scenario.csproj")
    .WithTags("simulation").WithDisplayOrder(simulationOrder)
    .WithHttpEndpoint().WithHttpHealthCheck("/health")
    .WithReference(database).WithReference(broker).WithReference(demand)
    .WithEnvironment("SERVICE_KEY", serviceKey)
    .WaitFor(database).WaitFor(broker).WaitFor(demand);
analytics.WithEnvironment("SCENARIO_URL", scenario.GetEndpoint("http")).WaitFor(scenario);

var control = builder.AddDotnetProject("control", "src/MetroOps.Control/MetroOps.Control.csproj")
    .WithTags("fleet").WithDisplayOrder(fleetOrder)
    .WithHttpEndpoint().WithHttpHealthCheck("/health")
    .WithReference(database).WithEnvironment("WORKER_KEY", workerKey)
    .WaitFor(database).WaitFor(scenario);
var dispatch = builder.AddDotnetProject("dispatch", "src/MetroOps.Dispatch/MetroOps.Dispatch.csproj")
    .WithTags("services").WithDisplayOrder(servicesOrder)
    .WithHttpEndpoint().WithHttpHealthCheck("/health")
    .WithReference(database).WithEnvironment("SERVICE_KEY", serviceKey).WithEnvironment("AGENT_KEY", agentKey)
    .WaitFor(database).WaitFor(scenario);
var incidents = builder.AddDotnetProject("incidents", "src/MetroOps.Incidents/MetroOps.Incidents.csproj")
    .WithTags("services").WithDisplayOrder(servicesOrder)
    .WithHttpEndpoint().WithHttpHealthCheck("/health")
    .WithReference(database).WithEnvironment("SERVICE_KEY", serviceKey)
    .WaitFor(database).WaitFor(scenario);
var planner = builder.AddDotnetProject("planner", "src/MetroOps.Planner/MetroOps.Planner.csproj")
    .WithTags("services").WithDisplayOrder(servicesOrder)
    .WithHttpEndpoint().WithHttpHealthCheck("/health")
    .WithReference(scenario).WaitFor(scenario);
var information = builder.AddDotnetProject("information", "src/MetroOps.Information/MetroOps.Information.csproj")
    .WithTags("services").WithDisplayOrder(servicesOrder)
    .WithHttpEndpoint().WithHttpHealthCheck("/health")
    .WithReference(scenario).WithReference(broker).WithReference(cache)
    .WaitFor(scenario).WaitFor(broker).WaitFor(cache)
    .WithHttpCommand("/stations/airport/arrivals", "Inspect Airport arrivals", endpointName: "http", commandName: "inspect-airport-arrivals",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Get, HttpClientName = demoCommandClient, ResultMode = HttpCommandResultMode.Json
        });
var ai = builder.AddDotnetProject("ai-operations", "src/MetroOps.AiHost/MetroOps.AiHost.csproj")
    .WithTags("ai").WithDisplayOrder(aiOrder)
    .WithHttpEndpoint().WithHttpHealthCheck("/health")
    .WithReference(scenario).WithReference(planner).WithReference(information).WithReference(dispatch)
    .WithEnvironment("AGENT_KEY", agentKey).WithEnvironment("OPERATOR_KEY", operatorKey)
    .WaitFor(scenario).WaitFor(planner).WaitFor(information).WaitFor(dispatch);
if (builder.Configuration.GetValue("MetroOps:ProvisionFoundry", false))
{
    var foundry = builder.AddFoundry("aspireland-foundry").WithTags("ai").WithDisplayOrder(aiOrder);
    var chat = foundry.AddDeployment("chat", FoundryModel.OpenAI.Gpt5Mini)
        .WithTags("ai").WithDisplayOrder(aiOrder)
        .WithProperties(deployment => deployment.SkuName = "GlobalStandard");
    var transcription = foundry.AddDeployment("transcription", FoundryModel.OpenAI.Gpt4oMiniTranscribe)
        .WithTags("ai").WithDisplayOrder(aiOrder)
        .WithProperties(deployment => deployment.SkuName = "GlobalStandard");
    var speech = foundry.AddDeployment("speech", FoundryModel.OpenAI.Tts)
        .WithTags("ai").WithDisplayOrder(aiOrder)
        .WithProperties(deployment => deployment.SkuName = "Standard");
    ai.WithReference(chat).WithReference(transcription).WithReference(speech)
        .WithEnvironment("AZURE_TENANT_ID", builder.Configuration["Azure:TenantId"])
        .WaitFor(foundry);
}
else
{
    foreach (var connection in new[] { "chat", "transcription", "speech" })
        if (!string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString(connection)))
            ai.WithReference(builder.AddConnectionString(connection).WithTags("ai", "config").WithDisplayOrder(configOrder));
    ai.WithEnvironment("AZURE_TENANT_ID", builder.Configuration["Azure:TenantId"]);
}

var operations = builder.AddDotnetProject("operations-api", "src/MetroOps.OperationsApi/MetroOps.OperationsApi.csproj")
    .WithTags("services").WithDisplayOrder(servicesOrder)
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
    .WithTags("services").WithDisplayOrder(servicesOrder)
    .WithHttpEndpoint().WithHttpHealthCheck("/health")
    .WithReference(planner).WithReference(information).WithReference(analytics)
    .WithReference(ai)
    .WaitFor(planner).WaitFor(information).WaitFor(analytics).WaitFor(ai);

foreach (var train in Fleet.Trains)
{
    builder.AddDotnetProject($"train-{train.Id}", "src/MetroOps.TrainWorker/MetroOps.TrainWorker.csproj")
        .WithTags("fleet").WithDisplayOrder(fleetOrder)
        .WithHttpEndpoint().WithHttpHealthCheck("/health")
        .WithEnvironment("TRAIN_ID", train.Id).WithEnvironment("WORKER_KEY", workerKey)
        .WithReference(control)
        .WaitFor(control);
}

var stablePorts = builder.Configuration.GetValue("MetroOps:StablePorts", false);
var operationsWeb = builder.AddViteApp("operations-web", "web").WithEnvironment("VITE_SITE", "operations")
    .WithEnvironment("API_URL", operations.GetEndpoint("http")).WithEnvironment("OPERATOR_KEY", operatorKey)
    .WaitFor(operations).WithExternalHttpEndpoints()
    .WithUrlForEndpoint("http", url => SetWebUrl(url, "Dispatch console", stablePorts))
    .WithTags("interactive").WithDisplayOrder(dispatchConsoleOrder);
var passengerWeb = builder.AddViteApp("passenger-web", "web").WithEnvironment("VITE_SITE", "passenger")
    .WithEnvironment("API_URL", passenger.GetEndpoint("http"))
    .WaitFor(passenger).WithExternalHttpEndpoints()
    .WithUrlForEndpoint("http", url => SetWebUrl(url, "Rider site", stablePorts))
    .WithTags("interactive").WithDisplayOrder(riderOrder);

if (stablePorts)
{
    passengerWeb.WithEndpoint("http", endpoint => SetLoopbackPort(endpoint, 17381));
    operationsWeb.WithEndpoint("http", endpoint => SetLoopbackPort(endpoint, 17382));
    passenger.WithEndpoint("http", endpoint => SetLoopbackPort(endpoint, 17383));
}

static void SetLoopbackPort(EndpointAnnotation endpoint, int port)
{
    endpoint.Port = port;
    endpoint.TargetHost = "127.0.0.1";
}

static void SetWebUrl(ResourceUrlAnnotation url, string displayText, bool stablePorts)
{
    url.DisplayText = displayText;
    if (stablePorts)
        url.Url = new UriBuilder(url.Url) { Host = "localhost" }.Uri.AbsoluteUri;
}

var attendeeTunnelId = builder.Configuration["MetroOps:AttendeeTunnelId"];
if (builder.ExecutionContext.IsRunMode && builder.Environment.IsDevelopment() && !stablePorts &&
    !string.IsNullOrWhiteSpace(attendeeTunnelId))
{
    if (!attendeeTunnelId.StartsWith("metroops-dev-", StringComparison.Ordinal))
        throw new InvalidOperationException("MetroOps:AttendeeTunnelId must start with 'metroops-dev-'. Use a dedicated private dev tunnel, not the demo tunnel.");

    var passengerPort = passenger.GetEndpoint("http").Property(EndpointProperty.Port);
    var attendee = builder.AddNodeApp("attendee-site", "web", "conference/server.mjs")
        .WithHttpEndpoint(env: "CONFERENCE_PORT")
        .WithEnvironment("PASSENGER_API_URL", ReferenceExpression.Create($"http://127.0.0.1:{passengerPort}"))
        .WithHttpHealthCheck("/")
        .WaitFor(passenger)
        .WithTags("interactive").WithDisplayOrder(attendeeOrder);

    builder.AddDevTunnel("attendee-tunnel", tunnelId: attendeeTunnelId)
        .WithReference(attendee.GetEndpoint("http"), allowAnonymous: false)
        .WaitFor(attendee)
        .WithTags("interactive").WithDisplayOrder(attendeeOrder);
}

#pragma warning restore ASPIRERESOURCETRAITS001

builder.Build().Run();
