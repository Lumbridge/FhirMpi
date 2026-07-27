using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

namespace UnifyEmpi.Fhir.R4;

public enum FhirWireFormat
{
    Json,
    Xml
}

public sealed class FhirResourceCodec
{
    private readonly FhirJsonDeserializer _jsonParser = FhirJsonDeserializer.STRICT;
    private readonly FhirXmlDeserializer _xmlParser = FhirXmlDeserializer.STRICT;
    private readonly FhirJsonSerializer _jsonSerialiser = new();
    private readonly FhirXmlSerializer _xmlSerialiser = new();

    public Resource Parse(string payload, FhirWireFormat format) =>
        format switch
        {
            FhirWireFormat.Json => _jsonParser.Deserialize<Resource>(payload),
            FhirWireFormat.Xml => _xmlParser.Deserialize<Resource>(payload),
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };

    public T Parse<T>(string payload, FhirWireFormat format)
        where T : Resource =>
        format switch
        {
            FhirWireFormat.Json => _jsonParser.Deserialize<T>(payload),
            FhirWireFormat.Xml => _xmlParser.Deserialize<T>(payload),
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };

    public string Serialise(Resource resource, FhirWireFormat format) =>
        format switch
        {
            FhirWireFormat.Json => _jsonSerialiser.SerializeToString(resource),
            FhirWireFormat.Xml => _xmlSerialiser.SerializeToString(resource),
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };

    public static FhirWireFormat ParseContentType(string? contentType)
    {
        if (contentType?.Contains("xml", StringComparison.OrdinalIgnoreCase) == true)
        {
            return FhirWireFormat.Xml;
        }

        if (contentType is null ||
            contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            return FhirWireFormat.Json;
        }

        throw new NotSupportedException(
            $"FHIR media type '{contentType}' is not supported. Use application/fhir+json or application/fhir+xml.");
    }

    public static string GetContentType(FhirWireFormat format) =>
        format == FhirWireFormat.Xml ? "application/fhir+xml" : "application/fhir+json";
}
