using UnifyEmpi.Domain;

namespace UnifyEmpi.Application;

public sealed record UpsertPatientCommand(
    SourceRecordKey SourceRecord,
    IdentityProfile Profile,
    string? IdempotencyKey = null,
    string? PayloadDigest = null,
    string? ReceiptResponse = null,
    long? ExpectedVersion = null);

public sealed record UpsertPatientResult(
    SourcePatientRecord SourcePatient,
    CanonicalPatient CanonicalPatient,
    EnterprisePerson Person,
    IReadOnlyList<ReviewCase> ReviewCases,
    bool WasIdempotent,
    string? ReplayResponse = null);

public sealed record ReviewDecisionCommand(
    Guid ReviewCaseId,
    ReviewDecision Decision,
    string Reason,
    long ExpectedVersion);

public sealed record CreateDuplicateReviewCommand(
    EnterpriseId SubjectEnterpriseId,
    EnterpriseId CandidateEnterpriseId,
    string Reason,
    long SubjectVersion,
    long CandidateVersion);

public sealed record CreateSplitReviewCommand(
    EnterpriseId EnterpriseId,
    IReadOnlyList<SourceRecordKey> SourcesToMove,
    string Reason,
    long ExpectedVersion);

public sealed record UpdateTenantSettingsCommand(
    string MatchingProfileVersion,
    double PossibleThreshold,
    double ProbableThreshold,
    int RequiredLinkApprovals,
    IReadOnlyList<SourceSystemSettings> Sources,
    string Reason,
    long ExpectedVersion);

public sealed record StartReindexCommand(
    string Reason,
    int BatchSize = 50);

public sealed record StartPopulationReconciliationCommand(
    string Reason,
    int BatchSize = 50,
    SourceSystemId? ExternalSourceSystem = null,
    DateTimeOffset? ChangedSince = null,
    RegistryMaintenanceTrigger Trigger = RegistryMaintenanceTrigger.Manual,
    string? ScheduleKey = null,
    Guid? JobId = null);
