using MetroOps.AiHost;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Services.AddServiceDiscovery();
builder.Services.ConfigureHttpClientDefaults(http => http.AddServiceDiscovery());
builder.Services.AddHealthChecks();
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddSource("MetroOps.AI"))
    .WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation());
builder.Logging.AddOpenTelemetry(options => { options.IncludeFormattedMessage = true; options.IncludeScopes = true; });
if (!string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddOtlpExporter()).WithMetrics(metrics => metrics.AddOtlpExporter());
    builder.Logging.AddOpenTelemetry(options => options.AddOtlpExporter());
}
AiEndpoints.ConfigureServices(builder);
var app = builder.Build();
app.MapHealthChecks("/health");
AiEndpoints.Map(app);
app.Run();
