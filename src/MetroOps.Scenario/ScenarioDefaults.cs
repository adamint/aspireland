using System.Diagnostics;
using MetroOps.ServiceDefaults;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace MetroOps.Scenario;

internal static class ScenarioDefaults
{
    public static void AddScenarioDefaults(this WebApplicationBuilder builder)
    {
        // Register before AddMetroDefaults adds the OTLP exporter.
        builder.Services.AddOpenTelemetry().WithTracing(t => t.AddProcessor(new StandaloneDatabaseSpanFilter()));
        builder.AddMetroDefaults(ready: false);
    }

    private sealed class StandaloneDatabaseSpanFilter : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity span)
        {
            if (span.Source.Name == "Npgsql" && span.ParentId is null && span.Status != ActivityStatusCode.Error)
                span.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
        }
    }
}
