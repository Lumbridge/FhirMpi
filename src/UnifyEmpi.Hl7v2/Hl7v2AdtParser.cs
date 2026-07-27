using System.Globalization;
using NHapi.Base.Parser;
using UnifyEmpi.Application.Normalisation;
using UnifyEmpi.Domain;

namespace UnifyEmpi.Hl7v2;

public sealed class Hl7v2AdtParser
{
    private static readonly HashSet<string> SupportedVersions =
        ["2.3.1", "2.4", "2.5.1"];
    private static readonly HashSet<string> SupportedTriggers =
        ["A01", "A04", "A08", "A28", "A31", "A40", "A47"];
    private static readonly int[] TelecomFields = [13, 14];
    private readonly PipeParser _parser = new();

    public ParsedAdtMessage Parse(string payload, Hl7ListenerBinding binding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        ArgumentNullException.ThrowIfNull(binding);
        var normalised = payload.Replace("\r\n", "\r", StringComparison.Ordinal)
            .Replace('\n', '\r')
            .Trim('\r');
        _ = _parser.Parse(normalised);

        var fields = new Hl7MessageFields(normalised);
        var version = fields.Required("MSH", 12);
        var messageCode = fields.Required("MSH", 9, component: 1);
        var trigger = fields.Required("MSH", 9, component: 2);
        var controlId = fields.Required("MSH", 10);
        if (!SupportedVersions.Contains(version))
        {
            throw new NotSupportedException($"HL7 version '{version}' is not supported.");
        }

        if (messageCode != "ADT" || !SupportedTriggers.Contains(trigger))
        {
            throw new NotSupportedException(
                $"HL7 message '{messageCode}^{trigger}' is not supported.");
        }

        var localId = GetLocalIdentifier(fields, "PID", 3);
        var sourceRecord = new SourceRecordKey(binding.SourceSystem, localId);
        SourceRecordKey? previous = trigger is "A40" or "A47"
            ? new SourceRecordKey(
                binding.SourceSystem,
                GetLocalIdentifier(fields, "MRG", 1))
            : null;
        var metadata = new Hl7MessageMetadata(
            version,
            messageCode,
            trigger,
            fields.Get("MSH", 3),
            fields.Get("MSH", 4),
            controlId);
        return new ParsedAdtMessage(
            metadata,
            sourceRecord,
            MapPatient(fields),
            previous);
    }

    private static IdentityProfile MapPatient(Hl7MessageFields fields) =>
        new(
            MapIdentifiers(fields),
            MapNames(fields),
            ParseDate(fields.Get("PID", 7)),
            MapGender(fields.Get("PID", 8)),
            MapAddresses(fields),
            MapTelecom(fields),
            IsTrue(fields.Get("PID", 30)));

    private static IdentityIdentifier[] MapIdentifiers(Hl7MessageFields fields) =>
        fields.Repetitions("PID", 3)
            .Select(repetition =>
            {
                var value = fields.Component(repetition, 1);
                var authority = fields.Component(repetition, 4);
                var type = fields.Component(repetition, 5);
                var isNhs = string.Equals(type, "NH", StringComparison.OrdinalIgnoreCase) ||
                            authority.Contains("NHS", StringComparison.OrdinalIgnoreCase);
                var system = isNhs
                    ? NhsNumberValidator.IdentifierSystem
                    : ToIdentifierSystem(authority);
                var verified = isNhs && NhsNumberValidator.IsValid(value);
                return new IdentityIdentifier(system, value, verified, verified);
            })
            .Where(static identifier => !string.IsNullOrWhiteSpace(identifier.Value))
            .ToArray();

    private static PersonName[] MapNames(Hl7MessageFields fields) =>
        fields.Repetitions("PID", 5)
            .Select(repetition => new PersonName(
                fields.Component(repetition, 1),
                new[]
                {
                    fields.Component(repetition, 2),
                    fields.Component(repetition, 3)
                }.Where(static value => !string.IsNullOrWhiteSpace(value)).ToArray(),
                MapNameUse(fields.Component(repetition, 7)),
                NullIfEmpty(fields.Component(repetition, 5)),
                NullIfEmpty(fields.Component(repetition, 4))))
            .Where(static name =>
                !string.IsNullOrWhiteSpace(name.Family) || name.Given.Count > 0)
            .ToArray();

