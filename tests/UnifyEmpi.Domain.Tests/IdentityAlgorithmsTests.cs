using UnifyEmpi.Application.Configuration;
using UnifyEmpi.Application.Identifiers;
using UnifyEmpi.Application.Matching;
using UnifyEmpi.Application.Normalisation;
using UnifyEmpi.Domain;
using Xunit;

namespace UnifyEmpi.Domain.Tests;

public sealed class IdentityAlgorithmsTests
{
    [Theory]
    [InlineData("943 476 5919", true)]
    [InlineData("9999999999", true)]
    [InlineData("9434765918", false)]
    [InlineData("123", false)]
    [InlineData(null, false)]
    public void NhsNumberValidationUsesModulusEleven(string? value, bool expected) =>
        Assert.Equal(expected, NhsNumberValidator.IsValid(value));

    [Fact]
    public void NormalisationCanonicalisesUnicodePostcodeAndPhone()
    {
        var profile = Profile(
            family: "  Gárcía--Smith ",
            postcode: " sw1a  2aa ",
            phone: "+44 (0)20 7946 0018");

        var normalised = IdentityNormaliser.Normalise(profile);

        Assert.Equal("GARCIA SMITH", normalised.Names[0].Family);
        Assert.Equal("SW1A2AA", normalised.Addresses[0].PostalCode);
        Assert.Equal("+4402079460018", normalised.Telecoms[0].Value);
    }

    [Fact]
    public void MissingFieldsContributeNoEvidence()
    {
        var candidate = Candidate(Profile(family: "Smith"));
        var result = WeightedIdentityMatcher.Match(
            IdentityNormaliser.Normalise(IdentityProfile.Empty),
            candidate,
            MatchingProfile.UkDefault);

        Assert.Equal(MatchGrade.None, result.Grade);
        Assert.Equal(0, result.Score);
        Assert.All(result.Evidence, static item => Assert.Equal(0, item.Similarity));
    }

    [Fact]
    public void ExactVerifiedNhsNumberProducesCertainMatch()
    {
        var profile = Profile(
            nhsNumber: "9434765919",
            family: "Smith",
            birthDate: new DateOnly(1980, 1, 2));
        var result = WeightedIdentityMatcher.Match(
            IdentityNormaliser.Normalise(profile),
            Candidate(profile),
            MatchingProfile.UkDefault);

        Assert.Equal(MatchGrade.Certain, result.Grade);
        Assert.False(result.HasHardConflict);
    }

    [Fact]
    public void ConflictingValidNhsNumbersAreAHardStop()
    {
        var query = Profile(
            nhsNumber: "9434765919",
            family: "Smith",
            birthDate: new DateOnly(1980, 1, 2));
        var candidate = Profile(
            nhsNumber: "9999999999",
            family: "Smith",
            birthDate: new DateOnly(1980, 1, 2));

        var result = WeightedIdentityMatcher.Match(
            IdentityNormaliser.Normalise(query),
            Candidate(candidate),
            MatchingProfile.UkDefault);

        Assert.True(result.HasHardConflict);
        Assert.NotEqual(MatchGrade.Certain, result.Grade);
    }

    [Fact]
    public void BlockingKeysQueryActiveAndPreviousSecretsWithoutLeakingValues()
    {
        var tenant = new TenantId("tenant-a");
        var configuration = new TenantMatchingConfiguration(
            tenant,
            MatchingProfile.UkDefault,
            [
                new BlockingKeySecret("v2", new byte[32].Select((_, index) => (byte)(index + 1)).ToArray(), true),
                new BlockingKeySecret("v1", new byte[32].Select((_, index) => (byte)(index + 2)).ToArray(), false)
            ],
            new Dictionary<SourceSystemId, int>(),
            new HashSet<SourceSystemId>());

        var keys = BlockingKeyGenerator.Generate(
            IdentityNormaliser.Normalise(Profile(
                family: "Smith",
                birthDate: new DateOnly(1980, 1, 2))),
            configuration);

        Assert.Contains(keys, static key => key.Version == "v1");
        Assert.Contains(keys, static key => key.Version == "v2");
        Assert.DoesNotContain(keys, static key => key.Value.Contains("SMITH", StringComparison.Ordinal));
        Assert.All(keys, static key => Assert.Equal(64, key.Value.Length));
    }

