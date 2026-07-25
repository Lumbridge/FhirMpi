using BenchmarkDotNet.Attributes;
using FhirMpi.Application.Matching;
using FhirMpi.Application.Normalisation;
using FhirMpi.Domain;

namespace FhirMpi.Benchmarks;

[MemoryDiagnoser]
public class MatchingBenchmarks
{
    private readonly NormalisedIdentity _query;
    private readonly PreparedIdentityCandidate[] _candidates;

    public MatchingBenchmarks()
    {
        var profile = CreateProfile("Smith", "Alex", new DateOnly(1980, 1, 2));
        _query = IdentityNormaliser.Normalise(profile);
        _candidates = Enumerable.Range(0, 500)
            .Select(index => new CanonicalPatient(
                EnterpriseId.New(),
                CreateProfile(
                    index % 10 == 0 ? "Smith" : $"Family{index}",
                    "Alex",
                    new DateOnly(1980, 1, 2)),
                [],
                [],
                100,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                1))
            .Select(WeightedIdentityMatcher.Prepare)
            .ToArray();
    }

    [Benchmark]
    public MatchGrade ScoreFiveHundredNormalisedCandidates()
    {
        var best = MatchGrade.None;
        foreach (var candidate in _candidates)
        {
            var match = WeightedIdentityMatcher.Match(
                _query,
                candidate,
                MatchingProfile.UkDefault);
            if (match.Grade > best)
            {
                best = match.Grade;
            }
        }

        return best;
    }

    private static IdentityProfile CreateProfile(
        string family,
        string given,
        DateOnly birthDate) =>
        new(
            [],
            [new PersonName(family, [given])],
            birthDate,
            AdministrativeGender.Female,
            [new PostalAddress(["1 High Street"], "London", null, "SW1A 2AA", "GB")],
            [new ContactPoint(ContactPointSystem.Phone, "+442079460018")]);
}
