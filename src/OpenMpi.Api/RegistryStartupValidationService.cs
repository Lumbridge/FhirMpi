using OpenMpi.Storage.Abstractions;

namespace OpenMpi.Api;

public sealed class RegistryStartupValidationService(
    IIdentityRegistryStore store,
    IHostApplicationLifetime lifetime,
    ILogger<RegistryStartupValidationService> logger) : IHostedService
{
    private static readonly Action<ILogger, string, Exception?> LogProviderReady =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(1101, nameof(LogProviderReady)),
            "Registry provider {Provider} passed capability validation");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var health = await store.CheckHealthAsync(cancellationToken);
        var capabilities = health.Capabilities;
        if (!health.IsHealthy ||
            !capabilities.SupportsAtomicMutations ||
            !capabilities.SupportsOptimisticConcurrency ||
            !capabilities.SupportsIdempotency ||
            !capabilities.SupportsOpaquePagination ||
            capabilities.MaximumCandidatePageSize < 500)
        {
            lifetime.StopApplication();
            throw new InvalidOperationException(
                $"Registry provider '{health.Provider}' is unhealthy or lacks required capabilities.");
        }

        LogProviderReady(logger, health.Provider, null);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
