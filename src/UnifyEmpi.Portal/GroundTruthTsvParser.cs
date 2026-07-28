using UnifyEmpi.Domain;

namespace UnifyEmpi.Portal;

public static class GroundTruthTsvParser
{
    private const int MaximumPairs = 10_000;

    public static IReadOnlyList<GroundTruthPair> Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new FormatException(
                "Paste at least one match and one non-match label.");
        }

        var result = new List<GroundTruthPair>();
        var lineNumber = 0;
        var contentLineNumber = 0;
        foreach (var rawLine in value.Split('\n'))
        {
            lineNumber++;
            var line = rawLine.TrimEnd('\r').TrimStart('\uFEFF');
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            contentLineNumber++;
            var columns = line.Split('\t');
            if (contentLineNumber == 1 &&
                columns.Length > 0 &&
                string.Equals(columns[0].Trim(), "leftSource", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (columns.Length != 5)
            {
                throw new FormatException(
                    $"Line {lineNumber} must contain five tab-separated columns.");
            }

            bool isMatch;
            var label = columns[4].Trim();
            if (label.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                label.Equals("match", StringComparison.OrdinalIgnoreCase) ||
                label == "1")
            {
                isMatch = true;
            }
            else if (label.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                     label.Equals("nonmatch", StringComparison.OrdinalIgnoreCase) ||
                     label.Equals("non-match", StringComparison.OrdinalIgnoreCase) ||
                     label == "0")
            {
                isMatch = false;
            }
            else
            {
                throw new FormatException(
                    $"Line {lineNumber} label must be match/non-match, true/false, or 1/0.");
            }

            result.Add(new GroundTruthPair(
                new SourceRecordKey(
                    new SourceSystemId(columns[0].Trim()),
                    RequireLocalId(columns[1], lineNumber)),
                new SourceRecordKey(
                    new SourceSystemId(columns[2].Trim()),
                    RequireLocalId(columns[3], lineNumber)),
                isMatch));
            if (result.Count > MaximumPairs)
            {
                throw new FormatException(
                    $"A workbench submission cannot exceed {MaximumPairs:N0} labelled pairs.");
            }
        }

        if (result.Count < 2 ||
            !result.Any(static pair => pair.IsMatch) ||
            !result.Any(static pair => !pair.IsMatch))
        {
            throw new FormatException(
                "Labels must contain at least one match and one non-match.");
        }

        return result;
    }

    private static string RequireLocalId(string value, int lineNumber)
    {
        var result = value.Trim();
        if (result.Length == 0 ||
            result.Length > 256 ||
            result.Any(char.IsControl))
        {
            throw new FormatException(
                $"Line {lineNumber} contains an invalid local record identifier.");
        }

        return result;
    }
}
