using System.Globalization;

namespace UnifyEmpi.Hl7v2;

public static class Hl7AcknowledgementFactory
{
    public static string Create(
        Hl7MessageMetadata? metadata,
        Hl7AcknowledgementCode code,
        string diagnostic,
        string? fallbackControlId = null)
    {
        var controlId = metadata?.MessageControlId ?? fallbackControlId ?? "UNKNOWN";
        var version = metadata?.Version ?? "2.5.1";
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmsszzz", CultureInfo.InvariantCulture)
            .Replace(":", string.Empty, StringComparison.Ordinal);
        var acknowledgementId = Guid.CreateVersion7().ToString("N");
        var severity = code == Hl7AcknowledgementCode.AE ? "E" : code == Hl7AcknowledgementCode.AR ? "E" : "I";
        var errorCode = code switch
        {
            Hl7AcknowledgementCode.AA => "0",
            Hl7AcknowledgementCode.AE => "207",
            Hl7AcknowledgementCode.AR => "200",
            _ => "207"
        };
        var text = Escape(diagnostic);
        return string.Join(
            "\r",
            $"MSH|^~\\&|UnifyEMPI|MPI|{Escape(metadata?.SendingApplication ?? string.Empty)}|{Escape(metadata?.SendingFacility ?? string.Empty)}|{timestamp}||ACK^{metadata?.TriggerEvent ?? "A00"}|{acknowledgementId}|P|{version}",
            $"MSA|{code}|{Escape(controlId)}|{text}",
            $"ERR|||{errorCode}^Application internal error^HL70357|{severity}|||{text}",
            string.Empty);
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\E\\", StringComparison.Ordinal)
            .Replace("|", "\\F\\", StringComparison.Ordinal)
            .Replace("^", "\\S\\", StringComparison.Ordinal)
            .Replace("~", "\\R\\", StringComparison.Ordinal)
            .Replace("&", "\\T\\", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
}
