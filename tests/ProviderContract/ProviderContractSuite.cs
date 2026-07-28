using UnifyEmpi.Domain;
using UnifyEmpi.Storage.Abstractions;
using Xunit;

namespace UnifyEmpi.Storage.Testing;

#pragma warning disable xUnit1031 // Contract providers used here complete their ValueTasks synchronously.
public abstract class ProviderContractSuite
{
    protected abstract IIdentityRegistryStore CreateStore();

    [Fact]
    public void CommitIsAtomicAndEnforcesOptimisticConcurrency()
    {
        var store = CreateStore();
        var actor = Actor("tenant-a");
        var patient = Patient(version: 1);
        store.CommitAsync(
            actor,
            new RegistryMutation([], [patient], [], [], [], []),
            CancellationToken.None).AsTask().GetAwaiter().GetResult();
        var staleUpdate = patient with { Version = 2 };

        Assert.Throws<RegistryConcurrencyException>(() =>
            store.CommitAsync(
                actor,
                new RegistryMutation(
                    [],
                    [staleUpdate],
                    [],
                    [],
                    [],
                    [new ExpectedVersion(
                        RegistryEntityKind.CanonicalPatient,
                        patient.EnterpriseId.ToString(),
                        99)]),
                CancellationToken.None).AsTask().GetAwaiter().GetResult());
        var unchanged = store.GetCanonicalPatientAsync(
            actor,
            patient.EnterpriseId,
            CancellationToken.None).AsTask().GetAwaiter().GetResult();
        Assert.Equal(1, unchanged!.Version);
    }

    [Fact]
    public void IdempotentCommitRejectsDigestChanges()
    {
        var store = CreateStore();
        var actor = Actor("tenant-a");
        var receipt = new IngestionReceipt(
            "message-1",
            "digest-a",
            "accepted",
            "ACK",
            DateTimeOffset.UnixEpoch);
        var first = store.CommitAsync(
            actor,
            RegistryMutation.Empty with { Receipt = receipt },
            CancellationToken.None).AsTask().GetAwaiter().GetResult();
        var replay = store.CommitAsync(
            actor,
            RegistryMutation.Empty with { Receipt = receipt },
            CancellationToken.None).AsTask().GetAwaiter().GetResult();

        Assert.True(first.WasApplied);
        Assert.True(replay.WasIdempotent);
        Assert.Throws<IdempotencyConflictException>(() =>
            store.CommitAsync(
                actor,
                RegistryMutation.Empty with
                {
                    Receipt = receipt with { PayloadDigest = "digest-b" }
                },
                CancellationToken.None).AsTask().GetAwaiter().GetResult());
    }

    [Fact]
    public void EveryOperationIsTenantScoped()
    {
        var store = CreateStore();
        var patient = Patient(version: 1);
        store.CommitAsync(
            Actor("tenant-a"),
            new RegistryMutation([], [patient], [], [], [], []),
            CancellationToken.None).AsTask().GetAwaiter().GetResult();

        try
        {
            var guessed = store.GetCanonicalPatientAsync(
                Actor("tenant-b"),
                patient.EnterpriseId,
                CancellationToken.None).AsTask().GetAwaiter().GetResult();
            Assert.Null(guessed);
        }
        catch (InvalidOperationException)
        {
            // A provider may reject a direct cross-tenant ID guess after its label check.
        }
    }

    [Fact]
    public void CandidateLookupIsBoundedAndReportsTruncation()
    {
        var store = CreateStore();
        var actor = Actor("tenant-a");
        var key = new BlockingKey("v1", "opaque");
        var patients = Enumerable.Range(0, 3)
            .Select(index => Patient(index + 1, EnterpriseId.New()) with
            {
                Version = 1,
                BlockingKeys = [key]
            })
            .ToArray();
        store.CommitAsync(
            actor,
            new RegistryMutation([], patients, [], [], [], []),
            CancellationToken.None).AsTask().GetAwaiter().GetResult();

        var page = store.FindCandidatesAsync(
            actor,
            [key],
            2,
            CancellationToken.None).AsTask().GetAwaiter().GetResult();

        Assert.True(page.IsTruncated);
        Assert.Equal(2, page.Items.Count);
    }

