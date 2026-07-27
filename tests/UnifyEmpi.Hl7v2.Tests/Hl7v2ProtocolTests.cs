using System.Buffers;
using System.Text;
using UnifyEmpi.Application;
using UnifyEmpi.Application.Configuration;
using UnifyEmpi.Domain;
using UnifyEmpi.Storage.InMemory;
using Xunit;

namespace UnifyEmpi.Hl7v2.Tests;

public sealed class Hl7v2ProtocolTests
{
    public static TheoryData<string, string> SupportedVersionsAndTriggers =>
        new()
        {
            { "2.3.1", "A01" },
            { "2.4", "A04" },
            { "2.5.1", "A08" },
            { "2.5.1", "A28" },
            { "2.5.1", "A31" },
            { "2.5.1", "A40" },
            { "2.5.1", "A47" }
        };

    [Theory]
    [MemberData(nameof(SupportedVersionsAndTriggers))]
    public void ParsesSupportedAdtMessages(string version, string trigger)
    {
        var parser = new Hl7v2AdtParser();
        var parsed = parser.Parse(
            Message(version, trigger),
            new Hl7ListenerBinding(
                new TenantId("tenant-a"),
                new SourceSystemId("pas"),
                "listener"));

        Assert.Equal(version, parsed.Metadata.Version);
        Assert.Equal(trigger, parsed.Metadata.TriggerEvent);
        Assert.Equal("12345", parsed.SourceRecord.LocalId);
        Assert.Equal("Smith", parsed.Profile.Names[0].Family);
        Assert.Equal(new DateOnly(1980, 1, 2), parsed.Profile.BirthDate);
        if (trigger is "A40" or "A47")
        {
            Assert.Equal("99999", parsed.PreviousSourceRecord!.Value.LocalId);
        }
    }

    [Fact]
    public void ListenerBindingOverridesUntrustedMshIdentity()
    {
        var parsed = new Hl7v2AdtParser().Parse(
            Message("2.5.1", "A08"),
            new Hl7ListenerBinding(
                new TenantId("trusted-tenant"),
                new SourceSystemId("trusted-source"),
                "listener"));

        Assert.Equal("trusted-source", parsed.SourceRecord.SourceSystem.Value);
        Assert.Equal("EVILAPP", parsed.Metadata.SendingApplication);
    }

    [Fact]
    public void RejectsUnsupportedTrigger()
    {
        Assert.Throws<NotSupportedException>(() =>
            new Hl7v2AdtParser().Parse(
                Message("2.5.1", "A03"),
                new Hl7ListenerBinding(
                    new TenantId("tenant-a"),
                    new SourceSystemId("pas"),
                    "listener")));
    }

    [Fact]
    public void MllpDecoderHandlesFragmentedAndCoalescedFrames()
    {
        var first = Frame("one");
        var second = Frame("two");
        var incomplete = new ReadOnlySequence<byte>(first[..3]);
        Assert.False(MllpFraming.TryReadFrame(ref incomplete, 1024, out _));

        var combined = new ReadOnlySequence<byte>(first.Concat(second).ToArray());
        Assert.True(MllpFraming.TryReadFrame(ref combined, 1024, out var firstPayload));
        Assert.Equal("one", Encoding.UTF8.GetString(firstPayload));
        Assert.True(MllpFraming.TryReadFrame(ref combined, 1024, out var secondPayload));
        Assert.Equal("two", Encoding.UTF8.GetString(secondPayload));
    }

    [Fact]
    public void MllpDecoderRejectsOversizedMessages()
    {
        var bytes = new byte[20];
        bytes[0] = MllpFraming.StartBlock;
        var sequence = new ReadOnlySequence<byte>(bytes);

        Assert.Throws<InvalidDataException>(() =>
            MllpFraming.TryReadFrame(ref sequence, 10, out _));
    }

    [Fact]
    public async Task DuplicateMessagesReplayTheOriginalAcknowledgement()
    {
        var processor = CreateProcessor();
        var binding = Binding();
        var payload = Message("2.5.1", "A08");

        var first = await processor.ProcessAsync(payload, binding, CancellationToken.None);
        var replay = await processor.ProcessAsync(payload, binding, CancellationToken.None);

        Assert.Equal(Hl7AcknowledgementCode.AA, first.Code);
        Assert.Equal(Hl7AcknowledgementCode.AA, replay.Code);
        Assert.False(first.WasReplay);
        Assert.True(replay.WasReplay);
        Assert.Equal(first.Acknowledgement, replay.Acknowledgement);
    }

