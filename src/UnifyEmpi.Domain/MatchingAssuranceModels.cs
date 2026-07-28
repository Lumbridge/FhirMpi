namespace UnifyEmpi.Domain;

public enum StringComparatorKind
{
    Exact,
    JaroWinkler,
    NormalisedDamerauLevenshtein,
    DiceCoefficient,
    Phonetic,
    Nickname
}

public sealed record NicknameLexicon(
    string Version,
    string Culture,
    IReadOnlyDictionary<string, string> EquivalenceKeys);

public sealed record ComparatorProfile(
    string Version,
    IReadOnlyList<StringComparatorKind> FamilyNameComparators,
    IReadOnlyList<StringComparatorKind> GivenNameComparators,
    double PhoneticMatchFloor,
    double NicknameMatchFloor,
    IReadOnlyList<NicknameLexicon> NicknameDictionaries)
{
    public static ComparatorProfile Default { get; } = new(
        "comparators-v1",
        [StringComparatorKind.JaroWinkler, StringComparatorKind.Phonetic],
        [StringComparatorKind.JaroWinkler],
        0.85,
        0.92,
        []);
}

public enum FellegiSunterComparisonLevel
{
    Exact,
    Strong,
    Partial,
    Different
}

public sealed record FellegiSunterLevelProbability(
    FellegiSunterComparisonLevel Level,
    double MProbability,
    double UProbability);

public sealed record FellegiSunterFieldModel(
    string Field,
    IReadOnlyList<FellegiSunterLevelProbability> Levels);

public sealed record FellegiSunterModel(
    string Version,
    double PriorMatchProbability,
    IReadOnlyList<FellegiSunterFieldModel> Fields,
    string? TrainingDatasetDigest = null);

public sealed record GroundTruthPair(
    SourceRecordKey Left,
    SourceRecordKey Right,
    bool IsMatch);

public sealed record ClassificationMetrics(
    double Threshold,
    int TruePositives,
    int FalsePositives,
    int TrueNegatives,
    int FalseNegatives,
    double? Precision,
    double? Recall,
    double? Specificity,
    double? NegativePredictiveValue,
    double? F1Score,
    double? MatthewsCorrelationCoefficient,
    ConfidenceInterval? Precision95,
    ConfidenceInterval? Recall95);

public sealed record ConfidenceInterval(double Lower, double Upper);

public sealed record FieldDiscriminationReport(
    string Field,
    int MatchObserved,
    int NonMatchObserved,
    double? MeanMatchSimilarity,
    double? MeanNonMatchSimilarity);

public sealed record MisclassifiedPair(
    SourceRecordKey Left,
    SourceRecordKey Right,
    bool ExpectedMatch,
    double Score,
    MatchGrade Grade,
    bool SharedBlockingKey,
    bool HasHardConflict);

public sealed record GroundTruthEvaluationReport(
    string DatasetId,
    string DatasetDigest,
    string MatchingProfileVersion,
    string ComparatorProfileVersion,
    string ScoreMethod,
    int LabelCount,
    int MatchCount,
    int NonMatchCount,
    int BlockingEligibleMatchCount,
    int BlockingRetrievedMatchCount,
    double? BlockingRecall,
    IReadOnlyList<ClassificationMetrics> Thresholds,
    IReadOnlyList<FieldDiscriminationReport> Fields,
    IReadOnlyList<MisclassifiedPair> MisclassifiedPairs,
    DateTimeOffset GeneratedAt);

public sealed record FellegiSunterCalibrationReport(
    string DatasetId,
    string DatasetDigest,
    FellegiSunterModel Model,
    int TrainingMatchCount,
    int TrainingNonMatchCount,
    int ValidationMatchCount,
    int ValidationNonMatchCount,
    double ValidationBrierScore,
    double ValidationLogLoss,
    IReadOnlyList<ClassificationMetrics> ValidationThresholds,
    double? RecommendedPossibleThreshold,
    double? RecommendedProbableThreshold,
    double TargetPrecision,
    DateTimeOffset GeneratedAt);
