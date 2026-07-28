using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using UnifyEmpi.Application.Configuration;
using UnifyEmpi.Domain;
using Xunit;

namespace UnifyEmpi.Api.Tests;

public sealed class FhirApiIntegrationTests : IClassFixture<FhirApiFactory>
{
    private static readonly double[] EvaluationThresholds = [0.82];
    private readonly HttpClient _client;

    public FhirApiIntegrationTests(FhirApiFactory factory) =>
        _client = factory.CreateClient();

    [Fact]
    public async Task ApiRootServesSwaggerUi()
    {
        var response = await _client.GetAsync("/", CancellationToken.None);
        var content = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("swagger-ui", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SwaggerDocumentIncludesFhirAndOperationalEndpoints()
    {
        var response = await _client.GetAsync(
            "/swagger/v1/swagger.json",
            CancellationToken.None);
        var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(CancellationToken.None));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("UnifyEMPI API", document.RootElement
            .GetProperty("info")
            .GetProperty("title")
            .GetString());
        Assert.True(document.RootElement
            .GetProperty("paths")
            .TryGetProperty("/fhir/R4/Patient", out _));
        Assert.True(document.RootElement
            .GetProperty("paths")
            .TryGetProperty("/api/v1/operations/summary", out _));
        Assert.True(document.RootElement
            .GetProperty("paths")
            .TryGetProperty("/api/v1/matching/evaluation", out _));
        Assert.True(document.RootElement
            .GetProperty("paths")
            .TryGetProperty("/api/v1/matching/calibration/fellegi-sunter", out _));
    }

    [Fact]
    public async Task CapabilityStatementAdvertisesR4AndMatch()
    {
        var response = await _client.GetAsync(
            "/fhir/R4/metadata",
            CancellationToken.None);
        var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(CancellationToken.None));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("CapabilityStatement", document.RootElement.GetProperty("resourceType").GetString());
        Assert.Equal("4.0.1", document.RootElement.GetProperty("fhirVersion").GetString());
        Assert.Contains(
            document.RootElement.GetProperty("rest")[0].GetProperty("operation").EnumerateArray(),
            static operation => operation.GetProperty("name").GetString() == "match");
    }

    [Fact]
    public async Task PatientCreateReadAndMatchFlowUsesEtags()
    {
        var patientJson = """
            {
              "resourceType": "Patient",
              "id": "local-100",
              "meta": {
                "tag": [
                  { "code": "traced" },
                  { "code": "gold" }
                ]
              },
              "identifier": [{
                "system": "https://fhir.nhs.uk/Id/nhs-number",
                "value": "9434765919"
              }],
              "name": [{ "family": "Smith", "given": ["Alex"] }],
              "birthDate": "1980-01-02",
              "address": [{ "line": ["1 High Street"], "postalCode": "SW1A 2AA" }]
            }
            """;
        using var create = new HttpRequestMessage(HttpMethod.Post, "/fhir/R4/Patient")
        {
            Content = FhirContent(patientJson)
        };
        create.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var created = await _client.SendAsync(create, CancellationToken.None);
        var createdBody = JsonDocument.Parse(
            await created.Content.ReadAsStringAsync(CancellationToken.None));
        var resourceId = createdBody.RootElement.GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.NotNull(created.Headers.ETag);
        Assert.StartsWith("src-", resourceId, StringComparison.Ordinal);

        var read = await _client.GetAsync(
            $"/fhir/R4/Patient/{resourceId}",
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(created.Headers.ETag, read.Headers.ETag);

        var parameters = $$"""
            {
              "resourceType": "Parameters",
              "parameter": [
                { "name": "resource", "resource": {{patientJson}} },
                { "name": "onlyCertainMatches", "valueBoolean": true },
                { "name": "count", "valueInteger": 10 }
              ]
            }
            """;
        var match = await _client.PostAsync(
            "/fhir/R4/Patient/$match",
            FhirContent(parameters),
            CancellationToken.None);
        var matchBody = JsonDocument.Parse(
            await match.Content.ReadAsStringAsync(CancellationToken.None));

        Assert.Equal(HttpStatusCode.OK, match.StatusCode);
        Assert.Equal("searchset", matchBody.RootElement.GetProperty("type").GetString());
        var entry = Assert.Single(matchBody.RootElement.GetProperty("entry").EnumerateArray());
        Assert.Contains(
            entry.GetProperty("resource").GetProperty("extension").EnumerateArray(),
            static extension =>
                extension.GetProperty("url").GetString() ==
                "http://hl7.org/fhir/StructureDefinition/match-grade" &&
                extension.GetProperty("valueCode").GetString() == "certain");
    }

    [Fact]
    public async Task ReservedTenantSearchFiltersAreRejected()
    {
        var response = await _client.GetAsync(
            "/fhir/R4/Patient?_security=attacker",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/fhir+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task TenantAndSourceHeadersCannotOverrideValidatedClaims()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/fhir/R4/Patient?family=Smith");
        request.Headers.Add("X-Tenant-Id", "attacker");
        request.Headers.Add("X-Source-System", "attacker-source");

        var response = await _client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/fhir+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task UpdateRequiresIfMatch()
    {
        const string patient = """
            {
              "resourceType": "Patient",
              "id": "local-update",
              "name": [{ "family": "Jones", "given": ["Sam"] }],
              "birthDate": "1970-03-04"
            }
            """;
        var create = await _client.PostAsync(
            "/fhir/R4/Patient",
            FhirContent(patient),
            CancellationToken.None);
        var body = JsonDocument.Parse(
            await create.Content.ReadAsStringAsync(CancellationToken.None));
        var id = body.RootElement.GetProperty("id").GetString();

        var update = await _client.PutAsync(
            $"/fhir/R4/Patient/{id}",
            FhirContent(patient.Replace("local-update", id, StringComparison.Ordinal)),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.PreconditionFailed, update.StatusCode);
    }

    [Fact]
    public async Task MaintenanceReindexIsAcceptedAndExposesDurableStatus()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/maintenance/reindex",
            new { reason = "Verify the asynchronous re-index API.", batchSize = 5 },
            CancellationToken.None);
        var created = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(CancellationToken.None));
        var id = created.RootElement.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(
            $"/api/v1/maintenance/jobs/{id:D}",
            response.Headers.Location?.OriginalString);

        var status = await _client.GetAsync(
            $"/api/v1/maintenance/jobs/{id:D}",
            CancellationToken.None);
        var statusBody = JsonDocument.Parse(
            await status.Content.ReadAsStringAsync(CancellationToken.None));

        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal(id, statusBody.RootElement.GetProperty("id").GetGuid());
        Assert.Equal((int)RegistryMaintenanceJobKind.Reindex, statusBody.RootElement
            .GetProperty("kind")
            .GetInt32());
    }

