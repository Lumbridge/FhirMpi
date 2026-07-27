using System.Diagnostics;
using System.Diagnostics.Metrics;
using OpenMpi.Domain;

namespace OpenMpi.Application;

internal static class RegistryTelemetry
{
    public const string MeterName = "OpenMpi.Registry";
    public const string ActivitySourceName = "OpenMpi.Registry";

    public static readonly ActivitySource Activities = new(ActivitySourceName);
    private static readonly Meter Meter = new(MeterName);
    private static readonly Histogram<double> MatchLatency =
        Meter.CreateHistogram<double>("openmpi.match.duration", "ms");
    private static readonly Histogram<int> CandidateCount =
        Meter.CreateHistogram<int>("openmpi.match.candidates", "{candidate}");
    private static readonly Counter<long> MatchGrades =
        Meter.CreateCounter<long>("openmpi.match.grade", "{match}");
    private static readonly Counter<long> ReviewCases =
        Meter.CreateCounter<long>("openmpi.review.created", "{case}");
    private static readonly Counter<long> ReviewDecisions =
        Meter.CreateCounter<long>("openmpi.review.decisions", "{decision}");

    public static void RecordMatch(
        long startedTimestamp,
        MatchResponse response,
        TenantId tenantId)
    {
        var tags = new TagList
        {
            { "tenant.id", tenantId.Value },
            { "matching.profile", response.MatchingProfileVersion }
        };
        MatchLatency.Record(
            Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds,
            tags);
        CandidateCount.Record(response.CandidateCount, tags);
        foreach (var grade in response.Matches
                     .GroupBy(static match => match.Grade)
                     .Select(static group => (Grade: group.Key, Count: group.LongCount())))
        {
            MatchGrades.Add(
                grade.Count,
                new TagList
                {
                    { "tenant.id", tenantId.Value },
                    { "match.grade", grade.Grade.ToString().ToLowerInvariant() }
                });
        }
    }

    public static void RecordReviewsCreated(int count, TenantId tenantId)
    {
        if (count > 0)
        {
            ReviewCases.Add(count, new TagList { { "tenant.id", tenantId.Value } });
        }
    }

    public static void RecordReviewDecision(ReviewDecision decision, TenantId tenantId) =>
        ReviewDecisions.Add(
            1,
            new TagList
            {
                { "tenant.id", tenantId.Value },
                { "review.decision", decision.ToString().ToLowerInvariant() }
            });
}
