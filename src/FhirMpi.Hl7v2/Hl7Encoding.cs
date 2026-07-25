using System.Globalization;
using System.Text;

namespace FhirMpi.Hl7v2;

internal sealed class Hl7Encoding
{
    private readonly char _field;
    private readonly char _component;
    private readonly char _repetition;
    private readonly char _escape;
    private readonly char _subcomponent;

    public Hl7Encoding(string payload)
    {
        if (payload.Length < 8 || !payload.StartsWith("MSH", StringComparison.Ordinal))
        {
            throw new FormatException("The message must begin with an MSH segment.");
        }

        _field = payload[3];
        var separators = payload.AsSpan(4, 4);
        _component = separators[0];
        _repetition = separators[1];
        _escape = separators[2];
        _subcomponent = separators[3];
    }

    public char FieldSeparator => _field;

    public char ComponentSeparator => _component;

    public char RepetitionSeparator => _repetition;

    public string Unescape(string value)
    {
        if (!value.Contains(_escape, StringComparison.Ordinal))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != _escape)
            {
                builder.Append(value[index]);
                continue;
            }

            var end = value.IndexOf(_escape, index + 1);
            if (end < 0)
            {
                throw new FormatException("An HL7 escape sequence is not terminated.");
            }

            var token = value[(index + 1)..end];
            builder.Append(token switch
            {
                "F" => _field,
                "S" => _component,
                "R" => _repetition,
                "E" => _escape,
                "T" => _subcomponent,
                ".br" => '\n',
                _ when token.StartsWith('X') => DecodeHex(token[1..]),
                _ => throw new FormatException($"Unsupported HL7 escape sequence '{token}'.")
            });
            index = end;
        }

        return builder.ToString();
    }

    private static string DecodeHex(string value)
    {
        if (value.Length == 0 || value.Length % 2 != 0)
        {
            throw new FormatException("An HL7 hexadecimal escape has an invalid length.");
        }

        var bytes = Convert.FromHexString(value);
        return Encoding.UTF8.GetString(bytes);
    }
}

internal sealed class Hl7MessageFields
{
    private readonly Dictionary<string, List<string[]>> _segments =
        new(StringComparer.Ordinal);
    private readonly Hl7Encoding _encoding;

    public Hl7MessageFields(string payload)
    {
        _encoding = new Hl7Encoding(payload);
        foreach (var segmentText in payload.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = segmentText.Split(_encoding.FieldSeparator);
            if (fields[0].Length != 3)
            {
                throw new FormatException("An HL7 segment name must contain three characters.");
            }

            if (!_segments.TryGetValue(fields[0], out var repetitions))
            {
                repetitions = [];
                _segments.Add(fields[0], repetitions);
            }

            repetitions.Add(fields);
        }
    }

    public string Required(
        string segment,
        int field,
        int repetition = 0,
        int component = 1)
    {
        var value = Get(segment, field, repetition, component);
        return string.IsNullOrWhiteSpace(value)
            ? throw new FormatException($"{segment}-{field} is required.")
            : value;
    }

    public string Get(
        string segment,
        int field,
        int repetition = 0,
        int component = 1)
    {
        if (!_segments.TryGetValue(segment, out var segments) || segments.Count == 0)
        {
            return string.Empty;
        }

        var fields = segments[0];
        var index = segment == "MSH"
            ? field switch
            {
                1 => -1,
                _ => field - 1
            }
            : field;
        if (index == -1)
        {
            return _encoding.FieldSeparator.ToString(CultureInfo.InvariantCulture);
        }

        if (index >= fields.Length)
        {
            return string.Empty;
        }

        var repeats = fields[index].Split(_encoding.RepetitionSeparator);
        if (repetition >= repeats.Length)
        {
            return string.Empty;
        }

        var components = repeats[repetition].Split(_encoding.ComponentSeparator);
        return component <= components.Length
            ? _encoding.Unescape(components[component - 1])
            : string.Empty;
    }

    public IReadOnlyList<string> Repetitions(string segment, int field)
    {
        if (!_segments.TryGetValue(segment, out var segments) || segments.Count == 0)
        {
            return [];
        }

        var index = segment == "MSH" ? field - 1 : field;
        return index < segments[0].Length
            ? segments[0][index].Split(_encoding.RepetitionSeparator)
            : [];
    }

    public string Component(string repetition, int component)
    {
        var components = repetition.Split(_encoding.ComponentSeparator);
        return component <= components.Length
            ? _encoding.Unescape(components[component - 1])
            : string.Empty;
    }
}
