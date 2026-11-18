using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MetroOps.ServiceDefaults;

public sealed class RuntimeHealth : IHealthCheck
{
    private volatile bool ready;
    public bool Ready { get => ready; set => ready = value; }
    public string? Error { get; set; }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(Ready && Error is null
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy(Error ?? "The authoritative store is not initialized."));
}
