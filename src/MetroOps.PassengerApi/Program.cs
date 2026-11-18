using MetroOps.PassengerApi;
using MetroOps.PublicApi;
using MetroOps.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddMetroDefaults();
builder.Services.AddAiForwarding();
var app = builder.Build();
app.UseMetroDefaults();
PassengerRoutes.Map(app);
app.Run();
