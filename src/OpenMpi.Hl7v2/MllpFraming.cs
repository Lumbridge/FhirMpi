using System.Buffers;
using System.IO.Pipelines;
using System.Text;

namespace OpenMpi.Hl7v2;

public static class MllpFraming
{
    public const byte StartBlock = 0x0b;
    public const byte EndBlock = 0x1c;
    public const byte CarriageReturn = 0x0d;

    public static bool TryReadFrame(
        ref ReadOnlySequence<byte> buffer,
        int maximumMessageBytes,
        out ReadOnlySequence<byte> payload)
    {
        var reader = new SequenceReader<byte>(buffer);
        if (!reader.TryAdvanceTo(StartBlock, advancePastDelimiter: true))
        {
            if (buffer.Length > maximumMessageBytes)
            {
                throw new InvalidDataException("MLLP data did not contain a start block.");
            }

            payload = default;
            buffer = buffer.Slice(buffer.End);
            return false;
        }

        var payloadStart = reader.Position;
        if (!reader.TryAdvanceTo(EndBlock, advancePastDelimiter: false))
        {
            if (buffer.Slice(payloadStart).Length > maximumMessageBytes)
            {
                throw new InvalidDataException("The MLLP message exceeds the configured size limit.");
            }

            payload = default;
            buffer = buffer.Slice(payloadStart);
            return false;
        }

        var endBlockPosition = reader.Position;
        reader.Advance(1);
        if (!reader.TryRead(out var terminator) || terminator != CarriageReturn)
        {
            throw new InvalidDataException("An MLLP end block must be followed by carriage return.");
        }

        payload = buffer.Slice(payloadStart, endBlockPosition);
        if (payload.Length > maximumMessageBytes)
        {
            throw new InvalidDataException("The MLLP message exceeds the configured size limit.");
        }

        buffer = buffer.Slice(reader.Position);
        return true;
    }

    public static async ValueTask WriteFrameAsync(
        PipeWriter writer,
        string payload,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        var memory = writer.GetMemory(bytes.Length + 3);
        memory.Span[0] = StartBlock;
        bytes.CopyTo(memory.Span[1..]);
        memory.Span[bytes.Length + 1] = EndBlock;
        memory.Span[bytes.Length + 2] = CarriageReturn;
        writer.Advance(bytes.Length + 3);
        await writer.FlushAsync(cancellationToken);
    }
}
