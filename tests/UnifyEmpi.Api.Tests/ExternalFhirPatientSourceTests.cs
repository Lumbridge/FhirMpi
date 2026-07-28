using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using UnifyEmpi.Api;
using UnifyEmpi.Domain;
using UnifyEmpi.Fhir.R4;
using Xunit;

namespace UnifyEmpi.Api.Tests;

public sealed class ExternalFhirPatientSourceTests
{
    [Fact]
    public async Task SourceUsesBoundedLastUpdatedWindowAndOpaqueFhirPaging()
    {
        var handler = new RecordingHandler(
            """
            {
              "resourceType": "Bundle",
              "type": "searchset",
              "link": [{
                "relation": "next",
                "url": "https://fhir.example.test/r4/Patient?_page_token=opaque"
              }],
              "entry": [{
                "search": { "mode": "match" },
                "resource": {
                  "resourceType": "Patient",
                  "id": "server-patient-1",
                  "meta": {
                    "versionId": "7",
                    "lastUpdated": "2026-07-28T10:00:00Z"
                  },
                  "identifier": [{
                    "system": "https://hospital.example.test/mrn",
                    "value": "MRN-123"
                  }],
                  "name": [{ "family": "Jones", "given": ["Alex"] }],
                  "birthDate": "1980-01-02"
                }
              }]
            }
            """);
        using var registry = new FhirPatientSourceRegistry(
            Options.Create(new RegistryMaintenanceOptions
            {
                FhirSources =
                [
                    new ExternalFhirSourceDefinition
                    {
                        TenantId = "tenant-a",
                        SourceSystem = "hospital",
                        BaseUrl = "https://fhir.example.test/r4",
                        LocalIdentifierSystem = "https://hospital.example.test/mrn",
                        PatientSearchParameters =
                        {
                            ["active"] = "true"
                        }
                    }
                ]
            }),
            new SingleClientFactory(new HttpClient(handler)),
            new FhirResourceCodec());
        var source = registry.Find(
            new TenantId("tenant-a"),
            new SourceSystemId("hospital"));

        var page = await source!.ReadPageAsync(
            DateTimeOffset.Parse(
                "2026-07-27T00:00:00Z",
                System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(
                "2026-07-29T00:00:00Z",
                System.Globalization.CultureInfo.InvariantCulture),
            null,
            25,
            CancellationToken.None);

        var patient = Assert.Single(page.Items);
        Assert.Equal("MRN-123", patient.LocalId);
        Assert.Equal("7", patient.SourceVersion);
        Assert.Equal("Jones", patient.Profile.Names.Single().Family);
        Assert.Equal(
            "https://fhir.example.test/r4/Patient?_page_token=opaque",
            page.NextCursor);
        Assert.NotNull(handler.LastRequestUri);
        var query = Uri.UnescapeDataString(handler.LastRequestUri.Query);
        Assert.Contains("_count=25", query, StringComparison.Ordinal);
        Assert.Contains("_lastUpdated=ge2026-07-27T00:00:00.0000000+00:00", query, StringComparison.Ordinal);
        Assert.Contains("_lastUpdated=le2026-07-29T00:00:00.0000000+00:00", query, StringComparison.Ordinal);
        Assert.Contains("active=true", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourceRejectsCrossOriginNextLinks()
    {
        var handler = new RecordingHandler(
            """
            {
              "resourceType": "Bundle",
              "type": "searchset",
              "link": [{
                "relation": "next",
                "url": "https://attacker.example/Patient?page=2"
              }]
            }
            """);
        using var registry = new FhirPatientSourceRegistry(
            Options.Create(new RegistryMaintenanceOptions
            {
                FhirSources =
                [
                    new ExternalFhirSourceDefinition
                    {
                        TenantId = "tenant-a",
                        SourceSystem = "hospital",
                        BaseUrl = "https://fhir.example.test/r4"
                    }
                ]
            }),
            new SingleClientFactory(new HttpClient(handler)),
            new FhirResourceCodec());
        var source = registry.Find(
            new TenantId("tenant-a"),
            new SourceSystemId("hospital"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            source!.ReadPageAsync(
                null,
                DateTimeOffset.Parse(
                    "2026-07-29T00:00:00Z",
                    System.Globalization.CultureInfo.InvariantCulture),
                null,
                25,
                CancellationToken.None).AsTask());
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            _ = name;
            return client;
        }
    }

    private sealed class RecordingHandler(string responseBody) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    responseBody,
                    Encoding.UTF8,
                    "application/fhir+json")
            });
        }
    }
}