    [Fact]
    public void PaginationCursorsAreOpaqueAndStable()
    {
        var store = CreateStore();
        var actor = Actor("tenant-a");
        var patients = Enumerable.Range(0, 3)
            .Select(_ => Patient(1, EnterpriseId.New()))
            .ToArray();
        store.CommitAsync(
            actor,
            new RegistryMutation([], patients, [], [], [], []),
            CancellationToken.None).AsTask().GetAwaiter().GetResult();

        var first = store.SearchCanonicalPatientsAsync(
            actor,
            new CanonicalPatientSearch(Count: 2),
            CancellationToken.None).AsTask().GetAwaiter().GetResult();
        var second = store.SearchCanonicalPatientsAsync(
            actor,
            new CanonicalPatientSearch(Count: 2, Cursor: first.NextCursor),
            CancellationToken.None).AsTask().GetAwaiter().GetResult();

        Assert.NotNull(first.NextCursor);
        Assert.False(int.TryParse(first.NextCursor, out _));
        Assert.Single(second.Items);
        Assert.Null(second.NextCursor);
    }

    [Fact]
    public void AuditRecordsAreTenantScopedAndSearchable()
    {
        var store = CreateStore();
        var actor = Actor("tenant-a");
        var record = new AuditRecord(
            Guid.CreateVersion7(),
            "review-link",
            "reviewer-a",
            "success",
            "Verified duplicate.",
            EnterpriseId.New(),
            null,
            DateTimeOffset.UtcNow,
            "correlation-a");
        store.CommitAsync(
            actor,
            new RegistryMutation([], [], [], [], [record], []),
            CancellationToken.None).AsTask().GetAwaiter().GetResult();

        var found = store.SearchAuditRecordsAsync(
            actor,
            new AuditRecordSearch(Action: "review-link", Actor: "reviewer-a"),
            CancellationToken.None).AsTask().GetAwaiter().GetResult();
        var otherTenant = store.SearchAuditRecordsAsync(
            Actor("tenant-b"),
            new AuditRecordSearch(),
            CancellationToken.None).AsTask().GetAwaiter().GetResult();

        Assert.Single(found.Items);
        Assert.Equal(record.Id, found.Items[0].Id);
        Assert.Equal(record.EnterpriseId, found.Items[0].EnterpriseId);
        Assert.Empty(otherTenant.Items);
    }

    [Fact]
    public void ReviewCasesRoundTripIdentityAndDecisionEvidence()
    {
        var store = CreateStore();
        var actor = Actor("tenant-a");
        var subject = EnterpriseId.New();
        var candidate = EnterpriseId.New();
        var source = new SourceRecordKey(new SourceSystemId("pas"), "12345");
        var recordedAt = new DateTimeOffset(2026, 7, 25, 18, 0, 0, TimeSpan.Zero);
        var review = new ReviewCase(
            Guid.CreateVersion7(),
            subject,
            candidate,
            0.91,
            MatchGrade.Probable,
            [new FieldEvidence("birthDate", 1, 0.3, "exact")],
            "uk-default-v1",
            ReviewCaseStatus.AwaitingSecondApproval,
            recordedAt,
            recordedAt,
            1,
            Kind: ReviewCaseKind.ManualDuplicate,
            RequiredApprovals: 2,
            Approvals:
            [
                new ReviewApproval(
                    "reviewer-one",
                    ReviewDecision.Link,
                    "Verified duplicate.",
                    recordedAt)
            ],
            SourcesToMove: [source],
            SubjectVersion: 3,
            CandidateVersion: 4,
            ApprovalPolicyLocked: true);

        store.CommitAsync(
            actor,
            new RegistryMutation([], [], [], [review], [], []),
            CancellationToken.None).AsTask().GetAwaiter().GetResult();
        var stored = store.GetReviewCaseAsync(
            actor,
            review.Id,
            CancellationToken.None).AsTask().GetAwaiter().GetResult();

        Assert.NotNull(stored);
        Assert.Equal(subject, stored.SubjectEnterpriseId);
        Assert.Equal(candidate, stored.CandidateEnterpriseId);
        Assert.Equal(review.Kind, stored.Kind);
        Assert.Equal(review.Status, stored.Status);
        Assert.Equal(review.Evidence, stored.Evidence);
        Assert.Equal(review.Approvals, stored.Approvals);
        Assert.Equal(review.SourcesToMove, stored.SourcesToMove);
        Assert.Equal(review.SubjectVersion, stored.SubjectVersion);
        Assert.Equal(review.CandidateVersion, stored.CandidateVersion);
        Assert.Equal(review.ApprovalPolicyLocked, stored.ApprovalPolicyLocked);
    }

