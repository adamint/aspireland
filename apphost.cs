#:package Aspire.Hosting.Dotnet@13.6.5-preview.1.26559.7
#:package Aspire.Hosting.Foundry@13.6.5-preview.1.26559.7
#:package Aspire.Hosting.JavaScript@13.6.5
#:package Aspire.Hosting.PostgreSQL@13.6.5
#:package Aspire.Hosting.Python@13.6.5
#:package Aspire.Hosting.RabbitMQ@13.6.5
#:package Aspire.Hosting.Redis@13.6.5
#:sdk Aspire.AppHost.Sdk@13.6.5
#:property AspireUseCliBundle=true
#:property UserSecretsId=aspireland-metroops-local
#:property TreatProjectReferencesAsResources=false
#:project src/MetroOps.Contracts/MetroOps.Contracts.csproj

using System.Security.Cryptography;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dotnet;
using Aspire.Hosting.Foundry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MetroOps.Contracts;

#pragma warning disable ASPIREDOTNETPROJECT001

var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
{
    Args = args,
    DashboardApplicationName = "Aspireland MetroOps"
});
const string demoCommandClient = "demo-read";
builder.Services.AddHttpClient(demoCommandClient, client => client.Timeout = TimeSpan.FromSeconds(5));

var serviceKey = builder.AddParameter("service-key",
    () => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), secret: true);
var workerKey = builder.AddParameter("worker-key",
    () => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), secret: true);
var operatorKey = builder.AddParameter("operator-key",
    () => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), secret: true);
var agentKey = builder.AddParameter("agent-key",
    () => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), secret: true);

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume(builder.Configuration["MetroOps:PostgresVolume"] ?? "aspireland-metroops-postgres");
var database = postgres.AddDatabase("metro");
var broker = builder.AddRabbitMQ("events");
var cache = builder.AddRedis("cache");
var demand = builder.AddPythonApp("demand", "python/demand", "main.py")
    .WithUv().WithHttpEndpoint(env: "PORT").WithHttpHealthCheck("/health");
var analytics = builder.AddPythonApp("analytics", "python/analytics", "main.py")
    .WithUv().WithHttpEndpoint(env: "PORT").WithHttpHealthCheck("/health")
    .WithHttpCommand("/inventory", "Check passenger inventory", endpointName: "http", commandName: "check-passenger-inventory",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Get, HttpClientName = demoCommandClient, ResultMode = HttpCommandResultMode.Json
        });

var scenario = builder.AddDotnetProject("scenario", "src/MetroOps.Scenario/MetroOps.Scenario.csproj")
    .WithHttpEndpoint().WithHttpHealthCheck("/health")
    .WithReference(database).WithReference(broker).WithReference(demand)
    .WithEnvironment("SERVICE_KEY", serviceKey)
    .WaitFor(database).WaitFor(broker).WaitFor(demand);
analytics.WithEnvironment("SCENARIO_URL", scenario.GetEndpoint("http")).WaitFor(scenario);

var control = builder.AddDotnetProject("control", "src/MetroOps.Control/MetroOps.Control.csproj")
    .WithHttpEndpoint().WithHttpHealthCheck("/health")
    .WithReference(database).WithEnvironment("WORKER_KEY", workerKey)
    .WaitFor(database).WaitFor(scenario);
var dispatch = builder.AddDotnetProject("dispatch", "src/MetroOps.Dispatch/MetroOps.Dispatch.csproj")
    .WithHttpEndpoint().WithHttpHealthCheck("/health")
    .WithReference(database).WithEnvironment("SERVICE_KEY", serviceKey).WithEnvironment("AGENT_KEY", agentKey)
    .WaitFor(database).WaitFor(scenario);
var incidents = builder.AddDotnetProject("incidents", "src/MetroOps.Incidents/MetroOps.Incidents.csproj")
    .WithHttpEndpoint().WithHttpHealthCheck("/health")
    .WithReference(database).WithEnvironment("SERVICE_KEY", serviceKey)
    .WaitFor(database).WaitFor(scenario);