    [Fact]
    public void BlockingRulesCanBeEnabledPerMatchingProfile()
    {
        var profile = Profile(
            nhsNumber: "9434765919",
            family: "Smith",
            birthDate: new DateOnly(1980, 1, 2),
            postcode: "LS1 1AA",
            phone: "0113 555 0100") with
        {
            Telecoms =
            [
                new ContactPoint(ContactPointSystem.Phone, "0113 555 0100"),
                new ContactPoint(ContactPointSystem.Email, "alex@example.test")
            ]
        };
        var matchingProfile = MatchingProfile.UkDefault with
        {
            BlockingRules = new HashSet<BlockingRuleKind>
            {
                BlockingRuleKind.Email
            }
        };
        var configuration = new TenantMatchingConfiguration(
            new TenantId("tenant-a"),
            matchingProfile,
            [new BlockingKeySecret("v1", new byte[32], true)],
            new Dictionary<SourceSystemId, int>(),
            new HashSet<SourceSystemId>());

        var keys = BlockingKeyGenerator.Generate(
            IdentityNormaliser.Normalise(profile),
            configuration);

        Assert.Single(keys);
        Assert.Equal("v1", keys[0].Version);
    }

    [Fact]
    public void MatchingProfileFactoryBuildsAndValidatesDeclarativeRules()
    {
        var options = new MatchingRuleOptions
        {
            Weights = new MatchingWeightOptions
            {
                FamilyName = 0.5,
                GivenNames = 0.2,
                BirthDate = 0.3,
                Address = 0,
                Telecom = 0,
                Gender = 0
            },
            BlockingRules =
            [
                nameof(BlockingRuleKind.AuthoritativeIdentifier),
                nameof(BlockingRuleKind.Email)
            ],
            AuthoritativeIdentifierSystems =
            [
                "https://fhir.nhs.uk/Id/nhs-number",
                "https://hospital.example/Id/mrn"
            ],
            MaximumCandidates = 250,
            DefaultResultCount = 5,
            MaximumResultCount = 25
        };

        var profile = MatchingProfileFactory.Create("tenant-v2", 0.6, 0.85, options);

        Assert.Equal("tenant-v2", profile.Version);
        Assert.Equal(0.5, profile.Weights.FamilyName);
        Assert.Equal(250, profile.MaximumCandidates);
        Assert.Equal(5, profile.DefaultResultCount);
        Assert.Equal(25, profile.MaximumResultCount);
        Assert.Contains(BlockingRuleKind.Email, profile.BlockingRules);
        Assert.Contains("https://hospital.example/Id/mrn", profile.AuthoritativeIdentifierSystems);
    }

