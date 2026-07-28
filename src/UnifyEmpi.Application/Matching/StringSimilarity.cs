namespace UnifyEmpi.Application.Matching;

public static class StringSimilarity
{
    public static double JaroWinkler(string first, string second)
    {
        if (first.Length == 0 || second.Length == 0)
        {
            return 0;
        }

        if (string.Equals(first, second, StringComparison.Ordinal))
        {
            return 1;
        }

        var matchDistance = Math.Max(first.Length, second.Length) / 2 - 1;
        var firstMatches = new bool[first.Length];
        var secondMatches = new bool[second.Length];
        var matches = 0;

        for (var firstIndex = 0; firstIndex < first.Length; firstIndex++)
        {
            var start = Math.Max(0, firstIndex - matchDistance);
            var end = Math.Min(firstIndex + matchDistance + 1, second.Length);
            for (var secondIndex = start; secondIndex < end; secondIndex++)
            {
                if (secondMatches[secondIndex] || first[firstIndex] != second[secondIndex])
                {
                    continue;
                }

                firstMatches[firstIndex] = true;
                secondMatches[secondIndex] = true;
                matches++;
                break;
            }
        }

        if (matches == 0)
        {
            return 0;
        }

        var transpositions = 0;
        var position = 0;
        for (var firstIndex = 0; firstIndex < first.Length; firstIndex++)
        {
            if (!firstMatches[firstIndex])
            {
                continue;
            }

            while (!secondMatches[position])
            {
                position++;
            }

            if (first[firstIndex] != second[position])
            {
                transpositions++;
            }

            position++;
        }

        var matchCount = (double)matches;
        var jaro = (matchCount / first.Length +
                    matchCount / second.Length +
                    (matchCount - transpositions / 2.0) / matchCount) / 3.0;

        var prefix = 0;
        var prefixLimit = Math.Min(4, Math.Min(first.Length, second.Length));
        while (prefix < prefixLimit && first[prefix] == second[prefix])
        {
            prefix++;
        }

        return jaro + prefix * 0.1 * (1 - jaro);
    }

    public static double TokenJaccard(string first, string second)
    {
        if (first.Length == 0 || second.Length == 0)
        {
            return 0;
        }

        var firstTokens = first.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var secondTokens = second.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var intersection = firstTokens.Count(secondTokens.Contains);
        var union = firstTokens.Count + secondTokens.Count - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }

    public static double NormalisedDamerauLevenshtein(string first, string second)
    {
        if (first.Length == 0 || second.Length == 0)
        {
            return 0;
        }

        if (string.Equals(first, second, StringComparison.Ordinal))
        {
            return 1;
        }

        var previousPrevious = new int[second.Length + 1];
        var previous = Enumerable.Range(0, second.Length + 1).ToArray();
        var current = new int[second.Length + 1];

        for (var firstIndex = 1; firstIndex <= first.Length; firstIndex++)
        {
            current[0] = firstIndex;
            for (var secondIndex = 1; secondIndex <= second.Length; secondIndex++)
            {
                var substitutionCost = first[firstIndex - 1] == second[secondIndex - 1] ? 0 : 1;
                current[secondIndex] = Math.Min(
                    Math.Min(
                        previous[secondIndex] + 1,
                        current[secondIndex - 1] + 1),
                    previous[secondIndex - 1] + substitutionCost);

                if (firstIndex > 1 &&
                    secondIndex > 1 &&
                    first[firstIndex - 1] == second[secondIndex - 2] &&
                    first[firstIndex - 2] == second[secondIndex - 1])
                {
                    current[secondIndex] = Math.Min(
                        current[secondIndex],
                        previousPrevious[secondIndex - 2] + 1);
                }
            }

            (previousPrevious, previous, current) = (previous, current, previousPrevious);
        }

        var maximumLength = Math.Max(first.Length, second.Length);
        return 1 - (double)previous[second.Length] / maximumLength;
    }

    public static double DiceCoefficient(string first, string second)
    {
        if (first.Length == 0 || second.Length == 0)
        {
            return 0;
        }

        if (string.Equals(first, second, StringComparison.Ordinal))
        {
            return 1;
        }

        if (first.Length == 1 || second.Length == 1)
        {
            return 0;
        }

        var firstBigrams = Bigrams(first);
        var secondBigrams = Bigrams(second);
        var intersection = 0;
        foreach (var bigram in firstBigrams.Keys)
        {
            if (secondBigrams.TryGetValue(bigram, out var secondCount))
            {
                intersection += Math.Min(firstBigrams[bigram], secondCount);
            }
        }

        return 2.0 * intersection /
               (firstBigrams.Values.Sum() + secondBigrams.Values.Sum());
    }

    private static Dictionary<string, int> Bigrams(string value)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < value.Length - 1; index++)
        {
            var bigram = value.Substring(index, 2);
            result[bigram] = result.GetValueOrDefault(bigram) + 1;
        }

        return result;
    }
}
