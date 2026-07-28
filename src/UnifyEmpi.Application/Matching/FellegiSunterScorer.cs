using UnifyEmpi.Domain;

namespace UnifyEmpi.Application.Matching;

public static class FellegiSunterScorer
{
    private const double ProbabilityFloor = 1e-12;

    public static FellegiSunterScore Score(
        IReadOnlyList<FieldEvidence> evidence,
        FellegiSunterModel model)
    {
        var prior = ClampProbability(model.PriorMatchProbability);
        var logOdds = Math.Log(prior / (1 - prior));
        var contributions = new Dictionary<string, double>(StringComparer.Ordinal);
        var fields = model.Fields.ToDictionary(static field => field.Field, StringComparer.Ordinal);

        foreach (var item in evidence)
        {
            if (item.IsMissing || !fields.TryGetValue(item.Field, out var field))
            {
                continue;
            }

            var level = Classify(item.Similarity, false);
            var probability = field.Levels.Single(candidate => candidate.Level == level);
            var contribution =
                Math.Log(ClampProbability(probability.MProbability)) -
                Math.Log(ClampProbability(probability.UProbability));
            logOdds += contribution;
            contributions[item.Field] = contribution;
        }

        var probabilityResult = logOdds >= 0
            ? 1 / (1 + Math.Exp(-logOdds))
            : Math.Exp(logOdds) / (1 + Math.Exp(logOdds));
        return new FellegiSunterScore(
            Math.Clamp(probabilityResult, 0, 1),
            logOdds,
            contributions);
    }

    public static FellegiSunterComparisonLevel Classify(
        double similarity,
        bool isMissing)
    {
        _ = isMissing;
        return similarity >= 0.999999
            ? FellegiSunterComparisonLevel.Exact
            : similarity >= 0.85
                ? FellegiSunterComparisonLevel.Strong
                : similarity >= 0.5
                    ? FellegiSunterComparisonLevel.Partial
                    : FellegiSunterComparisonLevel.Different;
    }

    private static double ClampProbability(double value) =>
        Math.Clamp(value, ProbabilityFloor, 1 - ProbabilityFloor);
}

public sealed record FellegiSunterScore(
    double Probability,
    double LogOdds,
    IReadOnlyDictionary<string, double> FieldLogLikelihoodRatios);
