using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace MetroOps.ServiceDefaults;

public static class Telemetry
{
    public static void AddMetroTelemetry(this WebApplicationBuilder builder, Func<HttpRequest, bool>? traceRequest = null)
    {
        builder.Services.AddOpenTelemetry()
            .WithTracing(t => t.AddAspNetCoreInstrumentation(o => o.Filter = context =>
                    !(HttpMethods.IsGet(context.Request.Method) &&
                        string.Equals(context.Request.Path.Value?.TrimEnd('/'), "/health", StringComparison.OrdinalIgnoreCase)) &&
                    (traceRequest?.Invoke(context.Request) ?? true))
                .AddHttpClientInstrumentation().AddSource("MetroOps"))
            .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddRuntimeInstrumentationIfAvailable());
        builder.Logging.AddOpenTelemetry(o => { o.IncludeFormattedMessage = true; o.IncludeScopes = true; });
        if (!string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            builder.Services.AddOpenTelemetry().WithTracing(t => t.AddOtlpExporter()).WithMetrics(m => m.AddOtlpExporter());
            builder.Logging.AddOpenTelemetry(o => o.AddOtlpExporter());
        }
    }

    private static MeterProviderBuilder AddRuntimeInstrumentationIfAvailable(this MeterProviderBuilder builder) =>
        builder.AddMeter("System.Runtime", "Microsoft.AspNetCore.Hosting", "System.Net.Http", "MetroOps");
}
