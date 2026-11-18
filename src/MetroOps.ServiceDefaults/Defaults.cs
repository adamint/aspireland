using System.Data.Common;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MetroOps.ServiceDefaults;

public static class Defaults
{
    public static void AddMetroDefaults(this WebApplicationBuilder builder, bool ready = true,
        Func<HttpRequest, bool>? traceRequest = null)
    {
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
        builder.Logging.AddFilter("Npgsql.Command", LogLevel.Warning);
        builder.AddMetroTelemetry(traceRequest);
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
        builder.Services.AddServiceDiscovery();
        builder.Services.ConfigureHttpClientDefaults(http => http.AddServiceDiscovery());
        builder.Services.AddHttpClient("domain", c => c.Timeout = TimeSpan.FromSeconds(90));
        builder.Services.AddSingleton<ServiceClients>();
        builder.Services.AddSingleton(new RuntimeHealth { Ready = ready });
        builder.Services.AddHealthChecks().AddCheck<RuntimeHealth>("runtime");
    }

    public static void UseMetroDefaults(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            try { await next(context); }
            catch (Exception ex) when (ex is UpstreamException or HttpRequestException or DbException or InvalidOperationException or ArgumentException or JsonException)
            {
                if (context.Response.HasStarted) throw;
                context.Response.StatusCode = ex switch
                {
                    UpstreamException upstream => (int)upstream.Status,
                    ArgumentException or JsonException => 400,
                    InvalidOperationException => 409,
                    _ => 503
                };
                app.Logger.LogError(ex, "Request failed in {Service} at {Path}", app.Environment.ApplicationName, context.Request.Path);
                await context.Response.WriteAsJsonAsync(new
                {
                    error = ex is DbException
                        ? "The authoritative store is unavailable. No movement or state change was permitted."
                        : ex.Message
                });
            }
        });
        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            ResponseWriter = (context, report) => context.Response.WriteAsJsonAsync(new
            {
                status = report.Status == HealthStatus.Healthy ? "healthy" : "unhealthy",
                service = app.Environment.ApplicationName,
                warning = context.RequestServices.GetRequiredService<RuntimeHealth>().Error,
                checks = report.Entries.ToDictionary(e => e.Key, e => e.Value.Status.ToString().ToLowerInvariant())
            }, context.RequestAborted)
        });
    }
}
