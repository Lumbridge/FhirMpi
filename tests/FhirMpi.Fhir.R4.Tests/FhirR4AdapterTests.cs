using FhirMpi.Application.Normalisation;
using FhirMpi.Domain;
using Hl7.Fhir.Model;
using Xunit;

namespace FhirMpi.Fhir.R4.Tests;

public sealed class FhirR4AdapterTests
{
    [Theory]
    [InlineData(FhirWireFormat.Json)]
    [InlineData(FhirWireFormat.Xml)]
    public void PatientRoundTripsInBothWireFormats(FhirWireFormat format)
    {
        var codec = new FhirResourceCodec();
        var patient = new Patient
        {
            Id = "source-1",
            BirthDate = "1980-01-02",
            Gender = Hl7.Fhir.Model.AdministrativeGender.Female,
            Name = [new HumanName { Family = "Smith", Given = ["Alex"] }],
            Identifier =
            [
                new Identifier(NhsNumberValidator.IdentifierSystem, "9434765919")
            ]
        };

        var roundTrip = codec.Parse<Patient>(codec.Serialise(patient, format), format);

        Assert.Equal("source-1", roundTrip.Id);
        Assert.Equal("Smith", roundTrip.Name[0].Family);
        Assert.Equal("1980-01-02", roundTrip.BirthDate);
    }

    [Fact]
    public void MatchBundleContainsScoreAndGradeOnEveryEntry()
    {
        var enterpriseId = new EnterpriseId(
            Guid.Parse("018f6f9a-1533-7b1c-8d7b-b85f4383a154"));
        var patient = new CanonicalPatient(
            enterpriseId,
            IdentityProfile.Empty,
            [],
            [],
            0,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            1);
        var response = new MatchResponse(
            [new MatchResult(patient, 0.9, MatchGrade.Probable, [])],
            1,
            "v1");

        var bundle = FhirR4Mapper.ToMatchBundle(response, new TenantId("tenant-a"));

        Assert.Equal(Bundle.BundleType.Searchset, bundle.Type);
        Assert.Equal(0.9m, bundle.Entry[0].Search!.Score);
        var resultPatient = Assert.IsType<Patient>(bundle.Entry[0].Resource);
        Assert.Contains(
            resultPatient.Extension,
            extension =>
                extension.Url == FhirR4Constants.MatchGradeExtension &&
                Assert.IsType<Code>(extension.Value).Value == "probable");
    }

    [Fact]
    public async System.Threading.Tasks.Task PartialMatchValidationRequiresIdentifyingInput()
    {
        var validator = new UkCorePatientValidator();

        var invalid = await validator.ValidateMatchInputAsync(
            new Patient(),
            CancellationToken.None);
        var valid = await validator.ValidateMatchInputAsync(
            new Patient { Name = [new HumanName { Family = "Smith" }] },
            CancellationToken.None);

        Assert.False(invalid.IsSuccessful);
        Assert.True(valid.IsSuccessful);
    }

    [Fact]
    public void TenantSecurityAssertionRejectsDirectIdCrossTenantRead()
    {
        var resource = new Patient
        {
            Meta = FhirR4Mapper.CreateMeta(
                new TenantId("tenant-a"),
                1,
                DateTimeOffset.UnixEpoch)
        };

        Assert.Throws<InvalidOperationException>(() =>
            FhirR4Mapper.AssertTenant(resource, new TenantId("tenant-b")));
    }

    [Fact]
    public void UntrustedWireResourcesCannotAssertIdentifierAuthority()
    {
        var identifier = new Identifier(
            NhsNumberValidator.IdentifierSystem,
            "9434765919");
        identifier.Extension.Add(new Extension(
            FhirR4Constants.IdentifierVerifiedExtension,
            new FhirBoolean(true)));
        identifier.Extension.Add(new Extension(
            FhirR4Constants.IdentifierAuthoritativeExtension,
            new FhirBoolean(true)));
        var patient = new Patient { Identifier = [identifier] };

        var untrusted = FhirR4Mapper.ToDomain(patient).Identifiers[0];
        var trusted = FhirR4Mapper.ToTrustedDomain(patient).Identifiers[0];

        Assert.False(untrusted.IsVerified);
        Assert.False(untrusted.IsAuthoritative);
        Assert.True(trusted.IsVerified);
        Assert.True(trusted.IsAuthoritative);
    }
}