    [Fact]
    public async Task ReusedControlIdWithDifferentPayloadIsRejected()
    {
        var processor = CreateProcessor();
        var binding = Binding();
        var original = Message("2.5.1", "A08");
        var changed = original.Replace("Smith^Alex", "Jones^Alex", StringComparison.Ordinal);

        var first = await processor.ProcessAsync(original, binding, CancellationToken.None);
        var rejected = await processor.ProcessAsync(changed, binding, CancellationToken.None);

        Assert.Equal(Hl7AcknowledgementCode.AA, first.Code);
        Assert.Equal(Hl7AcknowledgementCode.AR, rejected.Code);
        Assert.Contains("MSA|AR|MSG0001", rejected.Acknowledgement, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthoritativeIdentityChangeIsReceiptedAfterMergeAndReplays()
    {
        var store = new InMemoryIdentityRegistryStore();
        var processor = CreateProcessor(store);
        var binding = Binding();
        var oldIdentity = Message("2.5.1", "A28")
            .Replace("MSG0001", "MSG-SEED", StringComparison.Ordinal)
            .Replace("12345^^^LOCAL", "99999^^^LOCAL", StringComparison.Ordinal);
        var identityChange = Message("2.5.1", "A40")
            .Replace("9434765919^^^NHS", "9999999999^^^NHS", StringComparison.Ordinal);

        Assert.Equal(
            Hl7AcknowledgementCode.AA,
            (await processor.ProcessAsync(oldIdentity, binding, CancellationToken.None)).Code);
        var changed = await processor.ProcessAsync(
            identityChange,
            binding,
            CancellationToken.None);
        var replay = await processor.ProcessAsync(
            identityChange,
            binding,
            CancellationToken.None);

        Assert.Equal(Hl7AcknowledgementCode.AA, changed.Code);
        Assert.True(replay.WasReplay);
        Assert.Equal(changed.Acknowledgement, replay.Acknowledgement);

        var actor = new ActorContext(
            binding.TenantId,
            "test",
            binding.SourceSystem,
            new HashSet<string>(),
            "test");
        var previous = await store.GetSourcePatientAsync(
            actor,
            new SourceRecordKey(binding.SourceSystem, "99999"),
            CancellationToken.None);
        var survivor = await store.GetSourcePatientAsync(
            actor,
            new SourceRecordKey(binding.SourceSystem, "12345"),
            CancellationToken.None);
        Assert.Equal(survivor?.EnterpriseId, previous?.EnterpriseId);
    }

    private static byte[] Frame(string value) =>
        [MllpFraming.StartBlock, .. Encoding.UTF8.GetBytes(value),
            MllpFraming.EndBlock, MllpFraming.CarriageReturn];

    private static Hl7ListenerBinding Binding() =>
        new(
            new TenantId("tenant-a"),
            new SourceSystemId("pas"),
            "listener");

    private static Hl7v2IngestionProcessor CreateProcessor(
        InMemoryIdentityRegistryStore? store = null)
    {
        store ??= new InMemoryIdentityRegistryStore();
        var configuration = DefaultTenantConfigurationFactory.CreateDevelopment(
            "tenant-a",
            "pas");
        var provider = new StaticTenantConfigurationProvider(
            new Dictionary<TenantId, TenantMatchingConfiguration>
            {
                [configuration.TenantId] = configuration
            });
        return new Hl7v2IngestionProcessor(
            new Hl7v2AdtParser(),
            new RegistryService(store, provider, TimeProvider.System));
    }

    private static string Message(string version, string trigger) =>
        string.Join(
            "\r",
            $"MSH|^~\\&|EVILAPP|EVILFAC|MPI|MPI|20260725120000||ADT^{trigger}|MSG0001|P|{version}",
            "PID|1||12345^^^LOCAL^MR~9434765919^^^NHS^NH||Smith^Alex||19800102|F|||1 High Street^^London^^SW1A 2AA^GB^H||+442079460018^PRN^PH^^alex@example.test",
            "MRG|99999^^^LOCAL^MR",
            string.Empty);
}
