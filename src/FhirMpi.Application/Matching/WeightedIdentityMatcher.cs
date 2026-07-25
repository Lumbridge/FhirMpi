using FhirMpi.Application.Normalisation;
using FhirMpi.Domain;

namespace FhirMpi.Application.Matching;

public sealed class WeightedIdentityMatcher
{
    public static PreparedIdentityCandidate Prepare(CanonicalPatient candidate) =>
        new(candidate, IdentityNormaliser.Normalise(candidate.Profile));

    public static MatchResult Match(
        NormalisedIdentity query,
        CanonicalPatient candidate,
        MatchingProfile profile) =>
        Match(query, Prepare(candidate), profile);

    public static MatchResult Match(
        NormalisedIdentity query,
        PreparedIdentityCandidate candidate,
        MatchingProfile profile)
    {
        var normalisedCandidate = candidate.Normalised;
        var evidence = new List<FieldEvidence>(6);

        var identifierAssessment = AssessIdentifiers(query, normalisedCandidate, profile);
        var birthDateConflict =
            query.BirthDate.HasValue &&
            normalisedCandidate.BirthDate.HasValue &&
            query.BirthDate.Value != normalisedCandidate.BirthDate.Value;
        var hardConflict = identifierAssessment.HasAuthoritativeConflict ||
                           (identifierAssessment.HasCertainIdentifier && birthDateConflict);

        evidence.Add(new FieldEvidence(
            "family",
            CompareFamilyNames(query.Names, normalisedCandidate.Names),
            profile.Weights.FamilyName,
            "jaro-winkler/phonetic"));
        evidence.Add(new FieldEvidence(
            "given",
            CompareGivenNames(query.Names, normalisedCandidate.Names),
            profile.Weights.GivenNames,
            "jaro-winkler"));
        evidence.Add(new FieldEvidence(
            "birthDate",
            CompareBirthDates(query.BirthDate, normalisedCandidate.BirthDate),
            profile.Weights.BirthDate,
            "exact/day-month-transposition"));
        evidence.Add(new FieldEvidence(
            "address",
            CompareAddresses(query.Addresses, normalisedCandidate.Addresses),
            profile.Weights.Address,
            "postcode/token-jaccard"));
        evidence.Add(new FieldEvidence(
            "telecom",
            CompareTelecoms(query.Telecoms, normalisedCandidate.Telecoms),
            profile.Weights.Telecom,
            "normalised-exact"));
        evidence.Add(new FieldEvidence(
            "gender",
            CompareGender(query.Gender, normalisedCandidate.Gender),
            profile.Weights.Gender,
            "exact"));

        var weightedScore = evidence.Sum(static item => item.Contribution) / profile.Weights.Total;
        var score = Math.Clamp(weightedScore, 0, 1);
        var grade = hardConflict
            ? GradeFromScore(score, profile)
            : identifierAssessment.HasCertainIdentifier
                ? MatchGrade.Certain
                : GradeFromScore(score, profile);

        return new MatchResult(candidate.Patient, score, grade, evidence, hardConflict);
    }

    private static MatchGrade GradeFromScore(double score, MatchingProfile profile) =>
        score >= profile.ProbableThreshold
            ? MatchGrade.Probable
            : score >= profile.PossibleThreshold
                ? MatchGrade.Possible
                : MatchGrade.None;

    private static IdentifierAssessment AssessIdentifiers(
        NormalisedIdentity query,
        NormalisedIdentity candidate,
        MatchingProfile profile)
    {
        var hasCertain = false;
        var hasConflict = false;

        foreach (var system in profile.AuthoritativeIdentifierSystems)
        {
            var queryValues = query.Identifiers
                .Where(identifier =>
                    string.Equals(identifier.System, system, StringComparison.Ordinal) &&
                    identifier.IsVerified &&
                    identifier.IsAuthoritative &&
                    IsIdentifierValid(identifier))
                .Select(static identifier => identifier.Value)
                .ToHashSet(StringComparer.Ordinal);
            var candidateValues = candidate.Identifiers
                .Where(identifier =>
                    string.Equals(identifier.System, system, StringComparison.Ordinal) &&
                    identifier.IsVerified &&
                    identifier.IsAuthoritative &&
                    IsIdentifierValid(identifier))
                .Select(static identifier => identifier.Value)
                .ToHashSet(StringComparer.Ordinal);

            if (queryValues.Count == 0 || candidateValues.Count == 0)
            {
                continue;
            }

            if (queryValues.Overlaps(candidateValues))
            {
                hasCertain = true;
            }
            else
            {
                hasConflict = true;
            }
        }

        return new IdentifierAssessment(hasCertain, hasConflict);
    }

