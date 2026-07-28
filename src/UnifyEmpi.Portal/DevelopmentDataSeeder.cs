using Microsoft.Extensions.Options;
using UnifyEmpi.Application;
using UnifyEmpi.Application.Normalisation;
using UnifyEmpi.Domain;
using UnifyEmpi.Storage.Abstractions;

namespace UnifyEmpi.Portal;

public sealed partial class DevelopmentDataSeeder(
    RegistryService registry,
    IIdentityRegistryStore store,
    IOptions<PortalOptions> options,
    IOptions<PortalAuthenticationOptions> authenticationOptions,
    ILogger<DevelopmentDataSeeder> logger)
    : IHostedService
{
    private static readonly IReadOnlySet<string> SourceScopes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "system/Patient.*",
            "mpi.review",
            "mpi.admin"
        };

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.SeedSyntheticData)
        {
            return;
        }

        var tenant = new TenantId(authenticationOptions.Value.DevelopmentTenant);
        var reader = new ActorContext(
            tenant,
            "development-seeder",
            null,
            new HashSet<string>(StringComparer.Ordinal)
            {
                "mpi.review",
                "mpi.admin"
            },
            Guid.CreateVersion7().ToString("N"));
        var seededCount = 0;
        foreach (var seed in CreatePatients())
        {
            var sourceSystem = new SourceSystemId(seed.SourceSystem);
            var key = new SourceRecordKey(sourceSystem, seed.LocalId);
            var existing = await store.GetSourcePatientAsync(
                reader,
                key,
                cancellationToken);
            if (existing is not null)
            {
                continue;
            }

            var actor = new ActorContext(
                tenant,
                $"seed-{seed.SourceSystem}",
                sourceSystem,
                SourceScopes,
                Guid.CreateVersion7().ToString("N"));
            await registry.UpsertPatientAsync(
                actor,
                new UpsertPatientCommand(
                    key,
                    seed.Profile,
                    ExpectedVersion: 0),
                cancellationToken);
            seededCount++;
        }

        if (seededCount > 0)
        {
            LogSeeded(logger, seededCount, tenant.Value, null);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Task.CompletedTask;
    }

    private static List<SeedPatient> CreatePatients()
    {
        var patients = new List<SeedPatient>
        {
            new(
                "wds",
                "100184",
                Profile(
                    "9434765919",
                    "Carter",
                    "Alice",
                    new DateOnly(1984, 3, 12),
                    AdministrativeGender.Female,
                    "14 Willow Lane",
                    "Cardiff",
                    "CF10 1AA",
                    "07700 900181")),
            new(
                "cardiff-and-vale",
                "CAV-7712",
                Profile(
                    null,
                    "Carter",
                    "Alicia",
                    new DateOnly(1984, 3, 12),
                    AdministrativeGender.Female,
                    "14 Willow Lane",
                    "Cardiff",
                    "CF10 1AA",
                    "07700 900181")),
            new(
                "wds",
                "100263",
                Profile(
                    "9999999999",
                    "Hughes",
                    "Robert",
                    new DateOnly(1976, 11, 4),
                    AdministrativeGender.Male,
                    "8 Station Road",
                    "Newport",
                    "NP20 1AA",
                    "07700 900263")),
            new(
                "aneurin-bevan",
                "AB-5528",
                Profile(
                    null,
                    "Hughes",
                    "Rob",
                    new DateOnly(1976, 11, 4),
                    AdministrativeGender.Male,
                    "8 Station Road",
                    "Newport",
                    "NP20 1AA",
                    "07700 900263")),
            new(
                "wds",
                "100347",
                Profile(
                    "4857773456",
                    "Khan",
                    "Samira",
                    new DateOnly(1991, 7, 28),
                    AdministrativeGender.Female,
                    "22 Orchard Close",
                    "Swansea",
                    "SA1 1AA",
                    "07700 900347")),
            new(
                "velindre",
                "VEL-9041",
                Profile(
                    null,
                    "Khan",
                    "Sameera",
                    new DateOnly(1991, 7, 28),
                    AdministrativeGender.Female,
                    "22 Orchard Close",
                    "Swansea",
                    "SA1 1AA",
                    "07700 900347"))
        };
        patients.AddRange(
            SyntheticAssuranceDataset.Identities.SelectMany(static identity =>
            new SeedPatient[]
            {
                new SeedPatient(
                    identity.Primary.SourceSystem.Value,
                    identity.Primary.LocalId,
                    Profile(
                        null,
                        identity.PrimaryFamily,
                        identity.PrimaryGiven,
                        identity.BirthDate,
                        identity.Gender,
                        identity.PrimaryAddress,
                        identity.City,
                        identity.Postcode,
                        identity.PrimaryPhone)),
                new SeedPatient(
                    identity.Partner.SourceSystem.Value,
                    identity.Partner.LocalId,
                    Profile(
                        null,
                        identity.PartnerFamily,
                        identity.PartnerGiven,
                        identity.BirthDate,
                        identity.Gender,
                        identity.PartnerAddress,
                        identity.City,
                        identity.Postcode,
                        identity.PartnerPhone))
            }));
        return patients;
    }

    private static IdentityProfile Profile(
        string? nhsNumber,
        string family,
        string given,
        DateOnly birthDate,
        AdministrativeGender gender,
        string address,
        string city,
        string postcode,
        string? phone) =>
        new IdentityProfile(
            nhsNumber is null
                ? []
                :
                [
                    new IdentityIdentifier(
                        "https://fhir.nhs.uk/Id/nhs-number",
                        nhsNumber)
                ],
            [new PersonName(family, [given], NameUse.Official)],
            birthDate,
            gender,
            [new PostalAddress([address], city, null, postcode, "GB", AddressUse.Home)],
            phone is null
                ? []
                : [new ContactPoint(ContactPointSystem.Phone, phone, "mobile")])
        {
            Tags = NhsNumberValidator.IsValid(nhsNumber)
                ?
                [
                    new IdentityTag(null, NhsNumberValidator.TracedTag),
                    new IdentityTag(null, NhsNumberValidator.GoldTag)
                ]
                : []
        };

    [LoggerMessage(
        EventId = 6200,
        Level = LogLevel.Information,
        Message = "{PatientCount} synthetic portal records were seeded for tenant {TenantId}.")]
    private static partial void LogSeeded(
        ILogger logger,
        int patientCount,
        string tenantId,
        Exception? exception);

    private sealed record SeedPatient(
        string SourceSystem,
        string LocalId,
        IdentityProfile Profile);
}
