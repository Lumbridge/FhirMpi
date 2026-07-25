using System.Globalization;
using System.Net;
using System.Text;
using FhirMpi.Fhir.R4;
using FhirMpi.Storage.Abstractions;
using FhirMpi.Storage.Gcp;
using FhirMpi.Storage.Testing;
using Hl7.Fhir.Model;

namespace FhirMpi.Storage.Gcp.Tests;

public sealed class DeterministicGcpProviderContractTests : ProviderContractSuite
{
    protected override IIdentityRegistryStore CreateStore() =>
        new GcpIdentityRegistryStore(new DeterministicGcpFhirClient());
}

internal sealed class DeterministicGcpFhirClient : IGcpFhirClient
{
    private readonly Dictionary<(string Type, string Id), Resource> _resources = [];
    private readonly Dictionary<(string Type, string Id), long> _resourceRevisions = [];

    public ValueTask<Resource?> ReadAsync(
        string resourceType,
        string resourceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            _resources.TryGetValue((resourceType, resourceId), out var resource)
                ? Clone(resource)
                : null);
    }

    public ValueTask<Bundle> SearchAsync(
        string resourceType,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var matching = _resources
            .Where(pair => string.Equals(pair.Key.Type, resourceType, StringComparison.Ordinal))
            .Select(static pair => pair.Value)
            .Where(resource => Matches(resource, parameters))
            .OrderBy(static resource => resource.Id, StringComparer.Ordinal)
            .ToArray();
        var count = parameters.TryGetValue("_count", out var countText) &&
                    int.TryParse(countText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 20;
        var offset = parameters.TryGetValue("_page_token", out var cursor)
            ? DecodeCursor(cursor)
            : 0;
        var page = matching.Skip(offset).Take(count).ToArray();
        var bundle = new Bundle
        {
            Type = Bundle.BundleType.Searchset,
            Total = matching.Length,
            Entry = page.Select(resource => new Bundle.EntryComponent
            {
                Resource = Clone(resource),
                Search = new Bundle.SearchComponent { Mode = Bundle.SearchEntryMode.Match }
            }).ToList(),
            Link =
            [
                new Bundle.LinkComponent
                {
                    Relation = "self",
                    Url = SearchUrl(resourceType, parameters)
                }
            ]
        };
        if (offset + page.Length < matching.Length)
        {
            var nextParameters = parameters.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.Ordinal);
            nextParameters["_page_token"] = EncodeCursor(offset + page.Length);
            bundle.Link.Add(new Bundle.LinkComponent
            {
                Relation = "next",
                Url = SearchUrl(resourceType, nextParameters)
            });
        }

        return ValueTask.FromResult(bundle);
    }

    public ValueTask<Bundle> ExecuteTransactionAsync(
        Bundle transaction,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pending = transaction.Entry.Select(ParseEntry).ToArray();
        foreach (var item in pending)
        {
            var exists = _resources.TryGetValue((item.Type, item.Id), out var current);
            if (string.Equals(item.Entry.Request?.IfNoneMatch, "*", StringComparison.Ordinal) &&
                exists)
            {
                throw PreconditionFailed();
            }

            if (item.Entry.Request?.IfMatch is { } ifMatch &&
                (!exists ||
                 !string.Equals(
                     current!.Meta?.VersionId,
                     ParseEtag(ifMatch),
                     StringComparison.Ordinal)))
            {
                throw PreconditionFailed();
            }
        }

        foreach (var item in pending)
        {
            var stored = Clone(item.Entry.Resource!);
            stored.Meta ??= new Meta();
            var key = (item.Type, item.Id);
            var nextRevision = _resourceRevisions.GetValueOrDefault(key) + 1;
            stored.Meta.VersionId = $"opaque-{nextRevision.ToString(CultureInfo.InvariantCulture)}";
            stored.Meta.LastUpdated = DateTimeOffset.UtcNow;
            _resources[key] = stored;
            _resourceRevisions[key] = nextRevision;
        }

        return ValueTask.FromResult(new Bundle
        {
            Type = Bundle.BundleType.TransactionResponse,
            Entry = pending.Select(static _ => new Bundle.EntryComponent
            {
                Response = new Bundle.ResponseComponent { Status = "200 OK" }
            }).ToList()
        });
    }

    public ValueTask<bool> CheckHealthAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(true);
    }

    private static (string Type, string Id, Bundle.EntryComponent Entry) ParseEntry(
        Bundle.EntryComponent entry)
    {
        if (entry.Resource is null || string.IsNullOrWhiteSpace(entry.Request?.Url))
        {
            throw new InvalidOperationException("The deterministic transaction entry is incomplete.");
        }

        var segments = entry.Request.Url.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2)
        {
            throw new InvalidOperationException("The deterministic transaction URL is invalid.");
        }

        return (segments[0], segments[1], entry);
    }

    private static bool Matches(
        Resource resource,
        IReadOnlyDictionary<string, string> parameters)
    {
        if (parameters.TryGetValue("_security", out var security) &&
            !HasCoding(resource.Meta?.Security, security))
        {
            return false;
        }

        if (parameters.TryGetValue("_tag", out var tags) &&
            !tags.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Any(tag => HasCoding(resource.Meta?.Tag, tag)))
        {
            return false;
        }

        if (parameters.TryGetValue("identifier", out var identifier) &&
            !Identifiers(resource).Any(value =>
                TokenMatches(value.System, value.Value, identifier)))
        {
            return false;
        }

        if (resource is Patient patient)
        {
            if (parameters.TryGetValue("family", out var family) &&
                !patient.Name.Any(name =>
                    string.Equals(name.Family, family, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (parameters.TryGetValue("birthdate", out var birthDate) &&
                !string.Equals(patient.BirthDate, birthDate, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static List<Identifier> Identifiers(Resource resource) =>
        resource switch
        {
            Patient patient => patient.Identifier,
            Person person => person.Identifier,
            _ => []
        };

    private static bool HasCoding(IEnumerable<Coding>? codings, string token)
    {
        var separator = token.IndexOf('|', StringComparison.Ordinal);
        var system = separator < 0 ? null : token[..separator];
        var code = separator < 0 ? token : token[(separator + 1)..];
        return codings?.Any(coding =>
            (system is null || string.Equals(coding.System, system, StringComparison.Ordinal)) &&
            string.Equals(coding.Code, code, StringComparison.Ordinal)) == true;
    }

    private static bool TokenMatches(string? system, string? value, string token)
    {
        var separator = token.IndexOf('|', StringComparison.Ordinal);
        return separator < 0
            ? string.Equals(value, token, StringComparison.Ordinal)
            : string.Equals(system, token[..separator], StringComparison.Ordinal) &&
              string.Equals(value, token[(separator + 1)..], StringComparison.Ordinal);
    }

    private static string SearchUrl(
        string resourceType,
        IReadOnlyDictionary<string, string> parameters)
    {
        var query = string.Join(
            "&",
            parameters.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair =>
                    $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return $"https://deterministic.test/{resourceType}?{query}";
    }

    private static string EncodeCursor(int offset) =>
        Convert.ToBase64String(Encoding.ASCII.GetBytes(
            offset.ToString(CultureInfo.InvariantCulture)));

    private static int DecodeCursor(string cursor)
    {
        try
        {
            return int.Parse(
                Encoding.ASCII.GetString(Convert.FromBase64String(cursor)),
                NumberStyles.None,
                CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (
            exception is FormatException or OverflowException)
        {
            throw new InvalidOperationException("The deterministic page cursor is invalid.");
        }
    }

    private static string ParseEtag(string etag) =>
        etag.Replace("W/", string.Empty, StringComparison.Ordinal)
            .Trim('"');

    private static HttpRequestException PreconditionFailed() =>
        new(
            "The deterministic FHIR transaction precondition failed.",
            null,
            HttpStatusCode.PreconditionFailed);

    private static Resource Clone(Resource resource) =>
        (Resource)resource.DeepCopy();
}
