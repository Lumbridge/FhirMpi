using UnifyEmpi.Application;
using UnifyEmpi.Application.Configuration;
using UnifyEmpi.Domain;
using UnifyEmpi.Storage.Abstractions;
using UnifyEmpi.Storage.InMemory;
using Xunit;

namespace UnifyEmpi.Domain.Tests;

public sealed class RegistryMaintenanceTests
{
    [Fact]
    public async Task ReindexAddsRotatedKeyAndCompletes()
    {
        var fixture = CreateFixture(
            [Secret("v1", 1, true)]);
        var created = await fixture.UpsertAsync("pas", "P-1", Profile("Smith"));
        var target = fixture.WithConfiguration(
            [Secret("v1", 1, false), Secret("v2", 2, true)]);
        var job = await target.Maintenance.StartReindexAsync(
            target.Admin,
            new StartReindexCommand("Rotate the tenant blocking HMAC key.", 1),
            CancellationToken.None);

        var completed = await ProcessToTerminalAsync(target, job.Id);
        var patient = await target.Store.GetCanonicalPatientAsync(
            target.Admin,
            created.CanonicalPatient.EnterpriseId,
            CancellationToken.None);

        Assert.Equal(RegistryMaintenanceJobStatus.Completed, completed.Status);
        Assert.True(completed.Validated >= 1);
        Assert.Contains(patient!.BlockingKeys, static key => key.Version == "v1");
        Assert.Contains(patient.BlockingKeys, static key => key.Version == "v2");
    }

    [Fact]
    public async Task ReindexRejectsAKeyChangeWithoutAnOverlapStage()
    {
        var fixture = CreateFixture(
            [Secret("v1", 1, true)]);
        await fixture.UpsertAsync("pas", "P-1", Profile("Smith"));
        var unsafeTarget = fixture.WithConfiguration(
            [Secret("v2", 2, true)]);
        var job = await unsafeTarget.Maintenance.StartReindexAsync(
            unsafeTarget.Admin,
            new StartReindexCommand("Attempt an unsafe direct key replacement.", 1),
            CancellationToken.None);

        var failed = await ProcessToTerminalAsync(unsafeTarget, job.Id);

        Assert.Equal(RegistryMaintenanceJobStatus.Failed, failed.Status);
        Assert.Contains("do not overlap", failed.LastError, StringComparison.Ordinal);
        Assert.Contains("retaining the previous HMAC key", failed.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PopulationReconciliationCreatesGovernedDuplicateReview()
    {
        var fixture = CreateFixture(
            [Secret("v1", 1, true)]);
        await fixture.UpsertAsync("pas", "P-1", Profile("Smith"));
        await fixture.UpsertAsync("community", "C-2", Profile("Smith"));
        var job = await fixture.Maintenance.StartPopulationReconciliationAsync(
            fixture.Admin,
            new StartPopulationReconciliationCommand(
                "Run the scheduled whole-population assurance pass.",
                1),
            CancellationToken.None);

        var completed = await ProcessToTerminalAsync(fixture, job.Id);
        var reviews = await fixture.Store.SearchReviewCasesAsync(
            fixture.Admin,
            new ReviewCaseSearch(Status: ReviewCaseStatus.Pending, Count: 100),
            CancellationToken.None);

        Assert.Equal(RegistryMaintenanceJobStatus.Completed, completed.Status);
        Assert.Contains(
            reviews.Items,
            static review => review.Kind == ReviewCaseKind.PopulationReconciliation);
        Assert.True(completed.ReviewCasesCreated >= 1);
    }

    [Fact]
    public async Task ExternalFhirReconciliationImportsIdempotentlyBeforePopulationScan()
    {
        var external = new SourceSystemId("external");
        var page = new ExternalPatientPage(
            [
                new ExternalPatientRecord(
                    external,
                    "remote-1",
                    "remote-1",
                    "7",
                    DateTimeOffset.Parse(
                        "2026-07-28T10:00:00Z",
                        System.Globalization.CultureInfo.InvariantCulture),
                    Profile("Jones"),
                    new string('a', 64))
            ],
            null);
        var fixture = CreateFixture(
            [Secret("v1", 1, true)],
            new SinglePageExternalSourceRegistry(
                new FakeExternalSource(new TenantId("tenant-a"), external, page)));
        var job = await fixture.Maintenance.StartPopulationReconciliationAsync(
            fixture.Admin,
            new StartPopulationReconciliationCommand(
                "Synchronise the existing FHIR patient store.",
                1,
                external),
            CancellationToken.None);

        var completed = await ProcessToTerminalAsync(fixture, job.Id);
        var stored = await fixture.Store.GetSourcePatientAsync(
            fixture.Admin,
            new SourceRecordKey(external, "remote-1"),
            CancellationToken.None);

        Assert.Equal(RegistryMaintenanceJobStatus.Completed, completed.Status);
        Assert.Equal(1, completed.Imported);
        Assert.NotNull(stored);
        Assert.Equal("Jones", stored.Profile.Names.Single().Family);
    }

    private static async ValueTask<RegistryMaintenanceJob> ProcessToTerminalAsync(
        Fixture fixture,
        Guid jobId)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var job = await fixture.Maintenance.GetJobAsync(
                fixture.Admin,
                jobId,
                CancellationToken.None) ??
                      throw new InvalidOperationException("The maintenance job disappeared.");
            if (job.Status is RegistryMaintenanceJobStatus.Completed or
                RegistryMaintenanceJobStatus.Failed or
                RegistryMaintenanceJobStatus.Cancelled)
            {
                return job;
            }

            Assert.True(await fixture.Maintenance.ProcessJobBatchAsync(
                fixture.Admin,
                jobId,
                "test-worker",
                TimeSpan.FromMinutes(1),
                CancellationToken.None));
        }

        throw new TimeoutException("The maintenance job did not reach a terminal state.");
    }

