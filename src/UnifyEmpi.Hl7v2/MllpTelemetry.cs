using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace UnifyEmpi.Hl7v2;

internal static class MllpTelemetry
{
    public const string MeterName = "UnifyEmpi.Hl7v2";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Acknowledgements =
        Meter.CreateCounter<long>("unifyempi.mllp.acknowledgements", "{ack}");

    public static Hl7ProcessingResult Record(
        Hl7ProcessingResult result,
        Hl7ListenerBinding binding)
    {
        Acknowledgements.Add(
            1,
            new TagList
            {
                { "tenant.id", binding.TenantId.Value },
                { "source.system", binding.SourceSystem.Value },
                { "mllp.ack", result.Code.ToString() },
                { "mllp.replay", result.WasReplay }
            });
        return result;
    }
}
