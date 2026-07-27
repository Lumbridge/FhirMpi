using UnifyEmpi.Domain;
using UnifyEmpi.Fhir.R4;
using UnifyEmpi.Storage.Abstractions;
using UnifyEmpi.Storage.Gcp;
using UnifyEmpi.Storage.Testing;
using Xunit;

namespace UnifyEmpi.Storage.Gcp.LiveTests;

public sealed class LiveGcpProviderTests
{
    [Fact]
    public async Task IsolatedStoreSupportsHealthAtomicWriteAndTenantSafeRead()
    {
        var storeName = Environment.GetEnvironmentVariable("GCP_FHIR_STORE");
        Assert.False(
            string.IsNullOrWhiteSpace(storeName),
            "GCP_FHIR_STORE must identify an isolated disposable R4 test store.");

        using var client = HealthcareApiFhirClient.Create(
            new GcpFhirStoreOptions
            {
                StoreName = storeName,
                ApplicationName = "UnifyEmpi.LiveProviderTests"
            },
            new FhirResourceCodec());
        var store = new GcpIdentityRegistryStore(client);
        var health = await store.CheckHealthAsync(CancellationToken.None);
        Assert.True(health.IsHealthy, health.Detail);

        var tenant = $"live-{Guid.NewGuid():N}";
        var actor = Actor(tenant);
        var patient = new CanonicalPatient(
            EnterpriseId.New(),
            IdentityProfile.Empty,
            [],
            [],
            0,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            1);

        var commit = await store.CommitAsync(
            actor,
            new RegistryMutation([], [patient], [], [], [], []),
            CancellationToken.None);
        Assert.True(commit.WasApplied);

        var roundTrip = await store.GetCanonicalPatientAsync(
            actor,
            patient.EnterpriseId,
            CancellationToken.None);
        Assert.Equal(patient.EnterpriseId, roundTrip?.EnterpriseId);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.GetCanonicalPatientAsync(
                Actor($"{tenant}-other"),
                patient.EnterpriseId,
                CancellationToken.None));
    }

    private static ActorContext Actor(string tenant) =>
        new(
            new TenantId(tenant),
            "live-provider-tests",
            null,
            new HashSet<string>(),
            Guid.NewGuid().ToString("N"));
}

public sealed class LiveGcpProviderContractTests : ProviderContractSuite
{
    protected override IIdentityRegistryStore CreateStore()
    {
        var storeName = Environment.GetEnvironmentVariable("GCP_FHIR_STORE");
        if (string.IsNullOrWhiteSpace(storeName))
        {
            throw new InvalidOperationException(
                "GCP_FHIR_STORE must identify an isolated disposable R4 test store.");
        }

        var client = HealthcareApiFhirClient.Create(
            new GcpFhirStoreOptions
            {
                StoreName = storeName,
                ApplicationName = "UnifyEmpi.LiveProviderContractTests"
            },
            new FhirResourceCodec());
        return new GcpIdentityRegistryStore(client);
    }
}
