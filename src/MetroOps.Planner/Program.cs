using MetroOps.Planner;
using MetroOps.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddMetroDefaults();
var app = builder.Build();
app.UseMetroDefaults();
PlannerRoutes.Map(app);
app.Run();
