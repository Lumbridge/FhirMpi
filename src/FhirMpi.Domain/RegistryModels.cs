namespace FhirMpi.Domain;

public enum ReviewCaseStatus
{
    Pending,
    AwaitingSecondApproval,
    Linked,
    Rejected,
    Split,
    Superseded
}

public enum ReviewCaseKind
{
    PotentialDuplicate,
    ManualDuplicate,
    Split
}

public enum ReviewDecision
{
    Link,
    Reject,
    Split,
    Supersede
}

public sealed record ReviewApproval(
    string Actor,
    ReviewDecision Decision,
    string Reason,
    DateTimeOffset RecordedAt);

public sealed record ReviewCase(
    Guid Id,
    EnterpriseId SubjectEnterpriseId,
    EnterpriseId CandidateEnterpriseId,
    double Score,
    MatchGrade Grade,
    IReadOnlyList<FieldEvidence> Evidence,
    string MatchingProfileVersion,
    ReviewCaseStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Version,
    string? DecisionReason = null,
    string? DecidedBy = null,
    ReviewCaseKind Kind = ReviewCaseKind.PotentialDuplicate,
    int RequiredApprovals = 1,
    IReadOnlyList<ReviewApproval>? Approvals = null,
    IReadOnlyList<SourceRecordKey>? SourcesToMove = null,
    long SubjectVersion = 0,
    long? CandidateVersion = null,
    bool ApprovalPolicyLocked = false);

public sealed record AuditRecord(
    Guid Id,
    string Action,
    string Actor,
    string Outcome,
    string Reason,
    EnterpriseId? EnterpriseId,
    SourceRecordKey? SourceRecord,
    DateTimeOffset RecordedAt,
    string CorrelationId);

public sealed record IngestionReceipt(
    string IdempotencyKey,
    string PayloadDigest,
    string Outcome,
    string? Response,
    DateTimeOffset RecordedAt);

public sealed record ActorContext(
    TenantId TenantId,
    string ActorId,
    SourceSystemId? SourceSystem,
    IReadOnlySet<string> Scopes,
    string CorrelationId)
{
    public bool HasScope(string scope) => Scopes.Contains(scope);
}

public sealed record TenantMatchingConfiguration(
    TenantId TenantId,
    MatchingProfile MatchingProfile,
    IReadOnlyList<BlockingKeySecret> BlockingKeySecrets,
    IReadOnlyDictionary<SourceSystemId, int> SourceTrust,
    IReadOnlySet<SourceSystemId> AuthoritativeSources,
    int RequiredLinkApprovals = 2);

public sealed record BlockingKeySecret(string Version, byte[] Secret, bool IsActive);

public sealed record SourceSystemSettings(
    SourceSystemId SourceSystem,
    int Trust,
    bool IsAuthoritative);

public sealed record TenantSettings(
    TenantId TenantId,
    string MatchingProfileVersion,
    double PossibleThreshold,
    double ProbableThreshold,
    int RequiredLinkApprovals,
    IReadOnlyList<SourceSystemSettings> Sources,
    DateTimeOffset UpdatedAt,
    string UpdatedBy,
    long Version);
