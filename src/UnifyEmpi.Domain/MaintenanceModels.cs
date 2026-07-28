namespace UnifyEmpi.Domain;

public enum RegistryMaintenanceJobKind
{
    Reindex,
    PopulationReconciliation
}

public enum RegistryMaintenanceJobStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled
}

public enum RegistryMaintenanceJobPhase
{
    Queued,
    Validating,
    Importing,
    Rebuilding,
    Matching,
    Completed
}

public enum RegistryMaintenanceTrigger
{
    Manual,
    Scheduled
}

public sealed record RegistryMaintenanceJob(
    Guid Id,
    TenantId TenantId,
    RegistryMaintenanceJobKind Kind,
    RegistryMaintenanceJobStatus Status,
    RegistryMaintenanceJobPhase Phase,
    RegistryMaintenanceTrigger Trigger,
    string RequestedBy,
    string Reason,
    DateTimeOffset RequestedAt,
    string ConfigurationFingerprint,
    string MatchingProfileVersion,
    int BatchSize,
    long Version)
{
    public SourceSystemId? ExternalSourceSystem { get; init; }

    public string? ScheduleKey { get; init; }

    public DateTimeOffset? WindowStart { get; init; }

    public DateTimeOffset? WindowEnd { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public DateTimeOffset? NextAttemptAt { get; init; }

    public string? Cursor { get; init; }

    public string? ExternalCursor { get; init; }

    public string? LeaseOwner { get; init; }

    public DateTimeOffset? LeaseExpiresAt { get; init; }

    public string? LastError { get; init; }

    public bool CancellationRequested { get; init; }

    public long Validated { get; init; }

    public long Scanned { get; init; }

    public long Imported { get; init; }

    public long Updated { get; init; }

    public long Unchanged { get; init; }

    public long ReviewCasesCreated { get; init; }

    public long Warnings { get; init; }

    public long FailedItems { get; init; }

    public int Attempts { get; init; }
}

public sealed record ExternalPatientRecord(
    SourceSystemId SourceSystem,
    string LocalId,
    string ResourceId,
    string SourceVersion,
    DateTimeOffset LastUpdated,
    IdentityProfile Profile,
    string PayloadDigest);

public sealed record ExternalPatientPage(
    IReadOnlyList<ExternalPatientRecord> Items,
    string? NextCursor);
