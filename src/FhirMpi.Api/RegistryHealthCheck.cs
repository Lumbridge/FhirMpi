using FhirMpi.Storage.Abstractions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FhirMpi.Api;

public sealed class RegistryHealthCheck(IIdentityRegistryStore store) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var health = await store.CheckHealthAsync(cancellationToken);
        return health.IsHealthy
            ? HealthCheckResult.Healthy(health.Provider)
            : HealthCheckResult.Unhealthy(health.Provider);
    }
}
