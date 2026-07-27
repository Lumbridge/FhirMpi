using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UnifyEmpi.Domain;
using UnifyEmpi.Storage.Abstractions;
using UnifyEmpi.Storage.InMemory;
using Xunit;

namespace UnifyEmpi.Portal.Tests;

public sealed class OverviewResilienceTests : IClassFixture<FailingOverviewFactory>
{
    private readonly FailingOverviewFactory _factory;
    private readonly HttpClient _client;

    public OverviewResilienceTests(FailingOverviewFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ProviderFailureRendersRetryableOverviewError()
    {
        var response = await _client.GetAsync("/", CancellationToken.None);
        var html = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("The overview could not be loaded", html, StringComparison.Ordinal);
        Assert.Contains(
            "The registry provider could not be reached.",
            html,
            StringComparison.Ordinal);
        Assert.Contains("Try again", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Loading the tenant overview", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonPrerenderedDocumentDoesNotExposeMutableOverviewDom()
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Portal:PrerenderInteractiveComponents"] = "false"
                    })));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/", CancellationToken.None);
        var html = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("_framework/blazor.web.js", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Loading the tenant overview", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Operations overview", html, StringComparison.Ordinal);
    }
}

public sealed class FailingOverviewFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["PortalAuthentication:Enabled"] = "false",
                    ["Portal:PrerenderInteractiveComponents"] = "true",
                    ["Portal:SeedSyntheticData"] = "false",
                    ["RegistryProvider:Type"] = "InMemory"
                }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IIdentityRegistryStore>();
            services.AddSingleton<IIdentityRegistryStore, FailingOverviewStore>();
        });
    }
}

public sealed class FailingOverviewStore : IIdentityRegistryStore
{
    private readonly InMemoryIdentityRegistryStore _inner = new();
    private int _healthChecks;

    public ValueTask<SourcePatientRecord?> GetSourcePatientAsync(
        ActorContext context,
        SourceRecordKey key,
        CancellationToken cancellationToken) =>
        _inner.GetSourcePatientAsync(context, key, cancellationToken);

    public ValueTask<SourcePatientRecord?> GetSourcePatientByResourceIdAsync(
        ActorContext context,
        string resourceId,
        CancellationToken cancellationToken) =>
        _inner.GetSourcePatientByResourceIdAsync(context, resourceId, cancellationToken);

    public ValueTask<CanonicalPatient?> GetCanonicalPatientAsync(
        ActorContext context,
        EnterpriseId enterpriseId,
        CancellationToken cancellationToken) =>
        _inner.GetCanonicalPatientAsync(context, enterpriseId, cancellationToken);

    public ValueTask<EnterprisePerson?> GetPersonAsync(
        ActorContext context,
        EnterpriseId enterpriseId,
        CancellationToken cancellationToken) =>
        _inner.GetPersonAsync(context, enterpriseId, cancellationToken);

    public ValueTask<CandidatePage> FindCandidatesAsync(
        ActorContext context,
        IReadOnlyCollection<BlockingKey> blockingKeys,
        int maximumCandidates,
        CancellationToken cancellationToken) =>
        _inner.FindCandidatesAsync(context, blockingKeys, maximumCandidates, cancellationToken);

    public ValueTask<Page<CanonicalPatient>> SearchCanonicalPatientsAsync(
        ActorContext context,
        CanonicalPatientSearch search,
        CancellationToken cancellationToken) =>
        _inner.SearchCanonicalPatientsAsync(context, search, cancellationToken);

    public ValueTask<Page<EnterprisePerson>> SearchPersonsAsync(
        ActorContext context,
        PersonSearch search,
        CancellationToken cancellationToken) =>
        _inner.SearchPersonsAsync(context, search, cancellationToken);

    public ValueTask<ReviewCase?> GetReviewCaseAsync(
        ActorContext context,
        Guid reviewCaseId,
        CancellationToken cancellationToken) =>
        _inner.GetReviewCaseAsync(context, reviewCaseId, cancellationToken);

    public ValueTask<Page<ReviewCase>> SearchReviewCasesAsync(
        ActorContext context,
        ReviewCaseSearch search,
        CancellationToken cancellationToken) =>
        _inner.SearchReviewCasesAsync(context, search, cancellationToken);

    public ValueTask<Page<AuditRecord>> SearchAuditRecordsAsync(
        ActorContext context,
        AuditRecordSearch search,
        CancellationToken cancellationToken) =>
        _inner.SearchAuditRecordsAsync(context, search, cancellationToken);

    public ValueTask<TenantSettings?> GetTenantSettingsAsync(
        ActorContext context,
        CancellationToken cancellationToken) =>
        _inner.GetTenantSettingsAsync(context, cancellationToken);

    public ValueTask<IngestionReceipt?> GetReceiptAsync(
        ActorContext context,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        _inner.GetReceiptAsync(context, idempotencyKey, cancellationToken);

    public ValueTask<RegistryCommitResult> CommitAsync(
        ActorContext context,
        RegistryMutation mutation,
        CancellationToken cancellationToken) =>
        _inner.CommitAsync(context, mutation, cancellationToken);

    public ValueTask<RegistryStoreHealth> CheckHealthAsync(
        CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _healthChecks) == 1)
        {
            return _inner.CheckHealthAsync(cancellationToken);
        }

        throw new HttpRequestException("Synthetic provider failure.");
    }
}
