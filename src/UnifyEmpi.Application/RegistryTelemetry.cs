using System.Diagnostics;
using System.Diagnostics.Metrics;
using UnifyEmpi.Domain;

namespace UnifyEmpi.Application;

internal static class RegistryTelemetry
{
    public const string MeterName = "UnifyEmpi.Registry";
    public const string ActivitySourceName = "UnifyEmpi.Registry";

    public static readonly ActivitySource Activities = new(ActivitySourceName);
    private static readonly Meter Meter = new(MeterName);
    private static readonly Histogram<double> MatchLatency =
        Meter.CreateHistogram<double>("unifyempi.match.duration", "ms");
    private static readonly Histogram<int> CandidateCount =
        Meter.CreateHistogram<int>("unifyempi.match.candidates", "{candidate}");
    private static readonly Counter<long> MatchGrades =
        Meter.CreateCounter<long>("unifyempi.match.grade", "{match}");
    private static readonly Counter<long> ReviewCases =
        Meter.CreateCounter<long>("unifyempi.review.created", "{case}");
    private static readonly Counter<long> ReviewDecisions =
        Meter.CreateCounter<long>("unifyempi.review.decisions", "{decision}");
    private static readonly Counter<long> MaintenanceJobs =
        Meter.CreateCounter<long>("unifyempi.maintenance.jobs", "{job}");
    private static readonly Counter<long> MaintenanceItems =
        Meter.CreateCounter<long>("unifyempi.maintenance.items", "{item}");

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

    public static void RecordMaintenanceStarted(RegistryMaintenanceJob job) =>
        MaintenanceJobs.Add(
            1,
            new TagList
            {
                { "tenant.id", job.TenantId.Value },
                { "maintenance.kind", job.Kind.ToString().ToLowerInvariant() },
                { "maintenance.trigger", job.Trigger.ToString().ToLowerInvariant() }
            });

    public static void RecordMaintenanceBatch(RegistryMaintenanceJob job, int count)
    {
        if (count <= 0)
        {
            return;
        }

        MaintenanceItems.Add(
            count,
            new TagList
            {
                { "tenant.id", job.TenantId.Value },
                { "maintenance.kind", job.Kind.ToString().ToLowerInvariant() },
                { "maintenance.phase", job.Phase.ToString().ToLowerInvariant() }
            });
    }
}