    [Fact]
    public void TenantSettingsUseOptimisticConcurrency()
    {
        var store = CreateStore();
        var actor = Actor("tenant-a");
        var settings = new TenantSettings(
            actor.TenantId,
            "uk-default-v1",
            0.62,
            0.82,
            2,
            [new SourceSystemSettings(new SourceSystemId("pas"), 100, true)],
            DateTimeOffset.UtcNow,
            "administrator",
            1);
        store.CommitAsync(
            actor,
            RegistryMutation.Empty with { TenantSettings = settings },
            CancellationToken.None).AsTask().GetAwaiter().GetResult();
        var stored = store.GetTenantSettingsAsync(
            actor,
            CancellationToken.None).AsTask().GetAwaiter().GetResult();

        Assert.NotNull(stored);
        Assert.Equal(settings.TenantId, stored.TenantId);
        Assert.Equal(settings.MatchingProfileVersion, stored.MatchingProfileVersion);
        Assert.Equal(settings.PossibleThreshold, stored.PossibleThreshold);
        Assert.Equal(settings.ProbableThreshold, stored.ProbableThreshold);
        Assert.Equal(settings.RequiredLinkApprovals, stored.RequiredLinkApprovals);
        Assert.Equal(settings.Sources, stored.Sources);
        Assert.Equal(settings.UpdatedAt, stored.UpdatedAt);
        Assert.Equal(settings.UpdatedBy, stored.UpdatedBy);
        Assert.Equal(settings.Version, stored.Version);
        Assert.Throws<RegistryConcurrencyException>(() =>
            store.CommitAsync(
                actor,
                RegistryMutation.Empty with
                {
                    TenantSettings = settings with { Version = 2 },
                    ExpectedVersions =
                    [
                        new ExpectedVersion(
                            RegistryEntityKind.TenantSettings,
                            actor.TenantId.Value,
                            99)
                    ]
                },
                CancellationToken.None).AsTask().GetAwaiter().GetResult());
    }

    [Fact]
    public void MaintenanceJobsAreDurableTenantScopedAndVersionChecked()
    {
        var store = CreateStore();
        var actor = Actor("tenant-a");
        var job = new RegistryMaintenanceJob(
            Guid.Parse("0198f7c7-6280-7b83-946c-8cc6f47c83ee"),
            actor.TenantId,
            RegistryMaintenanceJobKind.Reindex,
            RegistryMaintenanceJobStatus.Queued,
            RegistryMaintenanceJobPhase.Validating,
            RegistryMaintenanceTrigger.Manual,
            "administrator",
            "Approved blocking index rebuild.",
            DateTimeOffset.Parse(
                "2026-07-28T12:00:00Z",
                System.Globalization.CultureInfo.InvariantCulture),
            new string('a', 64),
            "uk-default-v2",
            25,
            1);
        store.CommitAsync(
            actor,
            RegistryMutation.Empty with { MaintenanceJobs = [job] },
            CancellationToken.None).AsTask().GetAwaiter().GetResult();

        var stored = store.GetMaintenanceJobAsync(
            actor,
            job.Id,
            CancellationToken.None).AsTask().GetAwaiter().GetResult();
        var page = store.SearchMaintenanceJobsAsync(
            actor,
            new MaintenanceJobSearch(
                RegistryMaintenanceJobKind.Reindex,
                RegistryMaintenanceJobStatus.Queued),
            CancellationToken.None).AsTask().GetAwaiter().GetResult();
        Assert.NotNull(stored);
        Assert.Equal(job, stored);
        Assert.Contains(page.Items, item => item.Id == job.Id);
        try
        {
            var otherTenant = store.GetMaintenanceJobAsync(
                Actor("tenant-b"),
                job.Id,
                CancellationToken.None).AsTask().GetAwaiter().GetResult();
            Assert.Null(otherTenant);
        }
        catch (InvalidOperationException)
        {
            // A provider may reject a direct cross-tenant ID guess after its label check.
        }

        Assert.Throws<RegistryConcurrencyException>(() =>
            store.CommitAsync(
                actor,
                RegistryMutation.Empty with
                {
                    MaintenanceJobs =
                    [
                        job with
                        {
                            Status = RegistryMaintenanceJobStatus.Running,
                            Version = 2
                        }
                    ],
                    ExpectedVersions =
                    [
                        new ExpectedVersion(
                            RegistryEntityKind.MaintenanceJob,
                            job.Id.ToString("D"),
                            99)
                    ]
                },
                CancellationToken.None).AsTask().GetAwaiter().GetResult());
    }

    protected static ActorContext Actor(string tenant) =>
        new(
            new TenantId(tenant),
            "test",
            null,
            new HashSet<string>(),
            "correlation");

    protected static CanonicalPatient Patient(
        long version,
        EnterpriseId? enterpriseId = null) =>
        new(
            enterpriseId ?? new EnterpriseId(
                Guid.Parse("018f6f9a-1533-7b1c-8d7b-b85f4383a154")),
            IdentityProfile.Empty,
            [],
            [],
            0,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            version);
}
#pragma warning restore xUnit1031
