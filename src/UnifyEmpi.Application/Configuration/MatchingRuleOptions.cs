using UnifyEmpi.Application.Normalisation;
using UnifyEmpi.Domain;

namespace UnifyEmpi.Application.Configuration;

public sealed class MatchingRuleOptions
{
    public MatchingWeightOptions? Weights { get; init; }

    public List<string>? BlockingRules { get; init; }

    public List<string>? AuthoritativeIdentifierSystems { get; init; }

    public int MaximumCandidates { get; init; } = 500;

    public int DefaultResultCount { get; init; } = 10;

    public int MaximumResultCount { get; init; } = 50;

    public ComparatorProfileOptions? Comparators { get; init; }

    public FellegiSunterModelOptions? FellegiSunter { get; init; }
}

public sealed class MatchingWeightOptions
{
    public double FamilyName { get; init; } = 0.25;

    public double GivenNames { get; init; } = 0.20;

    public double BirthDate { get; init; } = 0.30;

    public double Address { get; init; } = 0.15;

    public double Telecom { get; init; } = 0.07;

    public double Gender { get; init; } = 0.03;
}

public sealed class ComparatorProfileOptions
{
    public string Version { get; init; } = "comparators-v1";

    public List<string> FamilyName { get; init; } =
        [nameof(StringComparatorKind.JaroWinkler), nameof(StringComparatorKind.Phonetic)];

    public List<string> GivenNames { get; init; } =
        [nameof(StringComparatorKind.JaroWinkler)];

    public double PhoneticMatchFloor { get; init; } = 0.85;

    public double NicknameMatchFloor { get; init; } = 0.92;

    public List<NicknameDictionaryOptions> NicknameDictionaries { get; init; } = [];
}

public sealed class NicknameDictionaryOptions
{
    public string Version { get; init; } = string.Empty;

    public string Culture { get; init; } = string.Empty;

