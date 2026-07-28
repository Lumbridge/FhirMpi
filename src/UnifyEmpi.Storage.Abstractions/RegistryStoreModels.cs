using UnifyEmpi.Domain;

namespace UnifyEmpi.Storage.Abstractions;

public sealed record CandidatePage(
    IReadOnlyList<CanonicalPatient> Items,
    bool IsTruncated);

public sealed record Page<T>(
    IReadOnlyList<T> Items,
    string? NextCursor);

public sealed record CanonicalPatientSearch(
    string? IdentifierSystem = null,
    string? IdentifierValue = null,
    string? FamilyName = null,
    DateOnly? BirthDate = null,
    int Count = 20,
    string? Cursor = null);

public sealed record PersonSearch(
    EnterpriseId? EnterpriseId = null,
    int Count = 20,
    string? Cursor = null);

public sealed record ReviewCaseSearch(
    ReviewCaseStatus? Status = ReviewCaseStatus.Pending,
    ReviewCaseKind? Kind = null,
    int Count = 50,
    string? Cursor = null);

public sealed record AuditRecordSearch(
    string? Action = null,
    string? Actor = null,
    string? Outcome = null,
    EnterpriseId? EnterpriseId = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Count = 50,
    string? Cursor = null);

public sealed record MaintenanceJobSearch(
    RegistryMaintenanceJobKind? Kind = null,
    RegistryMaintenanceJobStatus? Status = null,
    SourceSystemId? ExternalSourceSystem = null,
    string? ScheduleKey = null,
    int Count = 50,
    string? Cursor = null);

public enum RegistryEntityKind
{
    SourcePatient,
    CanonicalPatient,
    Person,
    ReviewCase,
    TenantSettings,
    MaintenanceJob
}

public sealed record ExpectedVersion(
    RegistryEntityKind Kind,
    string Id,
    long Version);

public sealed record RegistryMutation(
    IReadOnlyList<SourcePatientRecord> SourcePatients,
    IReadOnlyList<CanonicalPatient> CanonicalPatients,
    IReadOnlyList<EnterprisePerson> Persons,
    IReadOnlyList<ReviewCase> ReviewCases,
    IReadOnlyList<AuditRecord> AuditRecords,
    IReadOnlyList<ExpectedVersion> ExpectedVersions,
    IngestionReceipt? Receipt = null,
    TenantSettings? TenantSettings = null,
    IReadOnlyList<RegistryMaintenanceJob>? MaintenanceJobs = null)
{
    public static RegistryMutation Empty { get; } = new([], [], [], [], [], []);

    public IReadOnlyList<RegistryMaintenanceJob> EffectiveMaintenanceJobs =>
        MaintenanceJobs ?? [];
}

public sealed record RegistryCommitResult(
    bool WasApplied,
    bool WasIdempotent);

public sealed record RegistryStoreCapabilities(
    bool SupportsAtomicMutations,
    bool SupportsOptimisticConcurrency,
    bool SupportsIdempotency,
    bool SupportsOpaquePagination,
    int MaximumCandidatePageSize);

public sealed record RegistryStoreHealth(
    bool IsHealthy,
    string Provider,
    RegistryStoreCapabilities Capabilities,
    string? Detail = null);
