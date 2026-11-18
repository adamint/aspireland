using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
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
}
