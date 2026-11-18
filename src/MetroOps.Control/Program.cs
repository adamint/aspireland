using MetroOps.Control;
using MetroOps.ServiceDefaults;
using MetroOps.Simulation.Persistence;

var builder = WebApplication.CreateBuilder(args);
builder.AddMetroDefaults(traceRequest: ControlRoutes.TraceRequest);
builder.AddSimulationPersistence();
var app = builder.Build();
app.UseMetroDefaults();
ControlRoutes.Map(app);
app.Run();
