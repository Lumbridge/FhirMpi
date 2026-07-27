using Microsoft.Extensions.Diagnostics.HealthChecks;
using UnifyEmpi.Storage.Abstractions;

namespace UnifyEmpi.Portal;

public sealed class PortalRegistryHealthCheck(IIdentityRegistryStore store) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        _ = context;
        var health = await store.CheckHealthAsync(cancellationToken);
        return health.IsHealthy
            ? HealthCheckResult.Healthy(health.Provider)
            : HealthCheckResult.Unhealthy(health.Provider);
    }
}

public sealed partial class PortalRegistryStartupService(
    IIdentityRegistryStore store,
    ILogger<PortalRegistryStartupService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var health = await store.CheckHealthAsync(cancellationToken);
        if (!health.IsHealthy ||
            !health.Capabilities.SupportsAtomicMutations ||
            !health.Capabilities.SupportsOptimisticConcurrency ||
            !health.Capabilities.SupportsOpaquePagination)
        {
            throw new InvalidOperationException(
                $"Registry provider '{health.Provider}' is unhealthy or lacks portal capabilities.");
        }

        LogReady(logger, health.Provider, null);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Task.CompletedTask;
    }

    [LoggerMessage(
        EventId = 6100,
        Level = LogLevel.Information,
        Message = "UnifyEMPI portal registry provider {Provider} is ready.")]
    private static partial void LogReady(
        ILogger logger,
        string provider,
        Exception? exception);
}
