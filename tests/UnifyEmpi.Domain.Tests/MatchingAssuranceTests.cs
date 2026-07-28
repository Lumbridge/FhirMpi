using UnifyEmpi.Application;
using UnifyEmpi.Application.Configuration;
using UnifyEmpi.Domain;
using UnifyEmpi.Storage.Abstractions;
using UnifyEmpi.Storage.InMemory;
using Xunit;

namespace UnifyEmpi.Domain.Tests;

public sealed class MatchingAssuranceTests
{
    [Fact]
    public async Task GroundTruthEvaluationReportsBlockingAndClassificationQuality()
    {
        var fixture = await CreateFixtureAsync();

        var report = await fixture.Service.EvaluateAsync(
            fixture.Admin,
            new EvaluateGroundTruthCommand(
                "governed-clerical-labels-v1",
                fixture.Pairs,
                [0.5, 0.8],
                10),
            CancellationToken.None);

        Assert.Equal(20, report.LabelCount);
        Assert.Equal(10, report.MatchCount);
        Assert.Equal(1, report.BlockingRecall);
        var probable = Assert.Single(
            report.Thresholds,
            static value => Math.Abs(value.Threshold - 0.8) < 0.0001);
        Assert.Equal(1, probable.Precision);
        Assert.Equal(1, probable.Recall);
        Assert.Equal(64, report.DatasetDigest.Length);
        Assert.Empty(report.MisclassifiedPairs);
    }

    [Fact]
    public async Task CalibrationUsesHeldOutLabelsAndReturnsConfigurableModel()
    {
        var fixture = await CreateFixtureAsync();

        var report = await fixture.Service.CalibrateAsync(
            fixture.Admin,
            new CalibrateFellegiSunterCommand(
                "governed-clerical-labels-v1",
                "fs-governed-v1",
                fixture.Pairs,
                0.01,
                ValidationFraction: 0.2,
                TargetPrecision: 0.95),
            CancellationToken.None);

        Assert.Equal(8, report.TrainingMatchCount);
        Assert.Equal(8, report.TrainingNonMatchCount);
        Assert.Equal(2, report.ValidationMatchCount);
        Assert.Equal(2, report.ValidationNonMatchCount);
        Assert.Equal(6, report.Model.Fields.Count);
        Assert.All(report.Model.Fields, static field =>
        {
            Assert.Equal(1, field.Levels.Sum(static level => level.MProbability), 10);
            Assert.Equal(1, field.Levels.Sum(static level => level.UProbability), 10);
        });
        Assert.NotNull(report.RecommendedPossibleThreshold);
        Assert.NotNull(report.RecommendedProbableThreshold);
        Assert.True(
            report.RecommendedPossibleThreshold < report.RecommendedProbableThreshold);
        Assert.InRange(report.ValidationBrierScore, 0, 1);
    }

    [Fact]
    public async Task CalibrationRejectsUnrepresentativeSingleClassLabels()
    {
        var fixture = await CreateFixtureAsync();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await fixture.Service.CalibrateAsync(
                fixture.Admin,
                new CalibrateFellegiSunterCommand(
                    "matches-only",
                    "fs-invalid",
                    fixture.Pairs.Where(static pair => pair.IsMatch).ToArray(),
                    0.01),
                CancellationToken.None));
    }

    private static async Task<Fixture> CreateFixtureAsync()
    {
        var tenant = new TenantId("tenant-a");
        var leftSource = new SourceSystemId("left");
        var rightSource = new SourceSystemId("right");
        var profile = MatchingProfile.UkDefault;
        var configuration = new TenantMatchingConfiguration(
            tenant,
            profile,
            [new BlockingKeySecret("v1", Enumerable.Repeat((byte)7, 32).ToArray(), true)],
            new Dictionary<SourceSystemId, int>
            {
                [leftSource] = 100,
                [rightSource] = 100
            },
            new HashSet<SourceSystemId> { leftSource, rightSource });
        var provider = new StaticTenantConfigurationProvider(
            new Dictionary<TenantId, TenantMatchingConfiguration>
            {
                [tenant] = configuration
            });
        var store = new InMemoryIdentityRegistryStore();
        var admin = new ActorContext(
            tenant,
            "assurance-test",
            null,
            new HashSet<string>(StringComparer.Ordinal) { "mpi.admin" },
            Guid.NewGuid().ToString("D"));
        var records = new List<SourcePatientRecord>();
        var pairs = new List<GroundTruthPair>();
        var now = DateTimeOffset.Parse(
            "2026-07-28T10:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture);
        for (var index = 0; index < 10; index++)
        {
            var enterpriseId = EnterpriseId.New();
            var leftKey = new SourceRecordKey(leftSource, $"match-{index}-left");
            var rightKey = new SourceRecordKey(rightSource, $"match-{index}-right");
            var personProfile = Profile(
                $"Family{index}",
                $"Given{index}",
                new DateOnly(1970 + index, 1, index + 1),
                $"LS{index} 1AA");
            records.Add(Record(leftKey, enterpriseId, personProfile, now));
            records.Add(Record(rightKey, enterpriseId, personProfile, now));
            pairs.Add(new GroundTruthPair(leftKey, rightKey, true));

            var nonMatchLeft = new SourceRecordKey(leftSource, $"nonmatch-{index}-left");
            var nonMatchRight = new SourceRecordKey(rightSource, $"nonmatch-{index}-right");
            records.Add(Record(
                nonMatchLeft,
                EnterpriseId.New(),
                Profile(
                    $"Alpha{index}",
                    $"Person{index}",
                    new DateOnly(1940 + index, 2, index + 1),
                    $"CF{index} 2BB"),
                now));
            records.Add(Record(
                nonMatchRight,
                EnterpriseId.New(),
                Profile(
                    $"Zulu{index}",
                    $"Other{index}",
                    new DateOnly(1990 + index, 9, index + 1),
                    $"SA{index} 9ZZ"),
                now));
            pairs.Add(new GroundTruthPair(nonMatchLeft, nonMatchRight, false));
        }

        await store.CommitAsync(
            admin,
            new RegistryMutation(records, [], [], [], [], []),
            CancellationToken.None);
        return new Fixture(
            new MatchingAssuranceService(store, provider, TimeProvider.System),
            admin,
            pairs);
    }

    private static SourcePatientRecord Record(
        SourceRecordKey key,
        EnterpriseId enterpriseId,
        IdentityProfile profile,
        DateTimeOffset now) =>
        new(
            key,
            $"resource-{key.LocalId}",
            enterpriseId,
            profile,
            100,
            now,
            1);

    private static IdentityProfile Profile(
        string family,
        string given,
        DateOnly birthDate,
        string postcode) =>
        new(
            [],
            [new PersonName(family, [given])],
            birthDate,
            AdministrativeGender.Unknown,
            [new PostalAddress(["1 High Street"], null, null, postcode, "GB")],
            []);

    private sealed record Fixture(
        MatchingAssuranceService Service,
        ActorContext Admin,
        IReadOnlyList<GroundTruthPair> Pairs);
}
