using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MetroOps.ServiceDefaults;

public sealed class RuntimeHealth : IHealthCheck
{
    public bool Ready { get; set; }
    public string? Error { get; set; }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(Ready && Error is null
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy(Error ?? "The authoritative store is not initialized."));
}
