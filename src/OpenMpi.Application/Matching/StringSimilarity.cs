namespace OpenMpi.Application.Matching;

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
}
