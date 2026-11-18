using MetroOps.Dispatch;
using MetroOps.ServiceDefaults;
using MetroOps.Simulation.Persistence;

var builder = WebApplication.CreateBuilder(args);
builder.AddMetroDefaults();
builder.AddSimulationPersistence();
var app = builder.Build();
app.UseMetroDefaults();
DispatchRoutes.Map(app);
app.Run();