    private static bool IsIdentifierValid(IdentityIdentifier identifier) =>
        !string.Equals(identifier.System, NhsNumberValidator.IdentifierSystem, StringComparison.Ordinal) ||
        NhsNumberValidator.IsValid(identifier.Value);

    private static double CompareFamilyNames(
        IReadOnlyList<NormalisedName> query,
        IReadOnlyList<NormalisedName> candidate)
    {
        var best = 0.0;
        foreach (var left in query)
        {
            if (left.Family.Length == 0)
            {
                continue;
            }

            foreach (var right in candidate)
            {
                if (right.Family.Length == 0)
                {
                    continue;
                }

                var similarity = StringSimilarity.JaroWinkler(left.Family, right.Family);
                if (left.FamilyPhonetic.Length > 0 &&
                    string.Equals(left.FamilyPhonetic, right.FamilyPhonetic, StringComparison.Ordinal))
                {
                    similarity = Math.Max(similarity, 0.85);
                }

                best = Math.Max(best, similarity);
            }
        }

        return best;
    }

    private static double CompareGivenNames(
        IReadOnlyList<NormalisedName> query,
        IReadOnlyList<NormalisedName> candidate)
    {
        var queryNames = query.SelectMany(static name => name.Given).Distinct(StringComparer.Ordinal);
        var candidateNames = candidate.SelectMany(static name => name.Given).Distinct(StringComparer.Ordinal).ToArray();
        var best = 0.0;
        foreach (var left in queryNames)
        {
            foreach (var right in candidateNames)
            {
                best = Math.Max(best, StringSimilarity.JaroWinkler(left, right));
            }
        }

        return best;
    }

    private static double CompareBirthDates(DateOnly? query, DateOnly? candidate)
    {
        if (!query.HasValue || !candidate.HasValue)
        {
            return 0;
        }

        if (query.Value == candidate.Value)
        {
            return 1;
        }

        return query.Value.Year == candidate.Value.Year &&
               query.Value.Day == candidate.Value.Month &&
               query.Value.Month == candidate.Value.Day
            ? 0.5
            : 0;
    }

    private static double CompareAddresses(
        IReadOnlyList<NormalisedAddress> query,
        IReadOnlyList<NormalisedAddress> candidate)
    {
        var best = 0.0;
        foreach (var left in query)
        {
            foreach (var right in candidate)
            {
                var postcodeScore =
                    left.PostalCode.Length > 0 &&
                    string.Equals(left.PostalCode, right.PostalCode, StringComparison.Ordinal)
                        ? 1
                        : left.PostalSector.Length > 0 &&
                          string.Equals(left.PostalSector, right.PostalSector, StringComparison.Ordinal)
                            ? 0.6
                            : 0;
                var addressScore = StringSimilarity.TokenJaccard(left.AddressTokens, right.AddressTokens);
                best = Math.Max(best, Math.Max(postcodeScore, addressScore));
            }
        }

        return best;
    }

    private static double CompareTelecoms(
        IReadOnlyList<NormalisedTelecom> query,
        IReadOnlyList<NormalisedTelecom> candidate) =>
        query.Any(left => candidate.Any(right =>
            left.System == right.System &&
            string.Equals(left.Value, right.Value, StringComparison.Ordinal)))
            ? 1
            : 0;

    private static double CompareGender(AdministrativeGender query, AdministrativeGender candidate) =>
        query != AdministrativeGender.Unknown &&
        candidate != AdministrativeGender.Unknown &&
        query == candidate
            ? 1
            : 0;

    private readonly record struct IdentifierAssessment(
        bool HasCertainIdentifier,
        bool HasAuthoritativeConflict);
}

public sealed record PreparedIdentityCandidate(
    CanonicalPatient Patient,
    NormalisedIdentity Normalised);