    [Fact]
    public async Task GroundTruthEvaluationUsesTenantBoundSourceReferences()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var matchLeft = $"gt-ml-{suffix}";
        var matchRight = $"gt-mr-{suffix}";
        var nonMatchLeft = $"gt-nl-{suffix}";
        var nonMatchRight = $"gt-nr-{suffix}";
        await CreateGroundTruthPatientAsync(matchLeft, "Smith", "Alex", "1980-01-02");
        await CreateGroundTruthPatientAsync(matchRight, "Smith", "Alex", "1980-01-02");
        await CreateGroundTruthPatientAsync(nonMatchLeft, "Jones", "Morgan", "1970-03-04");
        await CreateGroundTruthPatientAsync(nonMatchRight, "Williams", "Taylor", "1990-05-06");

        var response = await _client.PostAsJsonAsync(
            "/api/v1/matching/evaluation",
            new
            {
                datasetId = $"api-labels-{suffix}",
                pairs = new object[]
                {
                    new
                    {
                        left = new { sourceSystem = "demo-source", localId = matchLeft },
                        right = new { sourceSystem = "demo-source", localId = matchRight },
                        isMatch = true
                    },
                    new
                    {
                        left = new { sourceSystem = "demo-source", localId = nonMatchLeft },
                        right = new { sourceSystem = "demo-source", localId = nonMatchRight },
                        isMatch = false
                    }
                },
                thresholds = EvaluationThresholds,
                maximumErrorExamples = 5
            },
            CancellationToken.None);
        var report = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(CancellationToken.None));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, report.RootElement.GetProperty("labelCount").GetInt32());
        Assert.Equal(1, report.RootElement.GetProperty("blockingRecall").GetDouble());
        Assert.Equal(64, report.RootElement.GetProperty("datasetDigest").GetString()!.Length);
    }

    private async Task CreateGroundTruthPatientAsync(
        string id,
        string family,
        string given,
        string birthDate)
    {
        var patient = $$"""
            {
              "resourceType": "Patient",
              "id": "{{id}}",
              "name": [{ "family": "{{family}}", "given": ["{{given}}"] }],
              "birthDate": "{{birthDate}}"
            }
            """;
        var response = await _client.PostAsync(
            "/fhir/R4/Patient",
            FhirContent(patient),
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static StringContent FhirContent(string json) =>
        new(json, Encoding.UTF8, "application/fhir+json");
}

public sealed class FhirApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Authentication:Enabled"] = "false",
                    ["RegistryProvider:Type"] = "InMemory"
                }));
    }
}

public sealed class MatchingRuleConfigurationTests
{
    [Fact]
    public void ExplicitBlockingRulesReplaceTheDefaultSetDuringConfigurationBinding()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["MatchingRules:BlockingRules:0"] = "Email",
                    ["MatchingRules:AuthoritativeIdentifierSystems:0"] =
                        "https://hospital.example/Id/mrn",
                    ["MatchingRules:Weights:FamilyName"] = "0.4"
                })
            .Build();

        var options = configuration
            .GetRequiredSection("MatchingRules")
            .Get<MatchingRuleOptions>();
        var profile = MatchingProfileFactory.Create("tenant-v2", 0.6, 0.85, options);

        Assert.Equal([BlockingRuleKind.Email], profile.BlockingRules);
        Assert.Equal(["https://hospital.example/Id/mrn"], profile.AuthoritativeIdentifierSystems);
        Assert.Equal(0.4, profile.Weights.FamilyName);
        Assert.Equal(0.2, profile.Weights.GivenNames);
    }
}
