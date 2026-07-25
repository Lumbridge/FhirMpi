using System.Diagnostics;
using System.Diagnostics.Metrics;
using FhirMpi.Domain;

namespace FhirMpi.Application;

internal static class RegistryTelemetry
{
    public const string MeterName = "FhirMpi.Registry";
    public const string ActivitySourceName = "FhirMpi.Registry";

    public static readonly ActivitySource Activities = new(ActivitySourceName);
    private static readonly Meter Meter = new(MeterName);
    private static readonly Histogram<double> MatchLatency =
        Meter.CreateHistogram<double>("fhir_mpi.match.duration", "ms");
    private static readonly Histogram<int> CandidateCount =
        Meter.CreateHistogram<int>("fhir_mpi.match.candidates", "{candidate}");
    private static readonly Counter<long> MatchGrades =
        Meter.CreateCounter<long>("fhir_mpi.match.grade", "{match}");
    private static readonly Counter<long> ReviewCases =
        Meter.CreateCounter<long>("fhir_mpi.review.created", "{case}");
    private static readonly Counter<long> ReviewDecisions =
        Meter.CreateCounter<long>("fhir_mpi.review.decisions", "{decision}");

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
