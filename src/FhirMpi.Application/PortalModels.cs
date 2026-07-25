using FhirMpi.Domain;
using FhirMpi.Storage.Abstractions;

namespace FhirMpi.Application;

public sealed record PatientIdentityView(
    CanonicalPatient CanonicalPatient,
    EnterprisePerson Person,
    IReadOnlyList<SourcePatientRecord> SourcePatients);

public sealed record ReviewCaseDetail(
    ReviewCase ReviewCase,
    PatientIdentityView Subject,
    PatientIdentityView? Candidate,
    int EffectiveRequiredApprovals);

public sealed record DuplicateSearchResult(
    EnterpriseId SubjectEnterpriseId,
    IReadOnlyList<MatchResult> Matches,
    int CandidateCount,
    string MatchingProfileVersion);

public sealed record RegistryOperationalSummary(
    RegistryStoreHealth Store,
    int PendingReviews,
    int AwaitingSecondApproval,
    int RecentDecisions,
    TenantSettings Settings,
    DateTimeOffset GeneratedAt);
