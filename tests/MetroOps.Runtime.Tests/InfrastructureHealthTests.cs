using MetroOps.ServiceDefaults;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MetroOps.Runtime.Tests;

public sealed class InfrastructureHealthTests
{
    [Fact]
    public async Task ReadinessStaysUnhealthyUntilInitializationAndAfterAClockFailure()
    {
        var health = new RuntimeHealth();
        Assert.Equal(HealthStatus.Unhealthy, (await health.CheckHealthAsync(new())).Status);
        health.Ready = true;
        Assert.Equal(HealthStatus.Healthy, (await health.CheckHealthAsync(new())).Status);
        health.Error = "The simulation clock is waiting for its durable store or demand service.";
        Assert.Equal(HealthStatus.Unhealthy, (await health.CheckHealthAsync(new())).Status);
        health.Error = null;
        Assert.Equal(HealthStatus.Healthy, (await health.CheckHealthAsync(new())).Status);
    }
}
