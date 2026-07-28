using UnifyEmpi.Domain;

namespace UnifyEmpi.Application;

public interface IExternalPatientSource
{
    TenantId TenantId { get; }

    SourceSystemId SourceSystem { get; }

    ValueTask<ExternalPatientPage> ReadPageAsync(
        DateTimeOffset? changedSince,
        DateTimeOffset changedThrough,
        string? cursor,
        int count,
        CancellationToken cancellationToken);
}

public interface IExternalPatientSourceRegistry
{
    IExternalPatientSource? Find(TenantId tenantId, SourceSystemId sourceSystem);
}

public sealed class EmptyExternalPatientSourceRegistry : IExternalPatientSourceRegistry
{
    public IExternalPatientSource? Find(TenantId tenantId, SourceSystemId sourceSystem)
    {
        _ = tenantId;
        _ = sourceSystem;
        return null;
    }
}
