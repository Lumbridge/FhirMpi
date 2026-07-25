using System.Security.Cryptography;
using System.Text;
using FhirMpi.Application;
using FhirMpi.Domain;

namespace FhirMpi.Hl7v2;

public sealed class Hl7v2IngestionProcessor(
    Hl7v2AdtParser parser,
    RegistryService registry)
{
    private static readonly HashSet<string> ProcessingScopes =
        new(StringComparer.Ordinal)
        {
            "system/Patient.*",
            "mpi.review",
            "mpi.admin"
        };

    public async ValueTask<Hl7ProcessingResult> ProcessAsync(
        string payload,
        Hl7ListenerBinding binding,
        CancellationToken cancellationToken)
    {
        ParsedAdtMessage? message = null;
        try
        {
            message = parser.Parse(payload, binding);
            var actor = new ActorContext(
                binding.TenantId,
                binding.ActorId,
                binding.SourceSystem,
                ProcessingScopes,
                Guid.CreateVersion7().ToString("N"));
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
            var idempotencyKey = string.Join(
                "|",
                binding.TenantId.Value,
                binding.SourceSystem.Value,
                message.Metadata.SendingApplication,
                message.Metadata.SendingFacility,
                message.Metadata.MessageControlId);
            var acknowledgement = Hl7AcknowledgementFactory.Create(
                message.Metadata,
                Hl7AcknowledgementCode.AA,
                "Message committed.");
            var existingReceipt = await registry.GetIngestionReceiptAsync(
                actor,
                idempotencyKey,
                cancellationToken);
            if (existingReceipt is not null)
            {
                if (!string.Equals(
                        existingReceipt.PayloadDigest,
                        digest,
                        StringComparison.Ordinal))
                {
                    throw new IdempotencyConflictException(idempotencyKey);
                }

                return MllpTelemetry.Record(new Hl7ProcessingResult(
                    Hl7AcknowledgementCode.AA,
                    existingReceipt.Response ?? acknowledgement,
                    true), binding);
            }

            var isAuthoritativeIdentityChange =
                message.PreviousSourceRecord is { } previous &&
                previous != message.SourceRecord;
            var result = await registry.UpsertPatientAsync(
                actor,
                new UpsertPatientCommand(
                    message.SourceRecord,
                    message.Profile,
                    isAuthoritativeIdentityChange ? null : idempotencyKey,
                    isAuthoritativeIdentityChange ? null : digest,
                    isAuthoritativeIdentityChange ? null : acknowledgement),
                cancellationToken);
            if (result.WasIdempotent)
            {
                return MllpTelemetry.Record(new Hl7ProcessingResult(
                    Hl7AcknowledgementCode.AA,
                    result.ReplayResponse ?? acknowledgement,
                    true), binding);
            }

            if (isAuthoritativeIdentityChange)
            {
                await registry.MergeSourceRecordsAsync(
                    actor,
                    message.PreviousSourceRecord!.Value,
                    message.SourceRecord,
                    $"HL7v2 {message.Metadata.TriggerEvent} authoritative identity event",
                    cancellationToken);
                await registry.RecordIngestionReceiptAsync(
                    actor,
                    new IngestionReceipt(
                        idempotencyKey,
                        digest,
                        "accepted",
                        acknowledgement,
                        DateTimeOffset.UtcNow),
                    cancellationToken);
            }

            return MllpTelemetry.Record(new Hl7ProcessingResult(
                Hl7AcknowledgementCode.AA,
                acknowledgement,
                false), binding);
        }
        catch (Exception exception) when (
            exception is FormatException or
                NotSupportedException or
                NHapi.Base.HL7Exception or
                IdempotencyConflictException)
        {
            var acknowledgement = Hl7AcknowledgementFactory.Create(
                message?.Metadata,
                Hl7AcknowledgementCode.AR,
                Sanitise(exception.Message));
            return MllpTelemetry.Record(new Hl7ProcessingResult(
                Hl7AcknowledgementCode.AR,
                acknowledgement,
                false), binding);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            var acknowledgement = Hl7AcknowledgementFactory.Create(
                message?.Metadata,
                Hl7AcknowledgementCode.AE,
                "The message could not be committed and may be retried.");
            return MllpTelemetry.Record(new Hl7ProcessingResult(
                Hl7AcknowledgementCode.AE,
                acknowledgement,
                false), binding);
        }
    }

    private static string Sanitise(string value) =>
        value.Length <= 256 ? value : value[..256];
}
