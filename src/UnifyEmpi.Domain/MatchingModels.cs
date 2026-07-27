namespace UnifyEmpi.Domain;

public readonly record struct BlockingKey(string Version, string Value)
{
    public override string ToString() => $"{Version}:{Value}";
}

public enum MatchGrade
{
    None,
    Possible,
    Probable,
    Certain
}

public sealed record FieldEvidence(
    string Field,
    double Similarity,
    double Weight,
    string Comparator,
    string? Detail = null)
{
    public double Contribution => Similarity * Weight;
}

public sealed record MatchResult(
    CanonicalPatient Patient,
    double Score,
    MatchGrade Grade,
    IReadOnlyList<FieldEvidence> Evidence,
    bool HasHardConflict = false);

public sealed record MatchResponse(
    IReadOnlyList<MatchResult> Matches,
    int CandidateCount,
    string MatchingProfileVersion);

public sealed record MatchRequest(
    IdentityProfile Profile,
    bool OnlyCertainMatches = false,
    int Count = 10);

public sealed record MatchingWeights(
    double FamilyName,
    double GivenNames,
    double BirthDate,
    double Address,
    double Telecom,
    double Gender)
{
    public static MatchingWeights UkDefault { get; } = new(0.25, 0.20, 0.30, 0.15, 0.07, 0.03);

    public double Total => FamilyName + GivenNames + BirthDate + Address + Telecom + Gender;
}

public sealed record MatchingProfile(
    string Version,
    MatchingWeights Weights,
    double PossibleThreshold,
    double ProbableThreshold,
    int MaximumCandidates,
    int DefaultResultCount,
    int MaximumResultCount,
    IReadOnlySet<string> AuthoritativeIdentifierSystems)
{
    public static MatchingProfile UkDefault { get; } = new(
        "uk-default-v1",
        MatchingWeights.UkDefault,
        0.62,
        0.82,
        500,
        10,
        50,
        new HashSet<string>(StringComparer.Ordinal)
        {
            "https://fhir.nhs.uk/Id/nhs-number"
        });
}
