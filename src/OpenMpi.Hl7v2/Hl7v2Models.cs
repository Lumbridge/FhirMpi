using OpenMpi.Domain;

namespace OpenMpi.Hl7v2;

public sealed record Hl7ListenerBinding(
    TenantId TenantId,
    SourceSystemId SourceSystem,
    string ActorId);

public sealed record Hl7MessageMetadata(
    string Version,
    string MessageCode,
    string TriggerEvent,
    string SendingApplication,
    string SendingFacility,
    string MessageControlId);

public sealed record ParsedAdtMessage(
    Hl7MessageMetadata Metadata,
    SourceRecordKey SourceRecord,
    IdentityProfile Profile,
    SourceRecordKey? PreviousSourceRecord);

public enum Hl7AcknowledgementCode
{
    AA,
    AE,
    AR
}

public sealed record Hl7ProcessingResult(
    Hl7AcknowledgementCode Code,
    string Acknowledgement,
    bool WasReplay);
