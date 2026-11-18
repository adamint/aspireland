using MetroOps.Information;
using MetroOps.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddMetroDefaults();
builder.AddRabbitMQClient("events", settings => settings.DisableAutoActivation = true);
builder.AddRedisClient("cache", settings => settings.DisableAutoActivation = true);
builder.Services.AddSingleton<InformationProjection>();
builder.Services.AddHostedService(s => s.GetRequiredService<InformationProjection>());
builder.Services.AddHealthChecks().AddCheck<InformationProjection>("projection");
var app = builder.Build();
app.UseMetroDefaults();
InformationRoutes.Map(app);
app.Run();
