using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MetroOps.Control;
using MetroOps.ServiceDefaults;
using OpenTelemetry;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Trace;

namespace MetroOps.Runtime.Tests;

public sealed class TelemetryTests
{
    [Fact]
    public async Task TraceCaptureExcludesOnlyTheRoutineGetPaths()
    {
        var builder = WebApplication.CreateBuilder();
        builder.AddMetroDefaults(traceRequest: ControlRoutes.TraceRequest);
        await using var app = builder.Build();
        var filter = app.Services.GetRequiredService<IOptions<AspNetCoreTraceInstrumentationOptions>>().Value.Filter!;

        Assert.False(filter(Request("GET", "/health")));
        Assert.False(filter(Request("GET", "/trains/blue-1")));
        Assert.True(filter(Request("POST", "/trains/blue-1")));
        Assert.True(filter(Request("GET", "/trains/blue-1/details")));
        Assert.True(filter(Request("POST", "/movement")));
        Assert.True(filter(Request("POST", "/journeys")));
    }

    [Fact]
    public async Task NormalDatabaseCommandChatterIsDisabledWithoutHidingOtherLogsOrWarnings()
    {
        var builder = WebApplication.CreateBuilder();
        builder.AddMetroDefaults();
        await using var app = builder.Build();
        var factory = app.Services.GetRequiredService<ILoggerFactory>();
        var command = factory.CreateLogger("Npgsql.Command");

        Assert.False(command.IsEnabled(LogLevel.Information));
        Assert.True(command.IsEnabled(LogLevel.Warning));
        Assert.True(factory.CreateLogger("MetroOps.Control").IsEnabled(LogLevel.Information));
    }

    [Fact]
    public void Repeated_failures_log_on_change_and_periodically_then_recover_once()
    {
        var clock = new StepClock();
        var events = new List<string>();
        var log = new FailureLog(clock);
        log.Report("first", () => events.Add("first"));
        log.Report("first", () => events.Add("duplicate"));
        log.Report("changed", () => events.Add("changed"));
        clock.Advance(TimeSpan.FromMinutes(1));
        log.Report("changed", () => events.Add("reminder"));
        log.Recover(() => events.Add("recovery"));
        log.Recover(() => events.Add("duplicate recovery"));
        log.Report("changed", () => events.Add("new failure"));
        Assert.Equal(["first", "changed", "reminder", "recovery", "new failure"], events);
    }

    [Fact]
    public async Task Information_health_polls_keep_unhealthy_status_without_duplicate_failure_logs()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var capture = new LogCapture();
        builder.Logging.AddProvider(capture);
        builder.AddMetroDefaults(ready: false, logHealthTransitions: true);
        await using var app = builder.Build();
        app.UseMetroDefaults();
        await app.StartAsync();
        var health = app.Services.GetRequiredService<RuntimeHealth>();
        using var client = app.GetTestClient();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health")).StatusCode);
        health.Error = "Changed cause.";
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health")).StatusCode);
        health.Error = null;
        health.Ready = true;
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(2, capture.HealthErrors.Count);
        Assert.Contains(capture.HealthErrors, error => error.Contains("Changed cause.", StringComparison.Ordinal));
    }

    [Fact]
    public void PollSuppressionDoesNotSuppressAnUnrelatedDatabaseActivity()
    {
        using var source = new ActivitySource("MetroOps.Tests.Npgsql");
        var exporter = new ActivityExporter();
        using var provider = Sdk.CreateTracerProviderBuilder().AddSource(source.Name)
            .AddProcessor(new SimpleActivityExportProcessor(exporter)).Build();

        using (SuppressInstrumentationScope.Begin())
        using (source.StartActivity("poll-read")) { }
        using (source.StartActivity("manual-write")) { }

        Assert.Contains("manual-write", exporter.Operations);
        Assert.DoesNotContain("poll-read", exporter.Operations);
    }

    private static HttpContext Request(string method, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        return context;
    }

    private sealed class ActivityExporter : BaseExporter<Activity>
    {
        public ConcurrentBag<string> Operations { get; } = [];

        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch) Operations.Add(activity.OperationName);
            return ExportResult.Success;
        }
    }

    private sealed class StepClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public void Advance(TimeSpan span) => ticks += span.Ticks;
    }

    private sealed class LogCapture : ILoggerProvider, ILogger
    {
        public ConcurrentBag<string> HealthErrors { get; } = [];
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (logLevel == LogLevel.Error && message.Contains("runtime", StringComparison.Ordinal))
                HealthErrors.Add(message);
        }
        public void Dispose() { }
    }
}