    [Fact]
    public void MatchingProfileFactoryRejectsUnknownBlockingRules()
    {
        var options = new MatchingRuleOptions
        {
            BlockingRules = ["NotARealRule"]
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => MatchingProfileFactory.Create("bad-profile", 0.62, 0.82, options));

        Assert.Contains("Unknown blocking rule", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ComparatorLibrarySupportsDamerauLevenshteinAndDice()
    {
        Assert.Equal(
            6.0 / 7.0,
            StringSimilarity.NormalisedDamerauLevenshtein("RICHARD", "RICHADR"),
            6);
        Assert.Equal(6.0 / 7.0, StringSimilarity.DiceCoefficient("NIGHT", "NIGH"), 6);
    }

    [Fact]
    public void VersionedNicknameDictionaryAddsExplainableGivenNameEvidence()
    {
        var options = new MatchingRuleOptions
        {
            Comparators = new ComparatorProfileOptions
            {
                Version = "names-v2",
                GivenNames =
                [
                    nameof(StringComparatorKind.JaroWinkler),
                    nameof(StringComparatorKind.Nickname)
                ],
                NicknameDictionaries =
                [
                    new NicknameDictionaryOptions
                    {
                        Version = "en-gb-reviewed-2026",
                        Culture = "en-GB",
                        Entries = new Dictionary<string, List<string>>
                        {
                            ["Robert"] = ["Bob", "Rob"]
                        }
                    }
                ]
            }
        };
        var profile = MatchingProfileFactory.Create("nickname-profile", 0.6, 0.85, options);
        var left = Profile(family: "Smith") with
        {
            Names = [new PersonName("Smith", ["Robert"])]
        };
        var right = Profile(family: "Jones") with
        {
            Names = [new PersonName("Jones", ["Bob"])]
        };

        var result = WeightedIdentityMatcher.Match(
            IdentityNormaliser.Normalise(left),
            Candidate(right),
            profile);
        var given = result.Evidence.Single(static item => item.Field == "given");

        Assert.Equal(0.92, given.Similarity);
        Assert.Equal("nickname", given.Comparator);
        Assert.Equal("en-GB/en-gb-reviewed-2026", given.Detail);
    }

    [Fact]
    public void FellegiSunterScorerProducesProbabilityAndFieldLogLikelihoods()
    {
        var model = new FellegiSunterModel(
            "fs-v1",
            0.01,
            [
                new FellegiSunterFieldModel(
                    "family",
                    [
                        new(FellegiSunterComparisonLevel.Exact, 0.8, 0.05),
                        new(FellegiSunterComparisonLevel.Strong, 0.1, 0.05),
                        new(FellegiSunterComparisonLevel.Partial, 0.05, 0.1),
                        new(FellegiSunterComparisonLevel.Different, 0.05, 0.8)
                    ])
            ]);
        var evidence = new[]
        {
            new FieldEvidence(
                "family",
                1,
                0.25,
                "exact",
                IsMissing: false,
                ComparisonLevel: nameof(FellegiSunterComparisonLevel.Exact))
        };

        var score = FellegiSunterScorer.Score(evidence, model);

        Assert.True(score.Probability > model.PriorMatchProbability);
        Assert.True(score.FieldLogLikelihoodRatios["family"] > 0);
    }

    [Fact]
    public void MatchingProfileFactoryActivatesValidatedFellegiSunterModel()
    {
        var options = new MatchingRuleOptions
        {
            FellegiSunter = new FellegiSunterModelOptions
            {
                Version = "fs-reviewed-v1",
                PriorMatchProbability = 0.01,
                TrainingDatasetDigest = new string('a', 64),
                Fields =
                [
                    Field("family"),
                    Field("given"),
                    Field("birthDate"),
                    Field("address"),
                    Field("telecom"),
                    Field("gender")
                ]
            }
        };
        var profile = MatchingProfileFactory.Create("probability-v1", 0.6, 0.9, options);
        var identity = Profile(
            family: "Smith",
            birthDate: new DateOnly(1980, 1, 2));

        var result = WeightedIdentityMatcher.Match(
            IdentityNormaliser.Normalise(identity),
            Candidate(identity),
            profile);

        Assert.NotNull(profile.ProbabilityModel);
        Assert.Equal("fellegi-sunter", result.ScoreMethod);
        Assert.Contains(
            result.Evidence,
            static evidence =>
                evidence.Field == "family" &&
                evidence.LogLikelihoodRatio > 0);
    }

    [Fact]
    public void MatchingProfileFactoryRejectsAmbiguousNicknameGroups()
    {
        var options = new MatchingRuleOptions
        {
            Comparators = new ComparatorProfileOptions
            {
                GivenNames = [nameof(StringComparatorKind.Nickname)],
                NicknameDictionaries =
                [
                    new NicknameDictionaryOptions
                    {
                        Version = "ambiguous-v1",
                        Culture = "en-GB",
                        Entries = new Dictionary<string, List<string>>
                        {
                            ["William"] = ["Bill"],
                            ["Wilhelmina"] = ["Bill"]
                        }
                    }
                ]
            }
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => MatchingProfileFactory.Create("invalid-nicknames", 0.6, 0.8, options));

        Assert.Contains("multiple groups", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StableResourceIdsAreTenantBoundAndDeterministic()
    {
        var secret = "01234567890123456789012345678901"u8.ToArray();
        var key = new SourceRecordKey(new SourceSystemId("pas"), "12345");

        var first = StableResourceIdGenerator.Create(new TenantId("a"), key, secret);
        var repeated = StableResourceIdGenerator.Create(new TenantId("a"), key, secret);
        var otherTenant = StableResourceIdGenerator.Create(new TenantId("b"), key, secret);

        Assert.Equal(first, repeated);
        Assert.NotEqual(first, otherTenant);
        Assert.DoesNotContain("12345", first, StringComparison.Ordinal);
    }

    [Fact]
    public void SurvivorshipDeduplicatesStructurallyEqualNamesAndAddresses()
    {
        var current = Profile(family: "Smith") with
        {
            Names = [new PersonName("Smith", ["Alex"], NameUse.Official)],
            Addresses =
            [
                new PostalAddress(
                    ["1 High Street"],
                    "Leeds",
                    null,
                    "LS1 1AA",
                    "GB",
                    AddressUse.Home)
            ]
        };
        var incoming = current with
        {
            Names = [new PersonName("Smith", ["Alex"], NameUse.Official)],
            Addresses =
            [
                new PostalAddress(
                    ["1 High Street"],
                    "Leeds",
                    null,
                    "LS1 1AA",
                    "GB",
                    AddressUse.Home)
            ]
        };

        var merged = SurvivorshipService.Merge(current, 50, incoming, 50);

        Assert.Single(merged.Names);
        Assert.Single(merged.Addresses);
    }

    [Fact]
    public void SurvivorshipUsesTrustThenRecencyThenStableId()
    {
        var older = Profile(family: "Older");
        var newer = Profile(family: "Newer");

        var byTrust = SurvivorshipService.Merge(
            older,
            100,
            newer,
            50,
            DateTimeOffset.MinValue,
            DateTimeOffset.MaxValue,
            "z",
            "a");
        var byRecency = SurvivorshipService.Merge(
            older,
            100,
            newer,
            100,
            DateTimeOffset.MinValue,
            DateTimeOffset.MaxValue,
            "z",
            "a");
        var byStableId = SurvivorshipService.Merge(
            older,
            100,
            newer,
            100,
            DateTimeOffset.MinValue,
            DateTimeOffset.MinValue,
            "z",
            "a");

        Assert.Equal("Older", byTrust.Names[0].Family);
        Assert.Equal("Newer", byRecency.Names[0].Family);
        Assert.Equal("Newer", byStableId.Names[0].Family);
    }

    private static CanonicalPatient Candidate(IdentityProfile profile) =>
        new(
            new EnterpriseId(Guid.Parse("018f6f9a-1533-7b1c-8d7b-b85f4383a154")),
            profile,
            [],
            [],
            100,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            1);

    private static FellegiSunterFieldOptions Field(string name) =>
        new()
        {
            Field = name,
            Levels =
            [
                new FellegiSunterLevelOptions
                {
                    Level = FellegiSunterComparisonLevel.Exact,
                    MProbability = 0.7,
                    UProbability = 0.05
                },
                new FellegiSunterLevelOptions
                {
                    Level = FellegiSunterComparisonLevel.Strong,
                    MProbability = 0.15,
                    UProbability = 0.05
                },
                new FellegiSunterLevelOptions
                {
                    Level = FellegiSunterComparisonLevel.Partial,
                    MProbability = 0.1,
                    UProbability = 0.1
                },
                new FellegiSunterLevelOptions
                {
                    Level = FellegiSunterComparisonLevel.Different,
                    MProbability = 0.05,
                    UProbability = 0.8
                }
            ]
        };

    private static IdentityProfile Profile(
        string? nhsNumber = null,
        string? family = null,
        DateOnly? birthDate = null,
        string? postcode = null,
        string? phone = null) =>
        new(
            nhsNumber is null
                ? []
                :
                [
                    new IdentityIdentifier(
                        NhsNumberValidator.IdentifierSystem,
                        nhsNumber,
                        true,
                        true)
                ],
            family is null ? [] : [new PersonName(family, ["Alex"])],
            birthDate,
            AdministrativeGender.Unknown,
            postcode is null
                ? []
                : [new PostalAddress(["1 High Street"], null, null, postcode, "GB")],
            phone is null ? [] : [new ContactPoint(ContactPointSystem.Phone, phone)]);
}
