using Hl7.Fhir.Model;

namespace FhirMpi.Storage.Gcp;

public interface IGcpFhirClient
{
    ValueTask<Resource?> ReadAsync(
        string resourceType,
        string resourceId,
        CancellationToken cancellationToken);

    ValueTask<Bundle> SearchAsync(
        string resourceType,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken);

    ValueTask<Bundle> ExecuteTransactionAsync(
        Bundle transaction,
        CancellationToken cancellationToken);

    ValueTask<bool> CheckHealthAsync(CancellationToken cancellationToken);
}
