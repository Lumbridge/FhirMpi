using System.IO.Pipelines;
using System.Text;

namespace OpenMpi.Hl7v2;

public sealed class MllpConnectionProcessor(
    Hl7v2IngestionProcessor ingestionProcessor,
    int maximumMessageBytes = 2 * 1024 * 1024)
{
    public async Task ProcessAsync(
        Stream stream,
        Hl7ListenerBinding binding,
        CancellationToken cancellationToken)
    {
        var reader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
        var writer = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await reader.ReadAsync(cancellationToken);
                var buffer = read.Buffer;
                while (MllpFraming.TryReadFrame(
                           ref buffer,
                           maximumMessageBytes,
                           out var frame))
                {
                    var payload = Encoding.UTF8.GetString(frame);
                    var result = await ingestionProcessor.ProcessAsync(
                        payload,
                        binding,
                        cancellationToken);
                    await MllpFraming.WriteFrameAsync(
                        writer,
                        result.Acknowledgement,
                        cancellationToken);
                }

                reader.AdvanceTo(buffer.Start, buffer.End);
                if (read.IsCompleted)
                {
                    break;
                }
            }
        }
        finally
        {
            await reader.CompleteAsync();
            await writer.CompleteAsync();
        }
    }
}
