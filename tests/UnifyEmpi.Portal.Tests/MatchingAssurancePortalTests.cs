using System.Net;
using Xunit;

namespace UnifyEmpi.Portal.Tests;

public sealed class MatchingAssurancePortalTests : IClassFixture<FailingOverviewFactory>
{
    private readonly HttpClient _client;

    public MatchingAssurancePortalTests(FailingOverviewFactory factory) =>
        _client = factory.CreateClient();

    [Fact]
    public async Task AdminAssuranceWorkbenchRendersGovernanceBoundary()
    {
        var response = await _client.GetAsync("/assurance", CancellationToken.None);
        var html = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Matching assurance workbench", html, StringComparison.Ordinal);
        Assert.Contains("Governance boundary", html, StringComparison.Ordinal);
        Assert.Contains("Run held-out calibration", html, StringComparison.Ordinal);
        Assert.Contains("Tab-separated labels", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdminMaintenanceWorkbenchExplainsRealJobPhases()
    {
        var response = await _client.GetAsync("/maintenance", CancellationToken.None);
        var html = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Maintenance operations", html, StringComparison.Ordinal);
        Assert.Contains("Start re-index", html, StringComparison.Ordinal);
        Assert.Contains("Start reconciliation", html, StringComparison.Ordinal);
        Assert.Contains("Run either operation to see its phases", html, StringComparison.Ordinal);
        Assert.Contains(
            "The visualisation uses persisted job state rather than simulated progress.",
            html,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GroundTruthParserAcceptsHeaderAndBothLabelClasses()
    {
        const string labels = """

            ﻿leftSource	leftLocalId	rightSource	rightLocalId	isMatch
            pas	P-1	wds	W-1	match
            pas	P-2	wds	W-2	non-match
            """;

        var pairs = GroundTruthTsvParser.Parse(labels);

        Assert.Equal(2, pairs.Count);
        Assert.True(pairs[0].IsMatch);
        Assert.False(pairs[1].IsMatch);
        Assert.Equal("pas/P-1", pairs[0].Left.ToString());
    }

    [Fact]
    public void GroundTruthParserRejectsSingleClassEvidence()
    {
        const string labels = """
            pas	P-1	wds	W-1	match
            pas	P-2	wds	W-2	match
            """;

        var exception = Assert.Throws<FormatException>(
            () => GroundTruthTsvParser.Parse(labels));

        Assert.Contains("match and one non-match", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SyntheticAssuranceDatasetSupportsHeldOutCalibration()
    {
        var pairs = GroundTruthTsvParser.Parse(SyntheticAssuranceDataset.Labels);

        Assert.Equal(
            SyntheticAssuranceDataset.MatchPairCount +
            SyntheticAssuranceDataset.NonMatchPairCount,
            pairs.Count);
        Assert.Equal(
            SyntheticAssuranceDataset.MatchPairCount,
            pairs.Count(static pair => pair.IsMatch));
        Assert.Equal(
            SyntheticAssuranceDataset.NonMatchPairCount,
            pairs.Count(static pair => !pair.IsMatch));
        Assert.Equal(
            pairs.Count,
            pairs.Select(static pair =>
                    string.Compare(
                        pair.Left.ToString(),
                        pair.Right.ToString(),
                        StringComparison.Ordinal) <= 0
                        ? $"{pair.Left}|{pair.Right}"
                        : $"{pair.Right}|{pair.Left}")
                .Distinct(StringComparer.Ordinal)
                .Count());
    }
}
