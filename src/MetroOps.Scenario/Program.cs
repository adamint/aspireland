using MetroOps.Scenario;
using MetroOps.ServiceDefaults;
using MetroOps.Simulation.Persistence;

var builder = WebApplication.CreateBuilder(args);
builder.AddMetroDefaults(ready: false);
builder.AddSimulationPersistence();
builder.AddRabbitMQClient("events", settings => settings.DisableAutoActivation = true);
builder.Services.AddHostedService<ScenarioLoop>();
builder.Services.AddHostedService<OutboxPublisher>();
var app = builder.Build();
app.UseMetroDefaults();
ScenarioRoutes.Map(app);
app.Run();
