using System.Net;
using Google.Apis.Auth.OAuth2;
using Google.Apis.CloudHealthcare.v1;
using Google.Apis.Services;
using Hl7.Fhir.Model;
using UnifyEmpi.Fhir.R4;

namespace UnifyEmpi.Storage.Gcp;

public sealed class HealthcareApiFhirClient : IGcpFhirClient, IDisposable
{
    private readonly CloudHealthcareService _service;
    private readonly GcpFhirStoreOptions _options;
    private readonly FhirResourceCodec _codec;

    public HealthcareApiFhirClient(
        CloudHealthcareService service,
        GcpFhirStoreOptions options,
        FhirResourceCodec codec)
    {
        _service = service;
        _options = options;
        _options.Validate();
        _codec = codec;
    }

    public static HealthcareApiFhirClient Create(
        GcpFhirStoreOptions options,
        FhirResourceCodec codec)
    {
        options.Validate();
        var credential = GoogleCredential.GetApplicationDefault()
            .CreateScoped(CloudHealthcareService.Scope.CloudPlatform);
        var service = new CloudHealthcareService(new BaseClientService.Initializer
        {
            ApplicationName = options.ApplicationName,
            HttpClientInitializer = credential
        });
        return new HealthcareApiFhirClient(service, options, codec);
    }

    public async ValueTask<Resource?> ReadAsync(
        string resourceType,
        string resourceId,
        CancellationToken cancellationToken)
    {
        ValidatePathSegment(resourceType);
        ValidatePathSegment(resourceId);
        using var request = CreateRequest(
            HttpMethod.Get,
            $"{resourceType}/{resourceId}");
        using var response = await _service.HttpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        return await ReadResourceAsync(response, cancellationToken);
    }

    public async ValueTask<Bundle> SearchAsync(
        string resourceType,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        ValidatePathSegment(resourceType);
        var query = string.Join(
            "&",
            parameters.Select(static parameter =>
                $"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value)}"));
        using var request = CreateRequest(
            HttpMethod.Get,
            string.IsNullOrEmpty(query) ? resourceType : $"{resourceType}?{query}");
        request.Headers.TryAddWithoutValidation("Prefer", "handling=strict");
        using var response = await _service.HttpClient.SendAsync(request, cancellationToken);
        var resource = await ReadResourceAsync(response, cancellationToken);
        return resource as Bundle ??
               throw new InvalidOperationException("The GCP FHIR search response was not a Bundle.");
    }

    public async ValueTask<Bundle> ExecuteTransactionAsync(
        Bundle transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (transaction.Type != Bundle.BundleType.Transaction)
        {
            throw new ArgumentException("Only FHIR transaction bundles are accepted.", nameof(transaction));
        }

        using var request = CreateRequest(HttpMethod.Post, string.Empty);
        request.Headers.TryAddWithoutValidation("Prefer", "handling=strict");
        request.Content = new StringContent(
            _codec.Serialise(transaction, FhirWireFormat.Json),
            System.Text.Encoding.UTF8,
            "application/fhir+json");
        using var response = await _service.HttpClient.SendAsync(request, cancellationToken);
        var resource = await ReadResourceAsync(response, cancellationToken);
        return resource as Bundle ??
               throw new InvalidOperationException("The GCP FHIR transaction response was not a Bundle.");
    }

    public async ValueTask<bool> CheckHealthAsync(CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "metadata");
        using var response = await _service.HttpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public void Dispose() => _service.Dispose();

    private HttpRequestMessage CreateRequest(HttpMethod method, string relative)
    {
        var baseUri =
            $"https://healthcare.googleapis.com/v1/{_options.StoreName}/fhir";
        var uri = relative.Length == 0 ? baseUri : $"{baseUri}/{relative}";
        return new HttpRequestMessage(method, uri);
    }

    private async ValueTask<Resource> ReadResourceAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"GCP Healthcare FHIR request failed with HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        return _codec.Parse(body, FhirWireFormat.Json);
    }

    private static void ValidatePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '.'))
        {
            throw new ArgumentException("FHIR path segments contain unsupported characters.", nameof(value));
        }
    }
}
