using OpenMpi.Application;
using OpenMpi.Application.Configuration;
using OpenMpi.Application.Matching;
using OpenMpi.Domain;
using OpenMpi.Storage.Abstractions;
using OpenMpi.Storage.InMemory;
using Xunit;

namespace OpenMpi.Domain.Tests;

public sealed class RegistryWorkflowTests
{
    [Fact]
    public async Task DuplicateLinkRequiresDistinctApproversAndRetiresTheSubject()
    {
        var fixture = CreateFixture();
        var subject = await fixture.UpsertAsync(
            "pas",
            "P-100",
            Profile("9434765919", "Smith"));
        var survivor = await fixture.UpsertAsync(
            "community",
            "C-200",
            Profile("9999999999", "Smith"));
        var review = await fixture.Service.CreateDuplicateReviewCaseAsync(
            fixture.Reviewer("reviewer-one"),
            new CreateDuplicateReviewCommand(
                subject.CanonicalPatient.EnterpriseId,
                survivor.CanonicalPatient.EnterpriseId,
                "Records appear to describe the same patient.",
                subject.CanonicalPatient.Version,
                survivor.CanonicalPatient.Version),
            CancellationToken.None);

        var firstApproval = await fixture.Service.DecideReviewCaseAsync(
            fixture.Reviewer("reviewer-one"),
            new ReviewDecisionCommand(
                review.Id,
                ReviewDecision.Link,
                "Demographics were compared against source systems.",
                review.Version),
            CancellationToken.None);

        Assert.Equal(ReviewCaseStatus.AwaitingSecondApproval, firstApproval.Status);
        Assert.Single(firstApproval.Approvals!);
        await Assert.ThrowsAsync<RegistryAuthorisationException>(() =>
            fixture.Service.DecideReviewCaseAsync(
                fixture.Reviewer("reviewer-one"),
                new ReviewDecisionCommand(
                    review.Id,
                    ReviewDecision.Link,
                    "Attempted duplicate approval.",
                    firstApproval.Version),
                CancellationToken.None).AsTask());

        var completed = await fixture.Service.DecideReviewCaseAsync(
            fixture.Reviewer("reviewer-two"),
            new ReviewDecisionCommand(
                review.Id,
                ReviewDecision.Link,
                "Independent verification completed.",
                firstApproval.Version),
            CancellationToken.None);
        var retired = await fixture.Service.GetCanonicalPatientAsync(
            fixture.Reviewer("reader"),
            subject.CanonicalPatient.EnterpriseId,
            CancellationToken.None);
        var active = await fixture.Service.GetCanonicalPatientAsync(
            fixture.Reviewer("reader"),
            survivor.CanonicalPatient.EnterpriseId,
            CancellationToken.None);

        Assert.Equal(ReviewCaseStatus.Linked, completed.Status);
        Assert.Equal(2, completed.Approvals!.Count);
        Assert.False(retired!.IsActive);
        Assert.Equal(active!.EnterpriseId, retired.ReplacedBy);
        Assert.Equal(2, active.Sources.Count);
    }

    [Fact]
    public async Task LoweredPolicyAllowsExplicitCompletionOfAnOpenOrdinaryLinkReview()
    {
        var fixture = CreateFixture();
        var subject = await fixture.UpsertAsync(
            "pas",
            "P-201",
            Profile("9434765919", "Smith"));
        var survivor = await fixture.UpsertAsync(
            "community",
            "C-201",
            Profile(null, "Smith"));
        var review = await fixture.Service.CreateDuplicateReviewCaseAsync(
            fixture.Reviewer("reviewer-one"),
            new CreateDuplicateReviewCommand(
                subject.CanonicalPatient.EnterpriseId,
                survivor.CanonicalPatient.EnterpriseId,
                "Records appear to describe the same patient.",
                subject.CanonicalPatient.Version,
                survivor.CanonicalPatient.Version),
            CancellationToken.None);
        var firstApproval = await fixture.Service.DecideReviewCaseAsync(
            fixture.Reviewer("reviewer-one"),
            new ReviewDecisionCommand(
                review.Id,
                ReviewDecision.Link,
                "Initial comparison completed.",
                review.Version),
            CancellationToken.None);

        await fixture.SetRequiredLinkApprovalsAsync(1);
        var detail = await fixture.Service.GetReviewCaseDetailAsync(
            fixture.Reviewer("reviewer-one"),
            review.Id,
            CancellationToken.None);
        var completed = await fixture.Service.DecideReviewCaseAsync(
            fixture.Reviewer("reviewer-one"),
            new ReviewDecisionCommand(
                review.Id,
                ReviewDecision.Link,
                "Completing under the current one-approval policy.",
                firstApproval.Version),
            CancellationToken.None);

        Assert.Equal(2, detail.ReviewCase.RequiredApprovals);
        Assert.Equal(1, detail.EffectiveRequiredApprovals);
        Assert.Equal(ReviewCaseStatus.Linked, completed.Status);
        Assert.Equal(1, completed.RequiredApprovals);
        Assert.Single(completed.Approvals!);
        Assert.Equal("reviewer-one", completed.DecidedBy);
    }