    private static PostalAddress[] MapAddresses(Hl7MessageFields fields) =>
        fields.Repetitions("PID", 11)
            .Select(repetition => new PostalAddress(
                new[]
                {
                    fields.Component(repetition, 1),
                    fields.Component(repetition, 2)
                }.Where(static value => !string.IsNullOrWhiteSpace(value)).ToArray(),
                NullIfEmpty(fields.Component(repetition, 3)),
                NullIfEmpty(fields.Component(repetition, 4)),
                NullIfEmpty(fields.Component(repetition, 5)),
                NullIfEmpty(fields.Component(repetition, 6)),
                MapAddressUse(fields.Component(repetition, 7))))
            .Where(static address =>
                address.Lines.Count > 0 || !string.IsNullOrWhiteSpace(address.PostalCode))
            .ToArray();

    private static ContactPoint[] MapTelecom(Hl7MessageFields fields) =>
        TelecomFields
            .SelectMany(field => fields.Repetitions("PID", field))
            .SelectMany(repetition => MapTelecomRepetition(fields, repetition))
            .ToArray();

    private static IEnumerable<ContactPoint> MapTelecomRepetition(
        Hl7MessageFields fields,
        string repetition)
    {
        var email = fields.Component(repetition, 4);
        if (email.Contains('@', StringComparison.Ordinal))
        {
            yield return new ContactPoint(
                ContactPointSystem.Email,
                email,
                NullIfEmpty(fields.Component(repetition, 2)));
        }

        var direct = fields.Component(repetition, 1);
        var area = fields.Component(repetition, 6);
        var local = fields.Component(repetition, 7);
        var phone = !string.IsNullOrWhiteSpace(direct)
            ? direct
            : $"{area}{local}";
        if (!string.IsNullOrWhiteSpace(phone))
        {
            yield return new ContactPoint(
                ContactPointSystem.Phone,
                phone,
                NullIfEmpty(fields.Component(repetition, 2)));
        }
    }

    private static string GetLocalIdentifier(
        Hl7MessageFields fields,
        string segment,
        int field)
    {
        var repetitions = fields.Repetitions(segment, field);
        var value = repetitions.Select(repetition => fields.Component(repetition, 1))
            .FirstOrDefault(static item => !string.IsNullOrWhiteSpace(item));
        return value ?? throw new FormatException($"{segment}-{field} requires a patient identifier.");
    }

    private static string ToIdentifierSystem(string authority)
    {
        if (string.IsNullOrWhiteSpace(authority))
        {
            return "urn:unifyempi:hl7v2:unassigned";
        }

        if (authority.All(character => char.IsDigit(character) || character == '.'))
        {
            return $"urn:oid:{authority}";
        }

        return $"urn:unifyempi:hl7v2:{Uri.EscapeDataString(authority)}";
    }

    private static DateOnly? ParseDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var date = value.Length >= 8 ? value[..8] : value;
        return DateOnly.TryParseExact(
            date,
            "yyyyMMdd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? parsed
            : throw new FormatException("PID-7 contains an invalid birth date.");
    }

    private static AdministrativeGender MapGender(string value) =>
        value.ToUpperInvariant() switch
        {
            "M" => AdministrativeGender.Male,
            "F" => AdministrativeGender.Female,
            "O" => AdministrativeGender.Other,
            _ => AdministrativeGender.Unknown
        };

    private static NameUse MapNameUse(string value) =>
        value.ToUpperInvariant() switch
        {
            "L" => NameUse.Official,
            "A" => NameUse.Usual,
            "N" => NameUse.Nickname,
            "M" => NameUse.Maiden,
            "D" => NameUse.Old,
            _ => NameUse.Unknown
        };

    private static AddressUse MapAddressUse(string value) =>
        value.ToUpperInvariant() switch
        {
            "H" => AddressUse.Home,
            "O" or "B" => AddressUse.Work,
            "C" => AddressUse.Temp,
            _ => AddressUse.Unknown
        };

    private static bool IsTrue(string value) =>
        value is "Y" or "y" or "1";

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