    public Dictionary<string, List<string>> Entries { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class FellegiSunterModelOptions
{
    public string Version { get; init; } = string.Empty;

    public double PriorMatchProbability { get; init; }

    public string? TrainingDatasetDigest { get; init; }

    public List<FellegiSunterFieldOptions> Fields { get; init; } = [];
}

public sealed class FellegiSunterFieldOptions
{
    public string Field { get; init; } = string.Empty;

    public List<FellegiSunterLevelOptions> Levels { get; init; } = [];
}

public sealed class FellegiSunterLevelOptions
{
    public FellegiSunterComparisonLevel? Level { get; init; }

    public double MProbability { get; init; }

    public double UProbability { get; init; }
}

public static class MatchingProfileFactory
{
    public static MatchingProfile Create(
        string version,
        double possibleThreshold,
        double probableThreshold,
        MatchingRuleOptions? options)
    {
        options ??= new MatchingRuleOptions();
        ValidateVersionAndThresholds(version, possibleThreshold, probableThreshold);

        var configuredWeights = options.Weights ?? new MatchingWeightOptions();
        var weights = new MatchingWeights(
            configuredWeights.FamilyName,
            configuredWeights.GivenNames,
            configuredWeights.BirthDate,
            configuredWeights.Address,
            configuredWeights.Telecom,
            configuredWeights.Gender);
        ValidateWeights(weights);

        var blockingRules = ParseBlockingRules(
            options.BlockingRules ?? Enum.GetNames<BlockingRuleKind>().ToList());
        var identifierSystems = ParseIdentifierSystems(
            options.AuthoritativeIdentifierSystems ?? [NhsNumberValidator.IdentifierSystem]);
        ValidateLimits(options);

        return new MatchingProfile(
            version,
            weights,
            possibleThreshold,
            probableThreshold,
            options.MaximumCandidates,
            options.DefaultResultCount,
            options.MaximumResultCount,
            blockingRules,
            identifierSystems)
        {
            Comparators = CreateComparatorProfile(options.Comparators),
            ProbabilityModel = CreateFellegiSunterModel(options.FellegiSunter)
        };
    }

    private static void ValidateVersionAndThresholds(
        string version,
        double possibleThreshold,
        double probableThreshold)
    {
        if (string.IsNullOrWhiteSpace(version) || version.Length > 64)
        {
            throw new InvalidOperationException(
                "MatchingProfileVersion is required and cannot exceed 64 characters.");
        }

        if (!double.IsFinite(possibleThreshold) ||
            !double.IsFinite(probableThreshold) ||
            possibleThreshold is < 0 or > 1 ||
            probableThreshold is < 0 or > 1 ||
            possibleThreshold >= probableThreshold)
        {
            throw new InvalidOperationException(
                "Matching thresholds must be finite values between zero and one, with possible below probable.");
        }
    }

    private static void ValidateWeights(MatchingWeights weights)
    {
        var values = new[]
        {
            weights.FamilyName,
            weights.GivenNames,
            weights.BirthDate,
            weights.Address,
            weights.Telecom,
            weights.Gender
        };
        if (values.Any(static value => !double.IsFinite(value) || value is < 0 or > 1) ||
            weights.Total <= 0)
        {
            throw new InvalidOperationException(
                "Every matching weight must be a finite value from zero to one and at least one weight must be positive.");
        }
    }

    private static HashSet<BlockingRuleKind> ParseBlockingRules(
        List<string>? configuredRules)
    {
        if (configuredRules is null || configuredRules.Count == 0)
        {
            throw new InvalidOperationException(
                "At least one named blocking rule must be configured.");
        }

        var rules = new HashSet<BlockingRuleKind>();
        foreach (var configuredRule in configuredRules)
        {
            if (!Enum.TryParse<BlockingRuleKind>(
                    configuredRule,
                    ignoreCase: true,
                    out var rule) ||
                !Enum.IsDefined(rule))
            {
                throw new InvalidOperationException(
                    $"Unknown blocking rule '{configuredRule}'. Valid rules: {string.Join(", ", Enum.GetNames<BlockingRuleKind>())}.");
            }

            if (!rules.Add(rule))
            {
                throw new InvalidOperationException(
                    $"Blocking rule '{configuredRule}' is configured more than once.");
            }
        }

        return rules;
    }

    private static HashSet<string> ParseIdentifierSystems(
        List<string>? configuredSystems)
    {
        if (configuredSystems is null || configuredSystems.Count == 0)
        {
            throw new InvalidOperationException(
                "At least one authoritative identifier system must be configured.");
        }

        var systems = new HashSet<string>(StringComparer.Ordinal);
        foreach (var configuredSystem in configuredSystems)
        {
            var system = configuredSystem?.Trim();
            if (string.IsNullOrWhiteSpace(system) ||
                !Uri.TryCreate(system, UriKind.Absolute, out _))
            {
                throw new InvalidOperationException(
                    $"Authoritative identifier system '{configuredSystem}' must be an absolute URI.");
            }

            if (!systems.Add(system))
            {
                throw new InvalidOperationException(
                    $"Authoritative identifier system '{system}' is configured more than once.");
            }
        }

        return systems;
    }

    private static void ValidateLimits(MatchingRuleOptions options)
    {
        if (options.MaximumCandidates is < 1 or > 500)
        {
            throw new InvalidOperationException(
                "MaximumCandidates must be between 1 and the provider safety limit of 500.");
        }

        if (options.MaximumResultCount is < 1 or > 100 ||
            options.MaximumResultCount > options.MaximumCandidates)
        {
            throw new InvalidOperationException(
                "MaximumResultCount must be between 1 and 100 and cannot exceed MaximumCandidates.");
        }

        if (options.DefaultResultCount is < 1 ||
            options.DefaultResultCount > options.MaximumResultCount)
        {
            throw new InvalidOperationException(
                "DefaultResultCount must be between 1 and MaximumResultCount.");
        }
    }

    private static ComparatorProfile CreateComparatorProfile(
        ComparatorProfileOptions? options)
    {
        options ??= new ComparatorProfileOptions();
        if (string.IsNullOrWhiteSpace(options.Version) || options.Version.Length > 64)
        {
            throw new InvalidOperationException(
                "Comparator profile Version is required and cannot exceed 64 characters.");
        }

        if (!double.IsFinite(options.PhoneticMatchFloor) ||
            !double.IsFinite(options.NicknameMatchFloor) ||
            options.PhoneticMatchFloor is <= 0 or > 1 ||
            options.NicknameMatchFloor is <= 0 or > 1)
        {
            throw new InvalidOperationException(
                "Comparator similarity floors must be finite values greater than zero and at most one.");
        }

        var family = ParseComparators(options.FamilyName, "FamilyName");
        var given = ParseComparators(options.GivenNames, "GivenNames");
        var dictionaries = new List<NicknameLexicon>();
        var versions = new HashSet<string>(StringComparer.Ordinal);
        var profileAliases = new Dictionary<string, string>(StringComparer.Ordinal);
        var totalEntries = 0;
        foreach (var configured in options.NicknameDictionaries)
        {
            if (string.IsNullOrWhiteSpace(configured.Version) ||
                configured.Version.Length > 64 ||
                !versions.Add(configured.Version) ||
                string.IsNullOrWhiteSpace(configured.Culture) ||
                configured.Culture.Length > 32)
            {
                throw new InvalidOperationException(
                    "Every nickname dictionary needs a unique Version and a Culture.");
            }

            var equivalenceKeys = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in configured.Entries)
            {
                var canonical = IdentityNormaliser.NormaliseWords(entry.Key);
                if (canonical.Length == 0)
                {
                    throw new InvalidOperationException(
                        $"Nickname dictionary '{configured.Version}' contains an empty canonical name.");
                }

                var groupKey = $"{configured.Version}:{canonical}";
                foreach (var value in entry.Value.Prepend(entry.Key))
                {
                    var normalised = IdentityNormaliser.NormaliseWords(value);
                    if (normalised.Length == 0 ||
                        equivalenceKeys.TryGetValue(normalised, out var existing) &&
                        !string.Equals(existing, groupKey, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Nickname '{value}' is empty or belongs to multiple groups in dictionary '{configured.Version}'.");
                    }

                    equivalenceKeys[normalised] = groupKey;
                    if (profileAliases.TryGetValue(normalised, out var profileGroup) &&
                        !string.Equals(profileGroup, groupKey, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Nickname '{value}' belongs to multiple groups in comparator profile '{options.Version}'.");
                    }

                    profileAliases[normalised] = groupKey;
                }
            }

            totalEntries += equivalenceKeys.Count;
            if (totalEntries > 10_000)
            {
                throw new InvalidOperationException(
                    "A comparator profile cannot contain more than 10,000 nickname entries.");
            }

            dictionaries.Add(new NicknameLexicon(
                configured.Version,
                configured.Culture,
                equivalenceKeys));
        }

        if ((family.Contains(StringComparatorKind.Nickname) ||
             given.Contains(StringComparatorKind.Nickname)) &&
            dictionaries.Count == 0)
        {
            throw new InvalidOperationException(
                "The Nickname comparator requires at least one versioned nickname dictionary.");
        }

        return new ComparatorProfile(
            options.Version,
            family,
            given,
            options.PhoneticMatchFloor,
            options.NicknameMatchFloor,
            dictionaries);
    }

    private static List<StringComparatorKind> ParseComparators(
        List<string> configured,
        string field)
    {
        if (configured.Count == 0)
        {
            throw new InvalidOperationException(
                $"At least one {field} comparator must be configured.");
        }

        var result = new List<StringComparatorKind>();
        foreach (var value in configured)
        {
            if (!Enum.TryParse<StringComparatorKind>(value, true, out var comparator) ||
                !Enum.IsDefined(comparator) ||
                !result.AddIfMissing(comparator))
            {
                throw new InvalidOperationException(
                    $"Comparator '{value}' is unknown or duplicated for {field}. Valid comparators: {string.Join(", ", Enum.GetNames<StringComparatorKind>())}.");
            }
        }

        return result;
    }

    private static FellegiSunterModel? CreateFellegiSunterModel(
        FellegiSunterModelOptions? options)
    {
        if (options is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(options.Version) ||
            options.Version.Length > 64 ||
            !double.IsFinite(options.PriorMatchProbability) ||
            options.PriorMatchProbability is <= 0 or >= 1)
        {
            throw new InvalidOperationException(
                "FellegiSunter requires a Version and a prior match probability strictly between zero and one.");
        }

        var expectedFields = new HashSet<string>(
            ["family", "given", "birthDate", "address", "telecom", "gender"],
            StringComparer.Ordinal);
        var fields = new List<FellegiSunterFieldModel>();
        foreach (var configuredField in options.Fields)
        {
            if (!expectedFields.Remove(configuredField.Field))
            {
                throw new InvalidOperationException(
                    $"FellegiSunter field '{configuredField.Field}' is unknown or duplicated.");
            }

            var levels = new List<FellegiSunterLevelProbability>();
            var remainingLevels = Enum.GetValues<FellegiSunterComparisonLevel>().ToHashSet();
            foreach (var configuredLevel in configuredField.Levels)
            {
                if (!configuredLevel.Level.HasValue ||
                    !remainingLevels.Remove(configuredLevel.Level.Value) ||
                    !ValidProbability(configuredLevel.MProbability) ||
                    !ValidProbability(configuredLevel.UProbability))
                {
                    throw new InvalidOperationException(
                        $"FellegiSunter level '{configuredLevel.Level}' is invalid or duplicated for '{configuredField.Field}'.");
                }

                levels.Add(new FellegiSunterLevelProbability(
                    configuredLevel.Level.Value,
                    configuredLevel.MProbability,
                    configuredLevel.UProbability));
            }

            if (remainingLevels.Count > 0 ||
                Math.Abs(levels.Sum(static level => level.MProbability) - 1) > 1e-6 ||
                Math.Abs(levels.Sum(static level => level.UProbability) - 1) > 1e-6)
            {
                throw new InvalidOperationException(
                    $"FellegiSunter probabilities for '{configuredField.Field}' must contain every level and each m/u distribution must sum to one.");
            }

            fields.Add(new FellegiSunterFieldModel(configuredField.Field, levels));
        }

        if (expectedFields.Count > 0)
        {
            throw new InvalidOperationException(
                $"FellegiSunter is missing fields: {string.Join(", ", expectedFields.Order())}.");
        }

        return new FellegiSunterModel(
            options.Version,
            options.PriorMatchProbability,
            fields,
            options.TrainingDatasetDigest);
    }

    private static bool ValidProbability(double value) =>
        double.IsFinite(value) && value is > 0 and < 1;

    private static bool AddIfMissing<T>(this List<T> values, T value)
        where T : notnull
    {
        if (values.Contains(value))
        {
            return false;
        }

        values.Add(value);
        return true;
    }
}
