using MetroOps.Incidents;
using MetroOps.ServiceDefaults;
using MetroOps.Simulation.Persistence;

var builder = WebApplication.CreateBuilder(args);
builder.AddMetroDefaults();
builder.AddSimulationPersistence();
var app = builder.Build();
app.UseMetroDefaults();
IncidentRoutes.Map(app);
app.Run();
