using UnifyEmpi.Application;
using UnifyEmpi.Domain;

namespace UnifyEmpi.Portal;

public static class ReviewAssistant
{
    public static ReviewAssessment Assess(ReviewCaseDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        if (detail.Candidate is null || detail.ReviewCase.Kind == ReviewCaseKind.Split)
        {
            return ReviewAssessment.Empty;
        }

        var subject = detail.Subject.CanonicalPatient.Profile;
        var candidate = detail.Candidate.CanonicalPatient.Profile;
        var evidence = detail.ReviewCase.Evidence.ToDictionary(
            static item => item.Field,
            StringComparer.OrdinalIgnoreCase);
        var rows = new[]
        {
            IdentifierRow(subject, candidate),
            EvidenceRow(
                "Family name",
                FamilyName(subject),
                FamilyName(candidate),
                EvidenceSimilarity(evidence, "family")),
            EvidenceRow(
                "Given names",
                GivenNames(subject),
                GivenNames(candidate),
                EvidenceSimilarity(evidence, "given")),
            EvidenceRow(
                "Date of birth",
                PortalDisplay.BirthDate(subject.BirthDate),
                PortalDisplay.BirthDate(candidate.BirthDate),
                EvidenceSimilarity(evidence, "birthDate")),
            EvidenceRow(
                "Address",
                FirstAddress(subject),
                FirstAddress(candidate),
                EvidenceSimilarity(evidence, "address")),
            EvidenceRow(
                "Telephone or email",
                Telecom(subject),
                Telecom(candidate),
                EvidenceSimilarity(evidence, "telecom")),
            EvidenceRow(
                "Administrative gender",
                PortalDisplay.Gender(subject.Gender),
                PortalDisplay.Gender(candidate.Gender),
                EvidenceSimilarity(evidence, "gender"))
        };

        var conflictCount = rows.Count(static row => row.State == ComparisonState.Conflict);
        var missingCount = rows.Count(static row => row.State == ComparisonState.Missing);
        var agreementCount = rows.Count(static row =>
            row.State is ComparisonState.Agreement or ComparisonState.Close);
        var hasIdentifierConflict =
            rows[0].State == ComparisonState.Conflict ||
            detail.ReviewCase.ApprovalPolicyLocked;
        var recommendation = Recommendation(
            detail.ReviewCase,
            hasIdentifierConflict,
            conflictCount);

        return new ReviewAssessment(
            recommendation.Title,
            recommendation.Description,
            recommendation.Tone,
            recommendation.Action,
            agreementCount,
            conflictCount,
            missingCount,
            rows);
    }

    private static ReviewRecommendation Recommendation(
        ReviewCase review,
        bool hasIdentifierConflict,
        int conflictCount)
    {
        if (hasIdentifierConflict)
        {
            return new ReviewRecommendation(
                "Pause and verify identifiers",
                "An authoritative identifier conflict is present. Do not rely on the aggregate score until the source identifiers have been checked.",
                "danger",
                "Independent verification recommended");
        }

        if (review.Grade == MatchGrade.Certain)
        {
            return new ReviewRecommendation(
                "Strong link signal",
                "A verified authoritative identifier agrees. Confirm the demographic context and proposed survivor before linking.",
                "success",
                "Link is supported");
        }

        if (review.Grade == MatchGrade.Probable && conflictCount == 0)
        {
            return new ReviewRecommendation(
                "Link is supported",
                "The weighted evidence is above the probable threshold with no clear field conflicts. Complete the checks before deciding.",
                "success",
                "Likely same person");
        }

        if (review.Grade == MatchGrade.Probable)
        {
            return new ReviewRecommendation(
                "Review conflicting fields",
                "The aggregate score is probable, but one or more fields disagree. Verify those fields against source records.",
                "warning",
                "Human judgement required");
        }

        return new ReviewRecommendation(
            "Human judgement required",
            "The evidence is within the possible-match band. Compare source provenance and resolve missing or conflicting fields.",
            "warning",
            "No automated recommendation");
    }

    private static ReviewComparisonRow IdentifierRow(
        IdentityProfile subject,
        IdentityProfile candidate)
    {
        var left = PortalDisplay.NhsNumber(subject);
        var right = PortalDisplay.NhsNumber(candidate);
        var leftMissing = left == "Not recorded";
        var rightMissing = right == "Not recorded";
        var state = leftMissing || rightMissing
            ? ComparisonState.Missing
            : string.Equals(left, right, StringComparison.Ordinal)
                ? ComparisonState.Agreement
                : ComparisonState.Conflict;
        return new ReviewComparisonRow(
            "NHS number",
            left,
            right,
            state == ComparisonState.Agreement ? 1 : 0,
            state);
    }

    private static ReviewComparisonRow EvidenceRow(
        string field,
        string subject,
        string candidate,
        double similarity)
    {
        var missing =
            string.Equals(subject, "Not recorded", StringComparison.Ordinal) ||
            string.Equals(candidate, "Not recorded", StringComparison.Ordinal);
        var state = missing
            ? ComparisonState.Missing
            : similarity >= 0.95
                ? ComparisonState.Agreement
                : similarity >= 0.60
                    ? ComparisonState.Close
                    : ComparisonState.Conflict;
        return new ReviewComparisonRow(field, subject, candidate, similarity, state);
    }

    private static double EvidenceSimilarity(
        Dictionary<string, FieldEvidence> evidence,
        string field) =>
        evidence.TryGetValue(field, out var item) ? item.Similarity : 0;

    private static string FamilyName(IdentityProfile profile) =>
        PreferredName(profile)?.Family?.Trim() is { Length: > 0 } family
            ? family
            : "Not recorded";

    private static string GivenNames(IdentityProfile profile)
    {
        var value = PreferredName(profile)?.Given
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .ToArray() ?? [];
        return value.Length == 0 ? "Not recorded" : string.Join(' ', value);
    }

    private static PersonName? PreferredName(IdentityProfile profile)
    {
        foreach (var name in profile.Names)
        {
            if (name.Use == NameUse.Official)
            {
                return name;
            }
        }

        return profile.Names.Count > 0 ? profile.Names[0] : null;
    }

    private static string FirstAddress(IdentityProfile profile) =>
        profile.Addresses.Count > 0
            ? PortalDisplay.Address(profile.Addresses[0])
            : "Not recorded";

    private static string Telecom(IdentityProfile profile)
    {
        var values = profile.Telecoms
            .Select(static item => item.Value)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Take(2)
            .ToArray();
        return values.Length == 0 ? "Not recorded" : string.Join(" · ", values);
    }

    private sealed record ReviewRecommendation(
        string Title,
        string Description,
        string Tone,
        string Action);
}

public enum ComparisonState
{
    Agreement,
    Close,
    Conflict,
    Missing
}

public sealed record ReviewComparisonRow(
    string Field,
    string SubjectValue,
    string CandidateValue,
    double Similarity,
    ComparisonState State);

public sealed record ReviewAssessment(
    string Title,
    string Description,
    string Tone,
    string RecommendedAction,
    int AgreementCount,
    int ConflictCount,
    int MissingCount,
    IReadOnlyList<ReviewComparisonRow> Rows)
{
    public static ReviewAssessment Empty { get; } =
        new(string.Empty, string.Empty, "neutral", string.Empty, 0, 0, 0, []);
}
