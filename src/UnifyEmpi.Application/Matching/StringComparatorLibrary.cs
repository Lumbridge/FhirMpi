using UnifyEmpi.Application.Normalisation;
using UnifyEmpi.Domain;

namespace UnifyEmpi.Application.Matching;

public static class StringComparatorLibrary
{
    public static StringComparisonResult Compare(
        string first,
        string second,
        IReadOnlyList<StringComparatorKind> comparators,
        ComparatorProfile profile,
        string? firstPhonetic = null,
        string? secondPhonetic = null)
    {
        if (first.Length == 0 || second.Length == 0)
        {
            return new StringComparisonResult(0, "missing", null);
        }

        var best = new StringComparisonResult(0, "none", null);
        foreach (var comparator in comparators)
        {
            var current = comparator switch
            {
                StringComparatorKind.Exact => new StringComparisonResult(
                    string.Equals(first, second, StringComparison.Ordinal) ? 1 : 0,
                    "exact",
                    null),
                StringComparatorKind.JaroWinkler => new StringComparisonResult(
                    StringSimilarity.JaroWinkler(first, second),
                    "jaro-winkler",
                    null),
                StringComparatorKind.NormalisedDamerauLevenshtein => new StringComparisonResult(
                    StringSimilarity.NormalisedDamerauLevenshtein(first, second),
                    "normalised-damerau-levenshtein",
                    null),
                StringComparatorKind.DiceCoefficient => new StringComparisonResult(
                    StringSimilarity.DiceCoefficient(first, second),
                    "dice-coefficient",
                    null),
                StringComparatorKind.Phonetic => ComparePhonetic(
                    first,
                    second,
                    profile,
                    firstPhonetic,
                    secondPhonetic),
                StringComparatorKind.Nickname => CompareNickname(first, second, profile),
                _ => throw new InvalidOperationException(
                    $"Unsupported string comparator '{comparator}'.")
            };

            if (current.Similarity > best.Similarity)
            {
                best = current;
            }
        }

        return best;
    }

    private static StringComparisonResult ComparePhonetic(
        string first,
        string second,
        ComparatorProfile profile,
        string? firstPhonetic,
        string? secondPhonetic)
    {
        var firstCode = firstPhonetic ?? PhoneticEncoder.Encode(first);
        var secondCode = secondPhonetic ?? PhoneticEncoder.Encode(second);
        return firstCode.Length > 0 &&
               string.Equals(firstCode, secondCode, StringComparison.Ordinal)
            ? new StringComparisonResult(
                profile.PhoneticMatchFloor,
                "phonetic",
                $"shared-code:{firstCode}")
            : new StringComparisonResult(0, "phonetic", null);
    }

    private static StringComparisonResult CompareNickname(
        string first,
        string second,
        ComparatorProfile profile)
    {
        foreach (var dictionary in profile.NicknameDictionaries)
        {
            if (dictionary.EquivalenceKeys.TryGetValue(first, out var firstKey) &&
                dictionary.EquivalenceKeys.TryGetValue(second, out var secondKey) &&
                string.Equals(firstKey, secondKey, StringComparison.Ordinal))
            {
                return new StringComparisonResult(
                    profile.NicknameMatchFloor,
                    "nickname",
                    $"{dictionary.Culture}/{dictionary.Version}");
            }
        }

        return new StringComparisonResult(0, "nickname", null);
    }
}

public readonly record struct StringComparisonResult(
    double Similarity,
    string Comparator,
    string? Detail);
