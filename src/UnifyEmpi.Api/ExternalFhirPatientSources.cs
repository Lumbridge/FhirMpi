using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hl7.Fhir.Model;
using Microsoft.Extensions.Options;
using UnifyEmpi.Application;
using UnifyEmpi.Domain;
using UnifyEmpi.Fhir.R4;

namespace UnifyEmpi.Api;

public sealed class FhirPatientSourceRegistry : IExternalPatientSourceRegistry, IDisposable
{
    private readonly IReadOnlyDictionary<(TenantId Tenant, SourceSystemId Source), IExternalPatientSource>
        _sources;

    public FhirPatientSourceRegistry(
        IOptions<RegistryMaintenanceOptions> options,
        IHttpClientFactory httpClientFactory,
        FhirResourceCodec codec)
    {
        var sources =
            new Dictionary<(TenantId Tenant, SourceSystemId Source), IExternalPatientSource>();
        foreach (var definition in options.Value.FhirSources)
        {
            var tenant = new TenantId(definition.TenantId);
            var source = new SourceSystemId(definition.SourceSystem);
            if (!sources.TryAdd(
                    (tenant, source),
                    new FhirPatientSource(
                        tenant,
                        source,
                        definition,
                        httpClientFactory.CreateClient("external-fhir"),
                        codec)))
            {
                throw new InvalidOperationException(
                    $"External FHIR source '{tenant}/{source}' is configured more than once.");
            }
        }

        _sources = sources;
    }

    public IExternalPatientSource? Find(TenantId tenantId, SourceSystemId sourceSystem) =>
        _sources.GetValueOrDefault((tenantId, sourceSystem));

    public void Dispose()
    {
        foreach (var source in _sources.Values.OfType<IDisposable>())
        {
            source.Dispose();
        }
    }
}

