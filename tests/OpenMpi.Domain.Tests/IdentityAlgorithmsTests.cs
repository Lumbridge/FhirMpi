using OpenMpi.Application.Identifiers;
using OpenMpi.Application.Matching;
using OpenMpi.Application.Normalisation;
using OpenMpi.Domain;
using Xunit;

namespace OpenMpi.Domain.Tests;

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