    private static Fixture CreateFixture(
        IReadOnlyList<BlockingKeySecret> secrets,
        IExternalPatientSourceRegistry? externalSources = null)
    {
        var tenant = new TenantId("tenant-a");
        var sourceSystems = new[]
        {
            new SourceSystemId("pas"),
            new SourceSystemId("community"),
            new SourceSystemId("external")
        };
        var configuration = new TenantMatchingConfiguration(
            tenant,
            MatchingProfile.UkDefault,
            secrets,
            sourceSystems.ToDictionary(static source => source, static _ => 100),
            sourceSystems.ToHashSet(),
            2);
        var store = new InMemoryIdentityRegistryStore();
        return CreateFixture(store, configuration, externalSources);
    }

    private static Fixture CreateFixture(
        InMemoryIdentityRegistryStore store,
        TenantMatchingConfiguration configuration,
        IExternalPatientSourceRegistry? externalSources)
    {
        var configurations = new Dictionary<TenantId, TenantMatchingConfiguration>
        {
            [configuration.TenantId] = configuration
        };
        var provider = new StaticTenantConfigurationProvider(configurations);
        var registry = new RegistryService(store, provider, TimeProvider.System);
        var maintenance = new RegistryMaintenanceService(
            store,
            provider,
            externalSources ?? new EmptyExternalPatientSourceRegistry(),
            registry,
            TimeProvider.System);
        var admin = new ActorContext(
            configuration.TenantId,
            "test-admin",
            null,
            new HashSet<string>(StringComparer.Ordinal)
            {
                "mpi.admin",
                "mpi.operations",
                "mpi.review",
                "mpi.audit"
            },
            Guid.CreateVersion7().ToString("N"));
        return new Fixture(store, configuration, registry, maintenance, admin);
    }

    private static BlockingKeySecret Secret(string version, byte value, bool active) =>
        new(version, Enumerable.Repeat(value, 32).ToArray(), active);

    private static IdentityProfile Profile(string family) =>
        new(
            [],
            [new PersonName(family, ["Alex"], NameUse.Official)],
            new DateOnly(1980, 1, 2),
            AdministrativeGender.Unknown,
            [new PostalAddress(["1 High Street"], "Leeds", null, "LS1 1AA", "GB")],
            []);

    private sealed record Fixture(
        InMemoryIdentityRegistryStore Store,
        TenantMatchingConfiguration Configuration,
        RegistryService Registry,
        RegistryMaintenanceService Maintenance,
        ActorContext Admin)
    {
        public Fixture WithConfiguration(IReadOnlyList<BlockingKeySecret> secrets) =>
            CreateFixture(
                Store,
                Configuration with { BlockingKeySecrets = secrets },
                new EmptyExternalPatientSourceRegistry());

        public ValueTask<UpsertPatientResult> UpsertAsync(
            string sourceSystem,
            string localId,
            IdentityProfile profile)
        {
            var source = new SourceSystemId(sourceSystem);
            return Registry.UpsertPatientAsync(
                Admin with
                {
                    ActorId = $"{sourceSystem}-service",
                    SourceSystem = source,
                    Scopes = new HashSet<string>(StringComparer.Ordinal)
                },
                new UpsertPatientCommand(new SourceRecordKey(source, localId), profile),
                CancellationToken.None);
        }
    }

    private sealed class SinglePageExternalSourceRegistry(IExternalPatientSource source)
        : IExternalPatientSourceRegistry
    {
        public IExternalPatientSource? Find(TenantId tenantId, SourceSystemId sourceSystem) =>
            tenantId == source.TenantId && sourceSystem == source.SourceSystem ? source : null;
    }

    private sealed class FakeExternalSource(
        TenantId tenantId,
        SourceSystemId sourceSystem,
        ExternalPatientPage page) : IExternalPatientSource
    {
        public TenantId TenantId { get; } = tenantId;

        public SourceSystemId SourceSystem { get; } = sourceSystem;

        public ValueTask<ExternalPatientPage> ReadPageAsync(
            DateTimeOffset? changedSince,
            DateTimeOffset changedThrough,
            string? cursor,
            int count,
            CancellationToken cancellationToken)
        {
            _ = changedSince;
            _ = changedThrough;
            _ = cursor;
            _ = count;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(page);
        }
    }
}
