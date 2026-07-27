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
            identifierSystems);
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
}