var planner = builder.AddDotnetProject("planner", "src/MetroOps.Planner/MetroOps.Planner.csproj")
    .WithHttpEndpoint().WithHttpHealthCheck("/health")
    .WithReference(scenario).WaitFor(scenario);
var information = builder.AddDotnetProject("information", "src/MetroOps.Information/MetroOps.Information.csproj")
    .WithHttpEndpoint().WithHttpHealthCheck("/health")
    .WithReference(scenario).WithReference(broker).WithReference(cache)
    .WaitFor(scenario).WaitFor(broker).WaitFor(cache)
    .WithHttpCommand("/stations/airport/arrivals", "Inspect Airport arrivals", endpointName: "http", commandName: "inspect-airport-arrivals",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Get, HttpClientName = demoCommandClient, ResultMode = HttpCommandResultMode.Json
        });
var ai = builder.AddDotnetProject("ai-operations", "src/MetroOps.AiHost/MetroOps.AiHost.csproj")
    .WithHttpEndpoint().WithHttpHealthCheck("/health")
    .WithReference(scenario).WithReference(planner).WithReference(information).WithReference(dispatch)
    .WithEnvironment("AGENT_KEY", agentKey).WithEnvironment("OPERATOR_KEY", operatorKey)
    .WaitFor(scenario).WaitFor(planner).WaitFor(information).WaitFor(dispatch);
if (builder.Configuration.GetValue("MetroOps:ProvisionFoundry", false))
{
    var foundry = builder.AddFoundry("aspireland-foundry");
    var chat = foundry.AddDeployment("chat", FoundryModel.OpenAI.Gpt41);
    var transcription = foundry.AddDeployment("transcription", FoundryModel.OpenAI.Gpt4oMiniTranscribe);
    var speech = foundry.AddDeployment("speech", FoundryModel.OpenAI.Tts);
    ai.WithReference(chat).WithReference(transcription).WithReference(speech)
        .WithEnvironment("AZURE_TENANT_ID", builder.Configuration["Azure:TenantId"])
        .WaitFor(foundry);
}

var operations = builder.AddDotnetProject("operations-api", "src/MetroOps.OperationsApi/MetroOps.OperationsApi.csproj")
    .WithHttpEndpoint().WithHttpHealthCheck("/health")
    .WithReference(scenario).WithReference(planner).WithReference(information).WithReference(analytics)
    .WithReference(ai).WithReference(dispatch).WithReference(incidents)
    .WithEnvironment("SERVICE_KEY", serviceKey).WithEnvironment("OPERATOR_KEY", operatorKey)
    .WaitFor(ai).WaitFor(incidents).WaitFor(analytics);
var passenger = builder.AddDotnetProject("passenger-api", "src/MetroOps.PassengerApi/MetroOps.PassengerApi.csproj")
    .WithHttpEndpoint().WithHttpHealthCheck("/health")
    .WithReference(planner).WithReference(information).WithReference(analytics)
    .WithReference(ai)
    .WaitFor(planner).WaitFor(information).WaitFor(analytics).WaitFor(ai);

foreach (var train in Fleet.Trains)
{
    builder.AddDotnetProject($"train-{train.Id}", "src/MetroOps.TrainWorker/MetroOps.TrainWorker.csproj")
        .WithHttpEndpoint().WithHttpHealthCheck("/health")
        .WithEnvironment("TRAIN_ID", train.Id).WithEnvironment("WORKER_KEY", workerKey)
        .WithReference(control)
        .WaitFor(control);
}

builder.AddViteApp("operations-web", "web").WithEnvironment("VITE_SITE", "operations")
    .WithEnvironment("API_URL", operations.GetEndpoint("http")).WithEnvironment("OPERATOR_KEY", operatorKey)
    .WaitFor(operations).WithExternalHttpEndpoints();
builder.AddViteApp("passenger-web", "web").WithEnvironment("VITE_SITE", "passenger")
    .WithEnvironment("API_URL", passenger.GetEndpoint("http"))
    .WaitFor(passenger).WithExternalHttpEndpoints();

builder.Build().Run();
