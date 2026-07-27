using Microsoft.Extensions.Options;
using UnifyEmpi.Application;
using UnifyEmpi.Domain;
using UnifyEmpi.Storage.Abstractions;

namespace UnifyEmpi.Portal;

public sealed partial class DevelopmentDataSeeder(
    RegistryService registry,
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
        var existing = await registry.SearchCanonicalPatientsAsync(
            reader,
            new CanonicalPatientSearch(Count: 1),
            cancellationToken);
        if (existing.Items.Count > 0)
        {
            return;
        }

        foreach (var seed in CreatePatients())
        {
            var actor = new ActorContext(
                tenant,
                $"seed-{seed.SourceSystem}",
                new SourceSystemId(seed.SourceSystem),
                SourceScopes,
                Guid.CreateVersion7().ToString("N"));
            await registry.UpsertPatientAsync(
                actor,
                new UpsertPatientCommand(
                    new SourceRecordKey(
                        new SourceSystemId(seed.SourceSystem),
                        seed.LocalId),
                    seed.Profile),
                cancellationToken);
        }

        LogSeeded(logger, tenant.Value, null);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Task.CompletedTask;
    }

    private static IReadOnlyList<SeedPatient> CreatePatients() =>
    [
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
    ];

    private static IdentityProfile Profile(
        string? nhsNumber,
        string family,
        string given,
        DateOnly birthDate,
        AdministrativeGender gender,
        string address,
        string city,
        string postcode,
        string phone) =>
        new(
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
            [new ContactPoint(ContactPointSystem.Phone, phone, "mobile")]);

    [LoggerMessage(
        EventId = 6200,
        Level = LogLevel.Information,
        Message = "Synthetic portal data was seeded for tenant {TenantId}.")]
    private static partial void LogSeeded(
        ILogger logger,
        string tenantId,
        Exception? exception);

    private sealed record SeedPatient(
        string SourceSystem,
        string LocalId,
        IdentityProfile Profile);
}