    [Fact]
    public async Task LoweredPolicyDoesNotRelaxAConflictingIdentifierReview()
    {
        var fixture = CreateFixture();
        var subject = await fixture.UpsertAsync(
            "pas",
            "P-202",
            Profile("9434765919", "Smith"));
        var survivor = await fixture.UpsertAsync(
            "community",
            "C-202",
            Profile("9999999999", "Smith"));
        var review = await fixture.Service.CreateDuplicateReviewCaseAsync(
            fixture.Reviewer("reviewer-one"),
            new CreateDuplicateReviewCommand(
                subject.CanonicalPatient.EnterpriseId,
                survivor.CanonicalPatient.EnterpriseId,
                "Conflicting identifiers require independent review.",
                subject.CanonicalPatient.Version,
                survivor.CanonicalPatient.Version),
            CancellationToken.None);
        var firstApproval = await fixture.Service.DecideReviewCaseAsync(
            fixture.Reviewer("reviewer-one"),
            new ReviewDecisionCommand(
                review.Id,
                ReviewDecision.Link,
                "Initial source comparison completed.",
                review.Version),
            CancellationToken.None);

        await fixture.SetRequiredLinkApprovalsAsync(1);
        var detail = await fixture.Service.GetReviewCaseDetailAsync(
            fixture.Reviewer("reviewer-one"),
            review.Id,
            CancellationToken.None);

        Assert.True(review.ApprovalPolicyLocked);
        Assert.Equal(2, detail.EffectiveRequiredApprovals);
        await Assert.ThrowsAsync<RegistryAuthorisationException>(() =>
            fixture.Service.DecideReviewCaseAsync(
                fixture.Reviewer("reviewer-one"),
                new ReviewDecisionCommand(
                    review.Id,
                    ReviewDecision.Link,
                    "Attempted completion without an independent reviewer.",
                    firstApproval.Version),
                CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task StaleDuplicateReviewCanBeClosedAsSuperseded()
    {
        var fixture = CreateFixture();
        var subject = await fixture.UpsertAsync(
            "pas",
            "P-250",
            Profile("9434765919", "Smith"));
        var survivor = await fixture.UpsertAsync(
            "community",
            "C-250",
            Profile("9999999999", "Smith"));
        var completedReview = await fixture.Service.CreateDuplicateReviewCaseAsync(
            fixture.Reviewer("reviewer-one"),
            new CreateDuplicateReviewCommand(
                subject.CanonicalPatient.EnterpriseId,
                survivor.CanonicalPatient.EnterpriseId,
                "First governed comparison.",
                subject.CanonicalPatient.Version,
                survivor.CanonicalPatient.Version),
            CancellationToken.None);
        var staleReview = await fixture.Service.CreateDuplicateReviewCaseAsync(
            fixture.Reviewer("reviewer-one"),
            new CreateDuplicateReviewCommand(
                subject.CanonicalPatient.EnterpriseId,
                survivor.CanonicalPatient.EnterpriseId,
                "Overlapping governed comparison.",
                subject.CanonicalPatient.Version,
                survivor.CanonicalPatient.Version),
            CancellationToken.None);
        var firstApproval = await fixture.Service.DecideReviewCaseAsync(
            fixture.Reviewer("reviewer-one"),
            new ReviewDecisionCommand(
                completedReview.Id,
                ReviewDecision.Link,
                "Initial evidence checked.",
                completedReview.Version),
            CancellationToken.None);
        await fixture.Service.DecideReviewCaseAsync(
            fixture.Reviewer("reviewer-two"),
            new ReviewDecisionCommand(
                completedReview.Id,
                ReviewDecision.Link,
                "Independent evidence checked.",
                firstApproval.Version),
            CancellationToken.None);

        var staleDetail = await fixture.Service.GetReviewCaseDetailAsync(
            fixture.Reviewer("reviewer-three"),
            staleReview.Id,
            CancellationToken.None);
        var superseded = await fixture.Service.DecideReviewCaseAsync(
            fixture.Reviewer("reviewer-three"),
            new ReviewDecisionCommand(
                staleReview.Id,
                ReviewDecision.Supersede,
                "The subject identity was replaced by another governed review.",
                staleReview.Version),
            CancellationToken.None);
        var openReviews = await fixture.Service.SearchReviewCasesAsync(
            fixture.Reviewer("reviewer-three"),
            new ReviewCaseSearch(ReviewCaseStatus.Pending, Count: 100),
            CancellationToken.None);
        var audit = await fixture.Service.SearchAuditRecordsAsync(
            fixture.Reviewer("reviewer-three"),
            new AuditRecordSearch(Action: "review-supersede"),
            CancellationToken.None);

        Assert.False(staleDetail.Subject.CanonicalPatient.IsActive);
        Assert.Equal(
            survivor.CanonicalPatient.EnterpriseId,
            staleDetail.Subject.CanonicalPatient.ReplacedBy);
        Assert.Equal(ReviewCaseStatus.Superseded, superseded.Status);
        Assert.DoesNotContain(openReviews.Items, item => item.Id == staleReview.Id);
        Assert.Contains(
            audit.Items,
            record =>
                record.Action == "review-supersede" &&
                record.EnterpriseId == staleReview.SubjectEnterpriseId);
    }

    [Fact]
    public async Task CurrentDuplicateReviewCannotBeClosedAsSuperseded()
    {
        var fixture = CreateFixture();
        var subject = await fixture.UpsertAsync(
            "pas",
            "P-251",
            Profile("9434765919", "Smith"));
        var survivor = await fixture.UpsertAsync(
            "community",
            "C-251",
            Profile("9999999999", "Smith"));
        var review = await fixture.Service.CreateDuplicateReviewCaseAsync(
            fixture.Reviewer("reviewer-one"),
            new CreateDuplicateReviewCommand(
                subject.CanonicalPatient.EnterpriseId,
                survivor.CanonicalPatient.EnterpriseId,
                "Current governed comparison.",
                subject.CanonicalPatient.Version,
                survivor.CanonicalPatient.Version),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<RegistryConcurrencyException>(() =>
            fixture.Service.DecideReviewCaseAsync(
                fixture.Reviewer("reviewer-one"),
                new ReviewDecisionCommand(
                    review.Id,
                    ReviewDecision.Supersede,
                    "Attempted premature closure.",
                    review.Version),
                CancellationToken.None).AsTask());

        Assert.Contains("have not changed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReviewWithUpdatedActiveIdentityCanBeClosedAsSuperseded()
    {
        var fixture = CreateFixture();
        var subject = await fixture.UpsertAsync(
            "pas",
            "P-252",
            Profile("9434765919", "Smith"));
        var survivor = await fixture.UpsertAsync(
            "community",
            "C-252",
            Profile("9999999999", "Smith"));
        var review = await fixture.Service.CreateDuplicateReviewCaseAsync(
            fixture.Reviewer("reviewer-one"),
            new CreateDuplicateReviewCommand(
                subject.CanonicalPatient.EnterpriseId,
                survivor.CanonicalPatient.EnterpriseId,
                "Current governed comparison.",
                subject.CanonicalPatient.Version,
                survivor.CanonicalPatient.Version),
            CancellationToken.None);

        var updatedSubject = await fixture.UpsertAsync(
            "pas",
            "P-252",
            Profile("9434765919", "Smyth"),
            subject.SourcePatient.Version);
        var staleDecision = await Assert.ThrowsAsync<RegistryConcurrencyException>(() =>
            fixture.Service.DecideReviewCaseAsync(
                fixture.Reviewer("reviewer-two"),
                new ReviewDecisionCommand(
                    review.Id,
                    ReviewDecision.Link,
                    "Attempted to use the captured evidence.",
                    review.Version),
                CancellationToken.None).AsTask());
        var unchangedReview = await fixture.Service.GetReviewCaseAsync(
            fixture.Reviewer("reviewer-two"),
            review.Id,
            CancellationToken.None);
        var superseded = await fixture.Service.DecideReviewCaseAsync(
            fixture.Reviewer("reviewer-two"),
            new ReviewDecisionCommand(
                review.Id,
                ReviewDecision.Supersede,
                "The subject demographics changed after the evidence was captured.",
                review.Version),
            CancellationToken.None);

        Assert.True(updatedSubject.CanonicalPatient.IsActive);
        Assert.Equal(
            subject.CanonicalPatient.EnterpriseId,
            updatedSubject.CanonicalPatient.EnterpriseId);
        Assert.True(updatedSubject.CanonicalPatient.Version > review.SubjectVersion);
        Assert.Contains("fresh comparison", staleDecision.Message, StringComparison.Ordinal);
        Assert.Equal(review.Version, unchangedReview!.Version);
        Assert.Empty(unchangedReview.Approvals!);
        Assert.Equal(ReviewCaseStatus.Superseded, superseded.Status);
    }

    [Fact]
    public async Task SplitMovesOnlySelectedSourcesAfterTwoApprovals()
    {
        var fixture = CreateFixture();
        var original = await fixture.UpsertAsync(
            "pas",
            "P-300",
            Profile("9434765919", "Jones"));
        var linked = await fixture.UpsertAsync(
            "community",
            "C-301",
            Profile("9434765919", "Jones"));
        Assert.Equal(
            original.CanonicalPatient.EnterpriseId,
            linked.CanonicalPatient.EnterpriseId);
        Assert.Equal(2, linked.CanonicalPatient.Sources.Count);

        var sourceToMove = new SourceRecordKey(new SourceSystemId("community"), "C-301");
        var review = await fixture.Service.CreateSplitReviewCaseAsync(
            fixture.Reviewer("reviewer-one"),
            new CreateSplitReviewCommand(
                linked.CanonicalPatient.EnterpriseId,
                [sourceToMove],
                "The community record was linked to the wrong person.",
                linked.CanonicalPatient.Version),
            CancellationToken.None);
        var first = await fixture.Service.DecideReviewCaseAsync(
            fixture.Reviewer("reviewer-one"),
            new ReviewDecisionCommand(
                review.Id,
                ReviewDecision.Split,
                "Source provenance confirms a separate identity.",
                review.Version),
            CancellationToken.None);
        var completed = await fixture.Service.DecideReviewCaseAsync(
            fixture.Reviewer("reviewer-two"),
            new ReviewDecisionCommand(
                review.Id,
                ReviewDecision.Split,
                "Independent source comparison confirms the correction.",
                first.Version),
            CancellationToken.None);
        var remaining = await fixture.Service.GetCanonicalPatientAsync(
            fixture.Reviewer("reader"),
            linked.CanonicalPatient.EnterpriseId,
            CancellationToken.None);
        var separated = await fixture.Service.GetCanonicalPatientAsync(
            fixture.Reviewer("reader"),
            review.CandidateEnterpriseId,
            CancellationToken.None);
        var movedSource = await fixture.Store.GetSourcePatientAsync(
            fixture.Reviewer("reader"),
            sourceToMove,
            CancellationToken.None);

        Assert.Equal(ReviewCaseStatus.Split, completed.Status);
        Assert.Single(remaining!.Sources);
        Assert.Single(separated!.Sources);
        Assert.Equal(review.CandidateEnterpriseId, movedSource!.EnterpriseId);
    }

    [Fact]
    public async Task PatientUpdateRejectsAStaleSourceVersion()
    {
        var fixture = CreateFixture();
        var created = await fixture.UpsertAsync(
            "pas",
            "P-300",
            Profile("9434765919", "Original"),
            expectedVersion: 0);
        var updated = await fixture.UpsertAsync(
            "pas",
            "P-300",
            Profile("9434765919", "Current"),
            created.SourcePatient.Version);

        await Assert.ThrowsAsync<RegistryConcurrencyException>(() =>
            fixture.UpsertAsync(
                "pas",
                "P-300",
                Profile("9434765919", "Stale"),
                created.SourcePatient.Version).AsTask());

        var stored = await fixture.Store.GetSourcePatientAsync(
            fixture.Reviewer("reader"),
            updated.SourcePatient.Key,
            CancellationToken.None);
        Assert.Equal("Current", stored!.Profile.Names.Single().Family);
        Assert.Equal(updated.SourcePatient.Version, stored.Version);
    }

    [Fact]
    public async Task TenantPolicyChangesAreAuditedAndVersionChecked()
    {
        var fixture = CreateFixture();
        var actor = fixture.Administrator("tenant-admin");
        var initial = await fixture.Service.GetTenantSettingsAsync(
            actor,
            CancellationToken.None);
        var command = new UpdateTenantSettingsCommand(
            "uk-safe-v2",
            0.65,
            0.85,
            2,
            initial.Sources,
            "Approved matching-policy change.",
            initial.Version);

        var updated = await fixture.Service.UpdateTenantSettingsAsync(
            actor,
            command,
            CancellationToken.None);
        var audit = await fixture.Service.SearchAuditRecordsAsync(
            actor,
            new AuditRecordSearch(Action: "tenant-settings-update"),
            CancellationToken.None);

        Assert.Equal(1, updated.Version);
        Assert.Equal("uk-safe-v2", updated.MatchingProfileVersion);
        Assert.Contains(
            audit.Items,
            static record =>
                record.Action == "tenant-settings-update" &&
                record.Outcome == "success");
        await Assert.ThrowsAsync<RegistryConcurrencyException>(() =>
            fixture.Service.UpdateTenantSettingsAsync(
                actor,
                command,
                CancellationToken.None).AsTask());
    }

    private static Fixture CreateFixture()
    {
        var tenant = new TenantId("tenant-a");
        var sources = new[]
        {
            new SourceSystemId("pas"),
            new SourceSystemId("community")
        };
        var configuration = new TenantMatchingConfiguration(
            tenant,
            MatchingProfile.UkDefault,
            [new BlockingKeySecret("v1", Enumerable.Repeat((byte)42, 32).ToArray(), true)],
            sources.ToDictionary(static source => source, static _ => 100),
            sources.ToHashSet(),
            2);
        var store = new InMemoryIdentityRegistryStore();
        var configurations = new Dictionary<TenantId, TenantMatchingConfiguration>
        {
            [tenant] = configuration
        };
        var service = new RegistryService(
            store,
            new StoredTenantConfigurationProvider(
                configurations,
                store,
                TimeProvider.System),
            TimeProvider.System);
        return new Fixture(tenant, store, service);
    }

    private static IdentityProfile Profile(string? nhsNumber, string family) =>
        new(
            nhsNumber is null
                ? []
                :
                [
                    new IdentityIdentifier(
                        "https://fhir.nhs.uk/Id/nhs-number",
                        nhsNumber,
                        true,
                        true)
                ],
            [new PersonName(family, ["Alex"], NameUse.Official)],
            new DateOnly(1980, 1, 2),
            AdministrativeGender.Unknown,
            [new PostalAddress(["1 High Street"], "Leeds", null, "LS1 1AA", "GB")],
            []);

    private sealed record Fixture(
        TenantId Tenant,
        InMemoryIdentityRegistryStore Store,
        RegistryService Service)
    {
        public ActorContext Reviewer(string actor) =>
            Context(actor, null, "mpi.review", "mpi.audit");

        public ActorContext Administrator(string actor) =>
            Context(actor, null, "mpi.admin");

        public ValueTask<UpsertPatientResult> UpsertAsync(
            string sourceSystem,
            string localId,
            IdentityProfile profile,
            long? expectedVersion = null)
        {
            var source = new SourceSystemId(sourceSystem);
            return Service.UpsertPatientAsync(
                Context($"{sourceSystem}-service", source),
                new UpsertPatientCommand(
                    new SourceRecordKey(source, localId),
                    profile,
                    ExpectedVersion: expectedVersion),
                CancellationToken.None);
        }

        public async ValueTask<TenantSettings> SetRequiredLinkApprovalsAsync(int requiredApprovals)
        {
            var actor = Administrator("tenant-admin");
            var current = await Service.GetTenantSettingsAsync(
                actor,
                CancellationToken.None);
            return await Service.UpdateTenantSettingsAsync(
                actor,
                new UpdateTenantSettingsCommand(
                    current.MatchingProfileVersion,
                    current.PossibleThreshold,
                    current.ProbableThreshold,
                    requiredApprovals,
                    current.Sources,
                    $"Set required link approvals to {requiredApprovals}.",
                    current.Version),
                CancellationToken.None);
        }

        private ActorContext Context(
            string actor,
            SourceSystemId? source,
            params string[] scopes) =>
            new(
                Tenant,
                actor,
                source,
                scopes.ToHashSet(StringComparer.Ordinal),
                Guid.CreateVersion7().ToString("N"));
    }
}
