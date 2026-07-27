using Hl7.Fhir.Model;
using UnifyEmpi.Domain;
using UnifyEmpi.Fhir.R4;
using UnifyEmpi.Storage.Abstractions;
using UnifyEmpi.Storage.Gcp;
using Xunit;

namespace UnifyEmpi.Storage.Gcp.Tests;

#pragma warning disable xUnit1031 // The recording client completes every ValueTask synchronously.
public sealed class GcpProviderDefenceTests
{
    [Fact]
    public void SearchRejectsSelfLinksThatDropTenantSecurity()
    {
        var client = new RecordingFhirClient
        {
            SearchResult = SearchBundle("https://example.test/Patient?_count=20")
        };
        var store = new GcpIdentityRegistryStore(client);

        Assert.Throws<InvalidOperationException>(() =>
            store.SearchCanonicalPatientsAsync(
                Actor("tenant-a"),
                new CanonicalPatientSearch(),
                CancellationToken.None).AsTask().GetAwaiter().GetResult());
    }

    [Fact]
    public void StoreAlwaysInjectsTenantSecurityIntoSearches()
    {
        var expected = Uri.EscapeDataString(
            $"{FhirR4Constants.TenantSecuritySystem}|tenant-a");
        var client = new RecordingFhirClient
        {
            SearchResult = SearchBundle(
                $"https://example.test/Patient?_security={expected}&_count=20")
        };
        var store = new GcpIdentityRegistryStore(client);

        store.SearchCanonicalPatientsAsync(
            Actor("tenant-a"),
            new CanonicalPatientSearch(),
            CancellationToken.None).AsTask().GetAwaiter().GetResult();

        Assert.Equal(
            $"{FhirR4Constants.TenantSecuritySystem}|tenant-a",
            client.LastSearchParameters!["_security"]);
    }

    [Fact]
    public void DirectReadsVerifySecurityLabelAfterIdGuessing()
    {
        var patient = new Patient
        {
            Id = Guid.CreateVersion7().ToString("D"),
            Meta = FhirR4Mapper.CreateMeta(
                new TenantId("tenant-a"),
                1,
                DateTimeOffset.UnixEpoch)
        };
        var store = new GcpIdentityRegistryStore(
            new RecordingFhirClient { ReadResult = patient });

        Assert.Throws<InvalidOperationException>(() =>
            store.GetCanonicalPatientAsync(
                Actor("tenant-b"),
                new EnterpriseId(Guid.Parse(patient.Id)),
                CancellationToken.None).AsTask().GetAwaiter().GetResult());
    }

    [Fact]
    public void TransactionResourcesAreTenantLabelledAndUseCreatePreconditions()
    {
        var client = new RecordingFhirClient();
        var store = new GcpIdentityRegistryStore(client);
        var patient = new CanonicalPatient(
            EnterpriseId.New(),
            IdentityProfile.Empty,
            [],
            [],
            0,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            1);

        store.CommitAsync(
            Actor("tenant-a"),
            new RegistryMutation([], [patient], [], [], [], []),
            CancellationToken.None).AsTask().GetAwaiter().GetResult();

        var entry = Assert.Single(client.LastTransaction!.Entry);
        Assert.Equal("*", entry.Request!.IfNoneMatch);
        FhirR4Mapper.AssertTenant(entry.Resource!, new TenantId("tenant-a"));
    }

    [Fact]
    public void ReceiptIdsAreTenantBoundAndDoNotRevealTheIdempotencyKey()
    {
        const string key = "shared-client-key";
        var client = new RecordingFhirClient();
        var store = new GcpIdentityRegistryStore(client);
        var receipt = new IngestionReceipt(
            key,
            "digest",
            "accepted",
            "response",
            DateTimeOffset.UnixEpoch);

        store.CommitAsync(
            Actor("tenant-a"),
            RegistryMutation.Empty with { Receipt = receipt },
            CancellationToken.None).AsTask().GetAwaiter().GetResult();
        var first = Assert.Single(client.LastTransaction!.Entry).Resource!.Id;
        store.CommitAsync(
            Actor("tenant-b"),
            RegistryMutation.Empty with { Receipt = receipt },
            CancellationToken.None).AsTask().GetAwaiter().GetResult();
        var second = Assert.Single(client.LastTransaction!.Entry).Resource!.Id;

        Assert.NotEqual(first, second);
        Assert.DoesNotContain(key, first!, StringComparison.Ordinal);
    }

    private static ActorContext Actor(string tenant) =>
        new(
            new TenantId(tenant),
            "test",
            null,
            new HashSet<string>(),
            "correlation");

    private static Bundle SearchBundle(string self) =>
        new()
        {
            Type = Bundle.BundleType.Searchset,
            Link = [new Bundle.LinkComponent { Relation = "self", Url = self }]
        };

    private sealed class RecordingFhirClient : IGcpFhirClient
    {
        public Resource? ReadResult { get; init; }

        public Bundle SearchResult { get; init; } = SearchBundle(
            $"https://example.test/Patient?_security={Uri.EscapeDataString($"{FhirR4Constants.TenantSecuritySystem}|tenant-a")}");

        public IReadOnlyDictionary<string, string>? LastSearchParameters { get; private set; }

        public Bundle? LastTransaction { get; private set; }

        public ValueTask<Resource?> ReadAsync(
            string resourceType,
            string resourceId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ReadResult);

        public ValueTask<Bundle> SearchAsync(
            string resourceType,
            IReadOnlyDictionary<string, string> parameters,
            CancellationToken cancellationToken)
        {
            LastSearchParameters = parameters;
            return ValueTask.FromResult(SearchResult);
        }

        public ValueTask<Bundle> ExecuteTransactionAsync(
            Bundle transaction,
            CancellationToken cancellationToken)
        {
            LastTransaction = transaction;
            return ValueTask.FromResult(new Bundle
            {
                Type = Bundle.BundleType.TransactionResponse,
                Entry = transaction.Entry.Select(static _ => new Bundle.EntryComponent
                {
                    Response = new Bundle.ResponseComponent { Status = "201 Created" }
                }).ToList()
            });
        }

        public ValueTask<bool> CheckHealthAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(true);
    }
}
#pragma warning restore xUnit1031
