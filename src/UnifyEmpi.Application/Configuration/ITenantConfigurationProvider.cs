using System.Collections.Concurrent;
using UnifyEmpi.Domain;
using UnifyEmpi.Storage.Abstractions;

namespace UnifyEmpi.Application.Configuration;

public interface ITenantConfigurationProvider
{
    ValueTask<TenantMatchingConfiguration> GetConfigurationAsync(
        TenantId tenantId,
        CancellationToken cancellationToken);

    void Invalidate(TenantId tenantId);
}

public sealed class StaticTenantConfigurationProvider(
    IReadOnlyDictionary<TenantId, TenantMatchingConfiguration> configurations)
    : ITenantConfigurationProvider
{
    public ValueTask<TenantMatchingConfiguration> GetConfigurationAsync(
        TenantId tenantId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            configurations.TryGetValue(tenantId, out var configuration)
                ? configuration
                : throw new RegistryAuthorisationException(
                    $"Tenant '{tenantId}' is not configured."));
    }

    public void Invalidate(TenantId tenantId) => _ = tenantId;
}

public sealed class StoredTenantConfigurationProvider(
    IReadOnlyDictionary<TenantId, TenantMatchingConfiguration> baseConfigurations,
    IIdentityRegistryStore store,
    TimeProvider timeProvider)
    : ITenantConfigurationProvider
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(15);
    private readonly ConcurrentDictionary<TenantId, CacheEntry> _cache = new();

    public async ValueTask<TenantMatchingConfiguration> GetConfigurationAsync(
        TenantId tenantId,
        CancellationToken cancellationToken)
    {
        if (!baseConfigurations.TryGetValue(tenantId, out var baseConfiguration))
        {
            throw new RegistryAuthorisationException($"Tenant '{tenantId}' is not configured.");
        }

        var now = timeProvider.GetUtcNow();
        if (_cache.TryGetValue(tenantId, out var cached) && cached.ExpiresAt > now)
        {
            return cached.Configuration;
        }

        var context = new ActorContext(
            tenantId,
            "tenant-configuration-provider",
            null,
            new HashSet<string>(StringComparer.Ordinal) { "mpi.admin" },
            Guid.CreateVersion7().ToString("D"));
        var settings = await store.GetTenantSettingsAsync(context, cancellationToken);
        var effective = settings is null
            ? baseConfiguration
            : ApplySettings(baseConfiguration, settings);
        _cache[tenantId] = new CacheEntry(effective, now.Add(CacheDuration));
        return effective;
    }

    public void Invalidate(TenantId tenantId) => _cache.TryRemove(tenantId, out _);

    private static TenantMatchingConfiguration ApplySettings(
        TenantMatchingConfiguration baseConfiguration,
        TenantSettings settings)
    {
        if (settings.TenantId != baseConfiguration.TenantId)
        {
            throw new InvalidOperationException(
                "Stored configuration cannot cross a tenant boundary.");
        }

        var sourceTrust = settings.Sources.ToDictionary(
            static source => source.SourceSystem,
            static source => source.Trust);
        var authoritative = settings.Sources
            .Where(static source => source.IsAuthoritative)
            .Select(static source => source.SourceSystem)
            .ToHashSet();
        return baseConfiguration with
        {
            MatchingProfile = baseConfiguration.MatchingProfile with
            {
                Version = settings.MatchingProfileVersion,
                PossibleThreshold = settings.PossibleThreshold,
                ProbableThreshold = settings.ProbableThreshold
            },
            SourceTrust = sourceTrust,
            AuthoritativeSources = authoritative,
            RequiredLinkApprovals = settings.RequiredLinkApprovals
        };
    }

    private sealed record CacheEntry(
        TenantMatchingConfiguration Configuration,
        DateTimeOffset ExpiresAt);
}

public static class DefaultTenantConfigurationFactory
{
    public static TenantMatchingConfiguration CreateDevelopment(
        string tenantId = "demo",
        string sourceSystem = "demo-source")
    {
        var tenant = new TenantId(tenantId);
        return new TenantMatchingConfiguration(
            tenant,
            MatchingProfile.UkDefault,
            [new BlockingKeySecret("v1", "development-only-change-me"u8.ToArray(), true)],
            new Dictionary<SourceSystemId, int>
            {
                [new SourceSystemId(sourceSystem)] = 100
            },
            new HashSet<SourceSystemId>
            {
                new(sourceSystem)
            },
            2);
    }
}
