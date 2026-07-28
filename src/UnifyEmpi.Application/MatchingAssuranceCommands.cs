using UnifyEmpi.Domain;

namespace UnifyEmpi.Application;

public sealed record EvaluateGroundTruthCommand(
    string DatasetId,
    IReadOnlyList<GroundTruthPair> Pairs,
    IReadOnlyList<double>? Thresholds = null,
    int MaximumErrorExamples = 25);

public sealed record CalibrateFellegiSunterCommand(
    string DatasetId,
    string ModelVersion,
    IReadOnlyList<GroundTruthPair> Pairs,
    double PriorMatchProbability,
    double Smoothing = 1,
    double ValidationFraction = 0.2,
    double TargetPrecision = 0.99);
