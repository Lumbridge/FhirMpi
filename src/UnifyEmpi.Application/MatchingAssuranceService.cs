using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UnifyEmpi.Application.Configuration;
using UnifyEmpi.Application.Matching;
using UnifyEmpi.Application.Normalisation;
using UnifyEmpi.Domain;
using UnifyEmpi.Storage.Abstractions;

namespace UnifyEmpi.Application;

public sealed class MatchingAssuranceService(
    IIdentityRegistryStore store,
    ITenantConfigurationProvider configurations,
    TimeProvider timeProvider)
{
    private static readonly FellegiSunterComparisonLevel[] CalibrationLevels =
        Enum.GetValues<FellegiSunterComparisonLevel>();
    private static readonly string[] CalibratedFields =
        ["family", "given", "birthDate", "address", "telecom", "gender"];

    public async ValueTask<GroundTruthEvaluationReport> EvaluateAsync(
        ActorContext context,
        EvaluateGroundTruthCommand command,
        CancellationToken cancellationToken)
    {
        RequireAdmin(context);
        ValidateDataset(command.DatasetId, command.Pairs);
        if (command.MaximumErrorExamples is < 0 or > 100)
        {
            throw new ArgumentException(
                "MaximumErrorExamples must be between zero and 100.");
        }

        var configuration = await configurations.GetConfigurationAsync(
            context.TenantId,
            cancellationToken);
        var observations = await LoadObservationsAsync(
            context,
            command.Pairs,
            configuration,
            cancellationToken);
        var digest = DatasetDigest(observations);
        var thresholds = NormaliseThresholds(
            command.Thresholds,
            configuration.MatchingProfile);
        var report = BuildEvaluationReport(
            command.DatasetId,
            digest,
            observations,
            configuration.MatchingProfile,
            thresholds,
            command.MaximumErrorExamples,
            timeProvider.GetUtcNow());
        await RecordAuditAsync(
            context,
            "matching-ground-truth-evaluated",
            $"Evaluated labelled dataset digest {digest} with {observations.Count} pairs.",
            cancellationToken);
        RegistryTelemetry.RecordMatchingAssurance(
            context.TenantId,
            "evaluation",
            observations.Count);
        return report;
    }

    public async ValueTask<FellegiSunterCalibrationReport> CalibrateAsync(
        ActorContext context,
        CalibrateFellegiSunterCommand command,
        CancellationToken cancellationToken)
    {
        RequireAdmin(context);
        ValidateDataset(command.DatasetId, command.Pairs);
        ValidateCalibrationCommand(command);

        var configuration = await configurations.GetConfigurationAsync(
            context.TenantId,
            cancellationToken);
        var observations = await LoadObservationsAsync(
            context,
            command.Pairs,
            configuration,
            cancellationToken);
        var matches = observations.Where(static value => value.IsMatch).ToArray();
        var nonMatches = observations.Where(static value => !value.IsMatch).ToArray();
        if (matches.Length < 10 || nonMatches.Length < 10)
        {
            throw new ArgumentException(
                "Fellegi-Sunter calibration requires at least ten labelled matches and ten labelled non-matches.");
        }

        var (trainingMatches, validationMatches) = SplitClass(
            matches,
            command.ValidationFraction);
        var (trainingNonMatches, validationNonMatches) = SplitClass(
            nonMatches,
            command.ValidationFraction);
        var training = trainingMatches.Concat(trainingNonMatches).ToArray();
        var validation = validationMatches.Concat(validationNonMatches).ToArray();
        var digest = DatasetDigest(observations);
        var model = FitModel(
            command.ModelVersion,
            command.PriorMatchProbability,
            command.Smoothing,
            digest,
            training);

        var validationScores = validation
            .Select(observation => new ScoredLabel(
                FellegiSunterScorer.Score(observation.Result.Evidence, model).Probability,
                observation.IsMatch))
            .ToArray();
        var thresholdCandidates = Enumerable.Range(1, 99)
            .Select(static value => value / 100.0)
            .ToArray();
        var validationMetrics = thresholdCandidates
            .Select(threshold => CalculateMetrics(validationScores, threshold))
            .ToArray();
        var recommendedProbable = validationMetrics
            .Where(metric =>
                metric.Precision >= command.TargetPrecision &&
                metric.TruePositives + metric.FalsePositives > 0)
            .OrderBy(static metric => metric.Threshold)
            .Select(static metric => (double?)metric.Threshold)
            .FirstOrDefault();
        var f1Threshold = validationMetrics
            .Where(static metric => metric.F1Score.HasValue)
            .OrderByDescending(static metric => metric.F1Score)
            .ThenBy(static metric => metric.Threshold)
            .Select(static metric => (double?)metric.Threshold)
            .FirstOrDefault();
        var recommendedPossible =
            recommendedProbable.HasValue && f1Threshold.HasValue
                ? Math.Min(f1Threshold.Value, recommendedProbable.Value / 2)
                : f1Threshold;
        var brier = validationScores.Average(static value =>
            Math.Pow(value.Score - (value.IsMatch ? 1 : 0), 2));
        var logLoss = validationScores.Average(static value =>
        {
            var probability = Math.Clamp(value.Score, 1e-12, 1 - 1e-12);
            return value.IsMatch ? -Math.Log(probability) : -Math.Log(1 - probability);
        });
        var selectedThresholds = new[]
            {
                recommendedPossible,
                recommendedProbable,
                configuration.MatchingProfile.PossibleThreshold,
                configuration.MatchingProfile.ProbableThreshold
            }
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .Distinct()
            .Order()
            .Select(threshold => CalculateMetrics(validationScores, threshold))
            .ToArray();
        var now = timeProvider.GetUtcNow();
        var report = new FellegiSunterCalibrationReport(
            command.DatasetId,
            digest,
            model,
            trainingMatches.Length,
            trainingNonMatches.Length,
            validationMatches.Length,
            validationNonMatches.Length,
            brier,
            logLoss,
            selectedThresholds,
            recommendedPossible,
            recommendedProbable,
            command.TargetPrecision,
            now);
        await RecordAuditAsync(
            context,
            "fellegi-sunter-calibrated",
            $"Calibrated model '{command.ModelVersion}' from labelled dataset digest {digest}; the model was reported but not activated.",
            cancellationToken);
        RegistryTelemetry.RecordMatchingAssurance(
            context.TenantId,
            "calibration",
            observations.Count);
        return report;
    }

    private async ValueTask<IReadOnlyList<PairObservation>> LoadObservationsAsync(
        ActorContext context,
        IReadOnlyList<GroundTruthPair> pairs,
        TenantMatchingConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var observations = new List<PairObservation>(pairs.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in pairs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateReference(pair.Left);
            ValidateReference(pair.Right);
            var canonicalPair = CanonicalPairKey(pair.Left, pair.Right);
            if (!seen.Add(canonicalPair))
            {
                throw new ArgumentException(
                    $"Ground-truth pair '{canonicalPair}' is duplicated or labelled more than once.");
            }

            if (pair.Left == pair.Right)
            {
                throw new ArgumentException("A ground-truth pair cannot compare a record with itself.");
            }

            var left = await store.GetSourcePatientAsync(
                context,
                pair.Left,
                cancellationToken) ??
                       throw new RegistryNotFoundException("SourcePatient", pair.Left.ToString());
            var right = await store.GetSourcePatientAsync(
                context,
                pair.Right,
                cancellationToken) ??
                        throw new RegistryNotFoundException("SourcePatient", pair.Right.ToString());
            var candidate = new CanonicalPatient(
                right.EnterpriseId,
                right.Profile,
                [right.Key],
                [],
                right.SourceTrust,
                right.LastUpdated,
                right.LastUpdated,
                right.Version);
            var result = WeightedIdentityMatcher.Match(
                IdentityNormaliser.Normalise(left.Profile),
                candidate,
                configuration.MatchingProfile);
            var leftKeys = TryBlockingKeys(left.Profile, configuration);
            var rightKeys = TryBlockingKeys(right.Profile, configuration);
            observations.Add(new PairObservation(
                pair,
                pair.IsMatch,
                result,
                leftKeys is not null && rightKeys is not null,
                leftKeys is not null &&
                rightKeys is not null &&
                leftKeys.Intersect(rightKeys).Any(),
                StablePairHash(canonicalPair)));
        }

        return observations;
    }

    private static IReadOnlyList<BlockingKey>? TryBlockingKeys(
        IdentityProfile profile,
        TenantMatchingConfiguration configuration)
    {
        try
        {
            return BlockingKeyGenerator.Generate(
                IdentityNormaliser.Normalise(profile),
                configuration);
        }
        catch (InsufficientIdentityDataException)
        {
            return null;
        }
    }

    private static GroundTruthEvaluationReport BuildEvaluationReport(
        string datasetId,
        string digest,
        IReadOnlyList<PairObservation> observations,
        MatchingProfile profile,
        IReadOnlyList<double> thresholds,
        int maximumErrorExamples,
        DateTimeOffset now)
    {
        var scored = observations
            .Select(static observation => new ScoredLabel(
                observation.HasHardConflict ? -1 : observation.Result.Score,
                observation.IsMatch,
                observation.Result.Grade == MatchGrade.Certain))
            .ToArray();
        var metrics = thresholds
            .Select(threshold => CalculateMetrics(scored, threshold))
            .ToArray();
        var matchCount = observations.Count(static value => value.IsMatch);
        var probableMetric = metrics.MinBy(metric =>
            Math.Abs(metric.Threshold - profile.ProbableThreshold))!;
        var misclassified = observations
            .Where(observation =>
            {
                var predicted =
                    !observation.HasHardConflict &&
                    (observation.Result.Grade == MatchGrade.Certain ||
                     observation.Result.Score >= probableMetric.Threshold);
                return predicted != observation.IsMatch;
            })
            .OrderByDescending(static observation =>
                Math.Abs(observation.Result.Score - 0.5))
            .Take(maximumErrorExamples)
            .Select(static observation => new MisclassifiedPair(
                observation.Pair.Left,
                observation.Pair.Right,
                observation.IsMatch,
                observation.Result.Score,
                observation.Result.Grade,
                observation.SharedBlockingKey,
                observation.HasHardConflict))
            .ToArray();
        var fields = CalibratedFields.Select(field =>
        {
            var matchEvidence = observations
                .Where(static value => value.IsMatch)
                .Select(value => value.Result.Evidence.Single(item => item.Field == field))
                .Where(static value => !value.IsMissing)
                .ToArray();
            var nonMatchEvidence = observations
                .Where(static value => !value.IsMatch)
                .Select(value => value.Result.Evidence.Single(item => item.Field == field))
                .Where(static value => !value.IsMissing)
                .ToArray();
            return new FieldDiscriminationReport(
                field,
                matchEvidence.Length,
                nonMatchEvidence.Length,
                matchEvidence.Length == 0
                    ? null
                    : matchEvidence.Average(static value => value.Similarity),
                nonMatchEvidence.Length == 0
                    ? null
                    : nonMatchEvidence.Average(static value => value.Similarity));
        }).ToArray();

        return new GroundTruthEvaluationReport(
            datasetId,
            digest,
            profile.Version,
            profile.Comparators.Version,
            profile.ProbabilityModel is null ? "weighted-similarity" : "fellegi-sunter",
            observations.Count,
            matchCount,
            observations.Count - matchCount,
            observations.Count(static value => value.IsMatch && value.BothBlockingEligible),
            observations.Count(static value => value.IsMatch && value.SharedBlockingKey),
            matchCount == 0
                ? null
                : (double)observations.Count(static value => value.IsMatch && value.SharedBlockingKey) /
                  matchCount,
            metrics,
            fields,
            misclassified,
            now);
    }

    private static FellegiSunterModel FitModel(
        string version,
        double prior,
        double smoothing,
        string digest,
        IReadOnlyList<PairObservation> training)
    {
        var fields = CalibratedFields.Select(field =>
        {
            var matchEvidence = training
                .Where(static value => value.IsMatch)
                .Select(value => value.Result.Evidence.Single(item => item.Field == field))
                .Where(static value => !value.IsMissing)
                .ToArray();
            var nonMatchEvidence = training
                .Where(static value => !value.IsMatch)
                .Select(value => value.Result.Evidence.Single(item => item.Field == field))
                .Where(static value => !value.IsMissing)
                .ToArray();
            var levels = CalibrationLevels.Select(level =>
            {
                var matchCount = matchEvidence.Count(value =>
                    FellegiSunterScorer.Classify(value.Similarity, false) == level);
                var nonMatchCount = nonMatchEvidence.Count(value =>
                    FellegiSunterScorer.Classify(value.Similarity, false) == level);
                return new FellegiSunterLevelProbability(
                    level,
                    (matchCount + smoothing) /
                    (matchEvidence.Length + smoothing * CalibrationLevels.Length),
                    (nonMatchCount + smoothing) /
                    (nonMatchEvidence.Length + smoothing * CalibrationLevels.Length));
            }).ToArray();
            return new FellegiSunterFieldModel(field, levels);
        }).ToArray();
        return new FellegiSunterModel(version, prior, fields, digest);
    }

    private static ClassificationMetrics CalculateMetrics(
        IReadOnlyList<ScoredLabel> labels,
        double threshold)
    {
        var truePositives = labels.Count(value =>
            value.IsMatch && (value.IsCertain || value.Score >= threshold));
        var falsePositives = labels.Count(value =>
            !value.IsMatch && (value.IsCertain || value.Score >= threshold));
        var trueNegatives = labels.Count(value =>
            !value.IsMatch && !value.IsCertain && value.Score < threshold);
        var falseNegatives = labels.Count(value =>
            value.IsMatch && !value.IsCertain && value.Score < threshold);
        var precision = Divide(truePositives, truePositives + falsePositives);
        var recall = Divide(truePositives, truePositives + falseNegatives);
        var specificity = Divide(trueNegatives, trueNegatives + falsePositives);
        var negativePredictiveValue = Divide(trueNegatives, trueNegatives + falseNegatives);
        double? f1 = precision.HasValue && recall.HasValue && precision + recall > 0
            ? 2 * precision * recall / (precision + recall)
            : null;
        var denominator = Math.Sqrt(
            (double)(truePositives + falsePositives) *
            (truePositives + falseNegatives) *
            (trueNegatives + falsePositives) *
            (trueNegatives + falseNegatives));
        double? mcc = denominator == 0
            ? null
            : (truePositives * (double)trueNegatives -
               falsePositives * (double)falseNegatives) / denominator;
        return new ClassificationMetrics(
            threshold,
            truePositives,
            falsePositives,
            trueNegatives,
            falseNegatives,
            precision,
            recall,
            specificity,
            negativePredictiveValue,
            f1,
            mcc,
            WilsonInterval(truePositives, truePositives + falsePositives),
            WilsonInterval(truePositives, truePositives + falseNegatives));
    }

    private static (PairObservation[] Training, PairObservation[] Validation) SplitClass(
        IReadOnlyList<PairObservation> observations,
        double validationFraction)
    {
        var ordered = observations.OrderBy(static value => value.StableHash, StringComparer.Ordinal).ToArray();
        var validationCount = Math.Clamp(
            (int)Math.Round(ordered.Length * validationFraction, MidpointRounding.AwayFromZero),
            2,
            ordered.Length - 5);
        return (ordered[validationCount..], ordered[..validationCount]);
    }

    private static double[] NormaliseThresholds(
        IReadOnlyList<double>? configured,
        MatchingProfile profile)
    {
        var values = configured is null || configured.Count == 0
            ? Enumerable.Range(0, 21).Select(static value => value / 20.0)
            : configured;
        var result = values
            .Append(profile.PossibleThreshold)
            .Append(profile.ProbableThreshold)
            .Distinct()
            .Order()
            .ToArray();
        if (result.Length > 101 ||
            result.Any(static value => !double.IsFinite(value) || value is < 0 or > 1))
        {
            throw new ArgumentException(
                "Evaluation thresholds must contain at most 101 unique finite values from zero to one.");
        }

        return result;
    }

    private async ValueTask RecordAuditAsync(
        ActorContext context,
        string action,
        string reason,
        CancellationToken cancellationToken)
    {
        var audit = new AuditRecord(
            Guid.CreateVersion7(),
            action,
            context.ActorId,
            "success",
            reason,
            null,
            null,
            timeProvider.GetUtcNow(),
            context.CorrelationId);
        await store.CommitAsync(
            context,
            new RegistryMutation([], [], [], [], [audit], []),
            cancellationToken);
    }

    private static void ValidateDataset(
        string datasetId,
        IReadOnlyList<GroundTruthPair> pairs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetId);
        ArgumentNullException.ThrowIfNull(pairs);
        if (datasetId.Length > 128 ||
            datasetId.Any(char.IsControl) ||
            pairs.Count is < 2 or > 10_000)
        {
            throw new ArgumentException(
                "DatasetId must be at most 128 non-control characters and datasets must contain 2-10,000 labelled pairs.");
        }

        if (!pairs.Any(static pair => pair.IsMatch) ||
            !pairs.Any(static pair => !pair.IsMatch))
        {
            throw new ArgumentException(
                "Ground-truth evaluation requires both match and non-match labels.");
        }
    }

    private static void ValidateCalibrationCommand(CalibrateFellegiSunterCommand command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ModelVersion);
        if (command.ModelVersion.Length > 64 ||
            !double.IsFinite(command.PriorMatchProbability) ||
            command.PriorMatchProbability is <= 0 or >= 1 ||
            !double.IsFinite(command.Smoothing) ||
            command.Smoothing is <= 0 or > 100 ||
            !double.IsFinite(command.ValidationFraction) ||
            command.ValidationFraction is < 0.1 or > 0.5 ||
            !double.IsFinite(command.TargetPrecision) ||
            command.TargetPrecision is <= 0 or > 1)
        {
            throw new ArgumentException(
                "Calibration requires a model version, a prior strictly between zero and one, smoothing in (0,100], validation fraction from 0.1-0.5, and target precision in (0,1].");
        }
    }

    private static void ValidateReference(SourceRecordKey reference)
    {
        if (string.IsNullOrWhiteSpace(reference.LocalId) ||
            reference.LocalId.Length > 256 ||
            reference.LocalId.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Ground-truth local record identifiers must be 1-256 non-control characters.");
        }
    }

    private static void RequireAdmin(ActorContext context)
    {
        if (!context.HasScope("mpi.admin"))
        {
            throw new RegistryAuthorisationException(
                "Ground-truth evaluation and model calibration require mpi.admin.");
        }
    }

    private static double? Divide(int numerator, int denominator) =>
        denominator == 0 ? null : (double)numerator / denominator;

    private static ConfidenceInterval? WilsonInterval(int successes, int count)
    {
        if (count == 0)
        {
            return null;
        }

        const double z = 1.959963984540054;
        var proportion = (double)successes / count;
        var zSquared = z * z;
        var denominator = 1 + zSquared / count;
        var centre = (proportion + zSquared / (2 * count)) / denominator;
        var margin = z / denominator * Math.Sqrt(
            proportion * (1 - proportion) / count +
            zSquared / (4.0 * count * count));
        return new ConfidenceInterval(
            Math.Max(0, centre - margin),
            Math.Min(1, centre + margin));
    }

    private static string DatasetDigest(IReadOnlyList<PairObservation> observations)
    {
        var content = string.Join(
            '\n',
            observations
                .OrderBy(static value => CanonicalPairKey(
                    value.Pair.Left,
                    value.Pair.Right), StringComparer.Ordinal)
                .Select(static value =>
                    $"{CanonicalPairKey(value.Pair.Left, value.Pair.Right)}|{(value.IsMatch ? '1' : '0')}"));
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(content)))
            .ToLowerInvariant();
    }

    private static string CanonicalPairKey(SourceRecordKey left, SourceRecordKey right)
    {
        var first = left.ToString();
        var second = right.ToString();
        return string.Compare(first, second, StringComparison.Ordinal) <= 0
            ? $"{first}|{second}"
            : $"{second}|{first}";
    }

    private static string StablePairHash(string canonicalPair) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPair)));

    private sealed record PairObservation(
        GroundTruthPair Pair,
        bool IsMatch,
        MatchResult Result,
        bool BothBlockingEligible,
        bool SharedBlockingKey,
        string StableHash)
    {
        public bool HasHardConflict => Result.HasHardConflict;
    }

    private readonly record struct ScoredLabel(
        double Score,
        bool IsMatch,
        bool IsCertain = false);
}
