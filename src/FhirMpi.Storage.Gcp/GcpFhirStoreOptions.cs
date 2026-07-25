namespace FhirMpi.Storage.Gcp;

public sealed class GcpFhirStoreOptions
{
    public const string SectionName = "GcpHealthcare";

    public string StoreName { get; init; } = string.Empty;

    public string ApplicationName { get; init; } = "FhirMpi";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(StoreName) ||
            !StoreName.StartsWith("projects/", StringComparison.Ordinal) ||
            !StoreName.Contains("/locations/", StringComparison.Ordinal) ||
            !StoreName.Contains("/datasets/", StringComparison.Ordinal) ||
            !StoreName.Contains("/fhirStores/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "GcpHealthcare:StoreName must be a full projects/.../locations/.../datasets/.../fhirStores/... resource name.");
        }
    }
}
