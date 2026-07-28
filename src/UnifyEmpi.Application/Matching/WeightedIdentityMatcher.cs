using UnifyEmpi.Application.Normalisation;
using UnifyEmpi.Domain;

namespace UnifyEmpi.Application.Matching;

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

        evidence.Add(ToEvidence(
            "family",
            profile.Weights.FamilyName,
            CompareFamilyNames(query.Names, normalisedCandidate.Names, profile.Comparators)));
        evidence.Add(ToEvidence(
            "given",
            profile.Weights.GivenNames,
            CompareGivenNames(query.Names, normalisedCandidate.Names, profile.Comparators)));
        evidence.Add(ToEvidence(
            "birthDate",
            profile.Weights.BirthDate,
            CompareBirthDates(query.BirthDate, normalisedCandidate.BirthDate)));
        evidence.Add(ToEvidence(
            "address",
            profile.Weights.Address,
            CompareAddresses(query.Addresses, normalisedCandidate.Addresses)));
        evidence.Add(ToEvidence(
            "telecom",
            profile.Weights.Telecom,
            CompareTelecoms(query.Telecoms, normalisedCandidate.Telecoms)));
        evidence.Add(ToEvidence(
            "gender",
            profile.Weights.Gender,
            CompareGender(query.Gender, normalisedCandidate.Gender)));

        var scoreMethod = profile.ProbabilityModel is null
            ? "weighted-similarity"
            : "fellegi-sunter";
        double score;
        if (profile.ProbabilityModel is null)
        {
            var weightedScore = evidence.Sum(static item => item.Contribution) / profile.Weights.Total;
            score = Math.Clamp(weightedScore, 0, 1);
        }
        else
        {
            var probability = FellegiSunterScorer.Score(evidence, profile.ProbabilityModel);
            score = probability.Probability;
            evidence = evidence
                .Select(item => item with
                {
                    LogLikelihoodRatio =
                        probability.FieldLogLikelihoodRatios.GetValueOrDefault(item.Field)
                })
                .ToList();
        }

        var grade = hardConflict
            ? GradeFromScore(score, profile)
            : identifierAssessment.HasCertainIdentifier
                ? MatchGrade.Certain
                : GradeFromScore(score, profile);

        return new MatchResult(
            candidate.Patient,
            score,
            grade,
            evidence,
            hardConflict,
            scoreMethod);
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

    private static FieldEvidence ToEvidence(
        string field,
        double weight,
        FieldComparison comparison) =>
        new(
            field,
            comparison.Similarity,
            weight,
            comparison.Comparator,
            comparison.Detail,
            comparison.IsMissing,
            FellegiSunterScorer.Classify(comparison.Similarity, comparison.IsMissing).ToString());

    private static FieldComparison CompareFamilyNames(
        IReadOnlyList<NormalisedName> query,
        IReadOnlyList<NormalisedName> candidate,
        ComparatorProfile profile)
    {
        var best = new StringComparisonResult(0, "none", null);
        var observed = false;
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

                observed = true;
                var comparison = StringComparatorLibrary.Compare(
                    left.Family,
                    right.Family,
                    profile.FamilyNameComparators,
                    profile);
                if (comparison.Similarity > best.Similarity)
                {
                    best = comparison;
                }
            }
        }

        return new FieldComparison(
            best.Similarity,
            best.Comparator,
            best.Detail,
            !observed);
    }

    private static FieldComparison CompareGivenNames(
        IReadOnlyList<NormalisedName> query,
        IReadOnlyList<NormalisedName> candidate,
        ComparatorProfile profile)
    {
        var queryNames = query
            .SelectMany(static name => name.Given)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var candidateNames = candidate
            .SelectMany(static name => name.Given)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (queryNames.Length == 0 || candidateNames.Length == 0)
        {
            return FieldComparison.Missing("configured-string-library");
        }

        var best = new StringComparisonResult(0, "none", null);
        foreach (var left in queryNames)
        {
            foreach (var right in candidateNames)
            {
                var comparison = StringComparatorLibrary.Compare(
                    left,
                    right,
                    profile.GivenNameComparators,
                    profile);
                if (comparison.Similarity > best.Similarity)
                {
                    best = comparison;
                }
            }
        }

        return new FieldComparison(best.Similarity, best.Comparator, best.Detail, false);
    }

    private static FieldComparison CompareBirthDates(DateOnly? query, DateOnly? candidate)
    {
        if (!query.HasValue || !candidate.HasValue)
        {
            return FieldComparison.Missing("exact/day-month-transposition");
        }

        if (query.Value == candidate.Value)
        {
            return new FieldComparison(1, "exact", null, false);
        }

        var transposed = query.Value.Year == candidate.Value.Year &&
                         query.Value.Day == candidate.Value.Month &&
                         query.Value.Month == candidate.Value.Day;
        return new FieldComparison(
            transposed ? 0.5 : 0,
            transposed ? "day-month-transposition" : "exact",
            null,
            false);
    }

    private static FieldComparison CompareAddresses(
        IReadOnlyList<NormalisedAddress> query,
        IReadOnlyList<NormalisedAddress> candidate)
    {
        if (query.Count == 0 || candidate.Count == 0)
        {
            return FieldComparison.Missing("postcode/token-jaccard");
        }

        var best = 0.0;
        var comparator = "token-jaccard";
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
                var pairBest = Math.Max(postcodeScore, addressScore);
                if (pairBest > best)
                {
                    best = pairBest;
                    comparator = postcodeScore >= addressScore
                        ? postcodeScore == 1 ? "postcode-exact" : "postcode-sector"
                        : "token-jaccard";
                }
            }
        }

        return new FieldComparison(best, comparator, null, false);
    }

    private static FieldComparison CompareTelecoms(
        IReadOnlyList<NormalisedTelecom> query,
        IReadOnlyList<NormalisedTelecom> candidate)
    {
        if (query.Count == 0 || candidate.Count == 0)
        {
            return FieldComparison.Missing("normalised-exact");
        }

        var matches = query.Any(left => candidate.Any(right =>
            left.System == right.System &&
            string.Equals(left.Value, right.Value, StringComparison.Ordinal)));
        return new FieldComparison(matches ? 1 : 0, "normalised-exact", null, false);
    }

    private static FieldComparison CompareGender(
        AdministrativeGender query,
        AdministrativeGender candidate) =>
        query == AdministrativeGender.Unknown || candidate == AdministrativeGender.Unknown
            ? FieldComparison.Missing("exact")
            : new FieldComparison(query == candidate ? 1 : 0, "exact", null, false);

    private readonly record struct IdentifierAssessment(
        bool HasCertainIdentifier,
        bool HasAuthoritativeConflict);

    private readonly record struct FieldComparison(
        double Similarity,
        string Comparator,
        string? Detail,
        bool IsMissing)
    {
        public static FieldComparison Missing(string comparator) =>
            new(0, comparator, null, true);
    }
}

public sealed record PreparedIdentityCandidate(
    CanonicalPatient Patient,
    NormalisedIdentity Normalised);
