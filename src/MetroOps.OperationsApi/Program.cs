using MetroOps.OperationsApi;
using MetroOps.PublicApi;
using MetroOps.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddMetroDefaults();
builder.Services.AddAiForwarding();
var app = builder.Build();
app.UseMetroDefaults();
OperationsRoutes.Map(app);
app.Run();
