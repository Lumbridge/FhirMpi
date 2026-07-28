using UnifyEmpi.Domain;
using UnifyEmpi.Storage.Abstractions;

namespace UnifyEmpi.Application;

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

public sealed record ResolutionConfigurationView(
    string MatchingProfileVersion,
    MatchingWeights Weights,
    ComparatorProfile Comparators,
    FellegiSunterModel? ProbabilityModel,
    double PossibleThreshold,
    double ProbableThreshold,
    int MaximumCandidates,
    int DefaultResultCount,
    int MaximumResultCount,
    IReadOnlySet<BlockingRuleKind> BlockingRules,
    IReadOnlySet<string> AuthoritativeIdentifierSystems,
    int RequiredLinkApprovals);