internal sealed class FhirPatientSource : IExternalPatientSource, IDisposable
{
    private static readonly HashSet<string> ReservedSearchParameters =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "_count",
            "_lastUpdated",
            "_format"
        };

    private readonly ExternalFhirSourceDefinition _definition;
    private readonly HttpClient _httpClient;
    private readonly FhirResourceCodec _codec;
    private readonly Uri _baseUri;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAt;

    public FhirPatientSource(
        TenantId tenantId,
        SourceSystemId sourceSystem,
        ExternalFhirSourceDefinition definition,
        HttpClient httpClient,
        FhirResourceCodec codec)
    {
        TenantId = tenantId;
        SourceSystem = sourceSystem;
        _definition = definition;
        _httpClient = httpClient;
        _codec = codec;
        _baseUri = ValidateBaseUri(definition);
        ValidateDefinition(definition);
    }

    public TenantId TenantId { get; }

    public SourceSystemId SourceSystem { get; }

    public void Dispose()
    {
        _tokenLock.Dispose();
        _httpClient.Dispose();
    }

    public async ValueTask<ExternalPatientPage> ReadPageAsync(
        DateTimeOffset? changedSince,
        DateTimeOffset changedThrough,
        string? cursor,
        int count,
        CancellationToken cancellationToken)
    {
        var requestUri = cursor is null
            ? BuildInitialSearchUri(changedSince, changedThrough, Math.Clamp(count, 1, 100))
            : ValidateCursor(cursor);
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/fhir+json"));
        request.Headers.TryAddWithoutValidation("Prefer", "handling=strict");
        await ApplyAuthenticationAsync(request, cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(
            Math.Clamp(_definition.RequestTimeoutSeconds, 5, 300)));
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"External FHIR Patient search failed with HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        var payload = await response.Content.ReadAsStringAsync(timeout.Token);
        var bundle = _codec.Parse<Bundle>(payload, FhirWireFormat.Json);
        if (bundle.Type != Bundle.BundleType.Searchset)
        {
            throw new InvalidOperationException(
                "The external FHIR Patient search did not return a searchset Bundle.");
        }

        var results = new List<ExternalPatientRecord>();
        foreach (var patient in bundle.Entry
                     .Where(static entry => entry.Search?.Mode is null or Bundle.SearchEntryMode.Match)
                     .Select(static entry => entry.Resource)
                     .OfType<Patient>())
        {
            if (string.IsNullOrWhiteSpace(patient.Id))
            {
                throw new InvalidOperationException(
                    "An external FHIR Patient search result omitted Patient.id.");
            }

            var lastUpdated = patient.Meta?.LastUpdated ?? changedThrough;
            if (lastUpdated > changedThrough ||
                (changedSince.HasValue && lastUpdated < changedSince.Value))
            {
                continue;
            }

            var resourceId = patient.Id;
            var localId = ResolveLocalId(patient);
            var serialised = _codec.Serialise(patient, FhirWireFormat.Json);
            var digest = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(serialised))).ToLowerInvariant();
            results.Add(new ExternalPatientRecord(
                SourceSystem,
                localId,
                resourceId,
                patient.Meta?.VersionId ??
                $"{lastUpdated.ToString("O", CultureInfo.InvariantCulture)}-{digest[..16]}",
                lastUpdated,
                FhirR4Mapper.ToDomain(patient),
                digest));
        }

        var next = bundle.Link.FirstOrDefault(link =>
            string.Equals(link.Relation, "next", StringComparison.Ordinal))?.Url;
        return new ExternalPatientPage(
            results,
            string.IsNullOrWhiteSpace(next) ? null : ValidateCursor(next).AbsoluteUri);
    }

    private Uri BuildInitialSearchUri(
        DateTimeOffset? changedSince,
        DateTimeOffset changedThrough,
        int count)
    {
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("_count", count.ToString(CultureInfo.InvariantCulture)),
            new(
                "_lastUpdated",
                $"le{changedThrough.ToString("O", CultureInfo.InvariantCulture)}")
        };
        if (changedSince.HasValue)
        {
            parameters.Add(new KeyValuePair<string, string>(
                "_lastUpdated",
                $"ge{changedSince.Value.ToString("O", CultureInfo.InvariantCulture)}"));
        }

        parameters.AddRange(_definition.PatientSearchParameters);
        var query = string.Join(
            "&",
            parameters.Select(static item =>
                $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}"));
        return new Uri(_baseUri, $"Patient?{query}");
    }

    private string ResolveLocalId(Patient patient)
    {
        if (string.IsNullOrWhiteSpace(_definition.LocalIdentifierSystem))
        {
            return patient.Id!;
        }

        var values = patient.Identifier
            .Where(identifier => string.Equals(
                identifier.System,
                _definition.LocalIdentifierSystem,
                StringComparison.Ordinal))
            .Select(static identifier => identifier.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return values.Length switch
        {
            1 => values[0]!,
            0 => throw new InvalidOperationException(
                "An external Patient is missing the configured source identifier."),
            _ => throw new InvalidOperationException(
                "An external Patient has multiple values for the configured source identifier.")
        };
    }

    private async ValueTask ApplyAuthenticationAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var type = _definition.Authentication.Type.Trim();
        switch (type.ToLowerInvariant())
        {
            case "none":
                return;
            case "bearer":
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Bearer",
                    _definition.Authentication.BearerToken);
                return;
            case "clientcredentials":
            case "client-credentials":
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Bearer",
                    await GetClientCredentialsTokenAsync(cancellationToken));
                return;
            default:
                throw new InvalidOperationException(
                    $"Unsupported external FHIR authentication type '{type}'.");
        }
    }

    private async ValueTask<string> GetClientCredentialsTokenAsync(
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (_accessToken is not null && _accessTokenExpiresAt > now.AddSeconds(30))
        {
            return _accessToken;
        }

        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (_accessToken is not null && _accessTokenExpiresAt > now.AddSeconds(30))
            {
                return _accessToken;
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                _definition.Authentication.TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(
                [
                    new KeyValuePair<string, string>("grant_type", "client_credentials"),
                    new KeyValuePair<string, string>(
                        "client_id",
                        _definition.Authentication.ClientId!),
                    new KeyValuePair<string, string>(
                        "client_secret",
                        _definition.Authentication.ClientSecret!),
                    new KeyValuePair<string, string>(
                        "scope",
                        _definition.Authentication.Scope ?? string.Empty)
                ])
            };
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"External FHIR token request failed with HTTP {(int)response.StatusCode}.",
                    null,
                    response.StatusCode);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken);
            _accessToken = document.RootElement.GetProperty("access_token").GetString();
            if (string.IsNullOrWhiteSpace(_accessToken))
            {
                throw new InvalidOperationException(
                    "The external FHIR token response omitted access_token.");
            }

            var expiresIn = document.RootElement.TryGetProperty("expires_in", out var expires) &&
                            expires.TryGetInt32(out var seconds)
                ? Math.Clamp(seconds, 60, 86400)
                : 300;
            _accessTokenExpiresAt = now.AddSeconds(expiresIn);
            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private Uri ValidateCursor(string cursor)
    {
        Uri? uri;
        if (!Uri.TryCreate(cursor, UriKind.Absolute, out uri) &&
            (!Uri.TryCreate(cursor, UriKind.Relative, out var relative) ||
             !Uri.TryCreate(_baseUri, relative, out uri)))
        {
            throw new InvalidOperationException(
                "The external FHIR server returned an invalid paging link.");
        }

        if (uri is null ||
            !string.Equals(uri.Scheme, _baseUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, _baseUri.Host, StringComparison.OrdinalIgnoreCase) ||
            uri.Port != _baseUri.Port ||
            !uri.AbsolutePath.StartsWith(_baseUri.AbsolutePath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The external FHIR server returned an unsafe cross-origin paging link.");
        }

        return uri;
    }

    private static Uri ValidateBaseUri(ExternalFhirSourceDefinition definition)
    {
        if (!Uri.TryCreate(definition.BaseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps &&
             !(definition.AllowInsecureHttp && uri.Scheme == Uri.UriSchemeHttp)) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                "External FHIR BaseUrl must be an absolute HTTPS URL without query or fragment.");
        }

        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
    }

    private static void ValidateDefinition(ExternalFhirSourceDefinition definition)
    {
        foreach (var parameter in definition.PatientSearchParameters)
        {
            if (string.IsNullOrWhiteSpace(parameter.Key) ||
                ReservedSearchParameters.Contains(parameter.Key))
            {
                throw new InvalidOperationException(
                    $"External FHIR Patient search parameter '{parameter.Key}' is reserved.");
            }
        }

        var authentication = definition.Authentication;
        switch (authentication.Type.Trim().ToLowerInvariant())
        {
            case "none":
                break;
            case "bearer" when !string.IsNullOrWhiteSpace(authentication.BearerToken):
                break;
            case "clientcredentials" or "client-credentials"
                when Uri.TryCreate(authentication.TokenEndpoint, UriKind.Absolute, out var tokenUri) &&
                     tokenUri.Scheme == Uri.UriSchemeHttps &&
                     !string.IsNullOrWhiteSpace(authentication.ClientId) &&
                     !string.IsNullOrWhiteSpace(authentication.ClientSecret):
                break;
            default:
                throw new InvalidOperationException(
                    "External FHIR authentication configuration is incomplete or unsupported.");
        }
    }
}
