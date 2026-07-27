using System.Diagnostics;
using UnifyEmpi.Application.Configuration;
using UnifyEmpi.Application.Identifiers;
using UnifyEmpi.Application.Matching;
using UnifyEmpi.Application.Normalisation;
using UnifyEmpi.Domain;
using UnifyEmpi.Storage.Abstractions;

namespace UnifyEmpi.Application;

public sealed class RegistryService(
    IIdentityRegistryStore store,
    ITenantConfigurationProvider configurationProvider,
    TimeProvider timeProvider)
{
    public async ValueTask<MatchResponse> MatchAsync(
        ActorContext context,
        MatchRequest request,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        using var activity = RegistryTelemetry.Activities.StartActivity("mpi.match");
        activity?.SetTag("tenant.id", context.TenantId.Value);
        var configuration = await configurationProvider.GetConfigurationAsync(
            context.TenantId,
            cancellationToken);
        var profile = configuration.MatchingProfile;
        var count = Math.Clamp(
            request.Count <= 0 ? profile.DefaultResultCount : request.Count,
            1,
            profile.MaximumResultCount);
        var normalised = IdentityNormaliser.Normalise(
            ApplyIdentifierAuthority(request.Profile, profile, isAuthoritative: true));
        var blockingKeys = BlockingKeyGenerator.Generate(normalised, configuration);
        var candidates = await store.FindCandidatesAsync(
            context,
            blockingKeys,
            profile.MaximumCandidates,
            cancellationToken);

        if (candidates.IsTruncated)
        {
            throw new CandidateLimitExceededException(profile.MaximumCandidates);
        }

        var matches = candidates.Items
            .Select(candidate => WeightedIdentityMatcher.Match(normalised, candidate, profile))
            .Where(result =>
                result.Grade != MatchGrade.None &&
                (!request.OnlyCertainMatches || result.Grade == MatchGrade.Certain))
            .OrderByDescending(static result => result.Score)
            .ThenBy(static result => result.Patient.EnterpriseId.Value)
            .Take(count)
            .ToArray();

        var response = new MatchResponse(matches, candidates.Items.Count, profile.Version);
        RegistryTelemetry.RecordMatch(started, response, context.TenantId);
        return response;
    }

    public async ValueTask<UpsertPatientResult> UpsertPatientAsync(
        ActorContext context,
        UpsertPatientCommand command,
        CancellationToken cancellationToken)
    {
        EnsureSourceActor(context, command.SourceRecord.SourceSystem);
        ValidateIdempotency(command);
        var now = timeProvider.GetUtcNow();
        var configuration = await configurationProvider.GetConfigurationAsync(
            context.TenantId,
            cancellationToken);
        var incomingProfile = ApplyIdentifierAuthority(
            command.Profile,
            configuration.MatchingProfile,
            configuration.AuthoritativeSources.Contains(command.SourceRecord.SourceSystem));

        if (command.IdempotencyKey is not null)
        {
            var existingReceipt = await store.GetReceiptAsync(
                context,
                command.IdempotencyKey,
                cancellationToken);
            if (existingReceipt is not null)
            {
                if (!string.Equals(
                        existingReceipt.PayloadDigest,
                        command.PayloadDigest,
                        StringComparison.Ordinal))
                {
                    throw new IdempotencyConflictException(command.IdempotencyKey);
                }

                var replaySource = await store.GetSourcePatientAsync(
                    context,
                    command.SourceRecord,
                    cancellationToken)
                    ?? throw new RegistryNotFoundException("Patient", command.SourceRecord.ToString());
                var replayCanonical = await store.GetCanonicalPatientAsync(
                    context,
                    replaySource.EnterpriseId,
                    cancellationToken)
                    ?? throw new RegistryNotFoundException("Patient", replaySource.EnterpriseId.ToString());
                var replayPerson = await store.GetPersonAsync(
                    context,
                    replaySource.EnterpriseId,
                    cancellationToken)
                    ?? throw new RegistryNotFoundException("Person", replaySource.EnterpriseId.ToString());
                return new UpsertPatientResult(
                    replaySource,
                    replayCanonical,
                    replayPerson,
                    [],
                    true,
                    existingReceipt.Response);
            }
        }

        var existingSource = await store.GetSourcePatientAsync(
            context,
            command.SourceRecord,
            cancellationToken);
        if (command.ExpectedVersion is { } expectedVersion &&
            (existingSource?.Version ?? 0) != expectedVersion)
        {
            throw new RegistryConcurrencyException(
                "The source patient changed after it was loaded.");
        }

        var normalisedIncoming = IdentityNormaliser.Normalise(incomingProfile);
        var incomingKeys = BlockingKeyGenerator.Generate(normalisedIncoming, configuration);
        var sourceTrust = configuration.SourceTrust.TryGetValue(
            command.SourceRecord.SourceSystem,
            out var configuredTrust)
            ? configuredTrust
            : 0;

        CanonicalPatient? selectedCanonical = null;
        EnterprisePerson? selectedPerson = null;
        IReadOnlyList<MatchResult> probableMatches = [];

        if (existingSource is not null)
        {
            selectedCanonical = await store.GetCanonicalPatientAsync(
                context,
                existingSource.EnterpriseId,
                cancellationToken);
            selectedPerson = await store.GetPersonAsync(
                context,
                existingSource.EnterpriseId,
                cancellationToken);
        }
        else
        {
            var candidatePage = await store.FindCandidatesAsync(
                context,
                incomingKeys,
                configuration.MatchingProfile.MaximumCandidates,
                cancellationToken);
            if (candidatePage.IsTruncated)
            {
                throw new CandidateLimitExceededException(configuration.MatchingProfile.MaximumCandidates);
            }

            var evaluated = candidatePage.Items
                .Select(candidate => WeightedIdentityMatcher.Match(
                    normalisedIncoming,
                    candidate,
                    configuration.MatchingProfile))
                .Where(static result => result.Grade != MatchGrade.None)
                .OrderByDescending(static result => result.Score)
                .ToArray();
            var certain = evaluated.FirstOrDefault(static result =>
                result.Grade == MatchGrade.Certain && !result.HasHardConflict);
            if (certain is not null)
            {
                selectedCanonical = certain.Patient;
                selectedPerson = await store.GetPersonAsync(
                    context,
                    certain.Patient.EnterpriseId,
                    cancellationToken);
            }

            probableMatches = evaluated
                .Where(static result => result.Grade == MatchGrade.Probable)
                .ToArray();
        }

        var enterpriseId = selectedCanonical?.EnterpriseId ?? EnterpriseId.New();
        var activeSecret = configuration.BlockingKeySecrets.FirstOrDefault(static item => item.IsActive)
            ?? throw new InvalidOperationException("The tenant has no active blocking-key secret.");
        var sourceResourceId = existingSource?.ResourceId ??
                               StableResourceIdGenerator.Create(
                                   context.TenantId,
                                   command.SourceRecord,
                                   activeSecret.Secret);
        var source = new SourcePatientRecord(
            command.SourceRecord,
            sourceResourceId,
            enterpriseId,
            incomingProfile,
            sourceTrust,
            now,
            (existingSource?.Version ?? 0) + 1);

        var canonicalProfile = selectedCanonical is null
            ? incomingProfile
            : SurvivorshipService.Merge(
                selectedCanonical.Profile,
                selectedCanonical.SurvivorshipTrust,
                incomingProfile,
                sourceTrust,
                selectedCanonical.LastUpdated,
                now,
                selectedCanonical.Sources
                    .Select(static source => source.ToString())
                    .Order(StringComparer.Ordinal)
                    .FirstOrDefault(),
                command.SourceRecord.ToString());
        var canonicalKeys = BlockingKeyGenerator.Generate(
            IdentityNormaliser.Normalise(canonicalProfile),
            configuration);
        var canonical = new CanonicalPatient(
            enterpriseId,
            canonicalProfile,
            AppendDistinct(selectedCanonical?.Sources ?? [], command.SourceRecord),
            canonicalKeys,
            Math.Max(selectedCanonical?.SurvivorshipTrust ?? 0, sourceTrust),
            selectedCanonical?.CreatedAt ?? now,
            now,
            (selectedCanonical?.Version ?? 0) + 1);

        var link = new PersonLink(
            command.SourceRecord,
            sourceResourceId,
            selectedCanonical is null ? LinkAssurance.Level2 : LinkAssurance.Level4,
            now,
            selectedCanonical is null ? "New enterprise identity" : "Certain identifier match");
        var person = new EnterprisePerson(
            enterpriseId,
            UpsertLink(selectedPerson?.Links ?? [], link),
            selectedPerson?.CreatedAt ?? now,
            now,
            (selectedPerson?.Version ?? 0) + 1);

        var reviews = selectedCanonical is null
            ? probableMatches.Select(match => new ReviewCase(
                    Guid.CreateVersion7(),
                    enterpriseId,
                    match.Patient.EnterpriseId,
                    match.Score,
                    match.Grade,
                    match.Evidence,
                    configuration.MatchingProfile.Version,
                    ReviewCaseStatus.Pending,
                    now,
                    now,
                    1,
                    Kind: ReviewCaseKind.PotentialDuplicate,
                    RequiredApprovals: match.HasHardConflict
                        ? 2
                        : configuration.RequiredLinkApprovals,
                    Approvals: [],
                    SourcesToMove: [],
                    SubjectVersion: canonical.Version,
                    CandidateVersion: match.Patient.Version,
                    ApprovalPolicyLocked: match.HasHardConflict))
                .ToArray()
            : [];

        var audit = new AuditRecord(
            Guid.CreateVersion7(),
            existingSource is null ? "source-patient-create" : "source-patient-update",
            context.ActorId,
            "success",
            "Source-system upsert",
            enterpriseId,
            command.SourceRecord,
            now,
            context.CorrelationId);
        var expectedVersions = CreateExpectedVersions(
            existingSource,
            selectedCanonical,
            selectedPerson);
        var receipt = command.IdempotencyKey is null
            ? null
            : new IngestionReceipt(
                command.IdempotencyKey,
                command.PayloadDigest ?? string.Empty,
                "accepted",
                command.ReceiptResponse,
                now);
        var mutation = new RegistryMutation(
            [source],
            [canonical],
            [person],
            reviews,
            [audit],
            expectedVersions,
            receipt);

        var commit = await store.CommitAsync(context, mutation, cancellationToken);
        RegistryTelemetry.RecordReviewsCreated(reviews.Length, context.TenantId);
        string? replayResponse = null;
        if (commit.WasIdempotent && command.IdempotencyKey is not null)
        {
            replayResponse = (await store.GetReceiptAsync(
                context,
                command.IdempotencyKey,
                cancellationToken))?.Response;
        }

        return new UpsertPatientResult(
            source,
            canonical,
            person,
            reviews,
            commit.WasIdempotent,
            replayResponse);
    }

    public async ValueTask MergeSourceRecordsAsync(
        ActorContext context,
        SourceRecordKey previousSource,
        SourceRecordKey survivingSource,
        string reason,
        CancellationToken cancellationToken)
    {
        EnsureSourceActor(context, previousSource.SourceSystem);
        EnsureSourceActor(context, survivingSource.SourceSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var previous = await store.GetSourcePatientAsync(
            context,
            previousSource,
            cancellationToken) ?? throw new RegistryNotFoundException(
                "Patient",
                previousSource.ToString());
        var survivor = await store.GetSourcePatientAsync(
            context,
            survivingSource,
            cancellationToken) ?? throw new RegistryNotFoundException(
                "Patient",
                survivingSource.ToString());
        if (previous.EnterpriseId == survivor.EnterpriseId)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var review = new ReviewCase(
            Guid.CreateVersion7(),
            previous.EnterpriseId,
            survivor.EnterpriseId,
            1,
            MatchGrade.Certain,
            [new FieldEvidence("hl7v2-merge", 1, 1, "authoritative-event")],
            "hl7v2-authoritative-merge-v1",
            ReviewCaseStatus.Pending,
            now,
            now,
            1,
            Kind: ReviewCaseKind.PotentialDuplicate,
            RequiredApprovals: 1,
            Approvals: [],
            SourcesToMove: [],
            SubjectVersion: 0,
            CandidateVersion: null);
        var audit = new AuditRecord(
            Guid.CreateVersion7(),
            "hl7v2-merge-request",
            context.ActorId,
            "pending",
            reason,
            previous.EnterpriseId,
            previousSource,
            now,
            context.CorrelationId);
        await store.CommitAsync(
            context,
            new RegistryMutation([], [], [], [review], [audit], []),
            cancellationToken);
        await DecideReviewCaseAsync(
            context,
            new ReviewDecisionCommand(review.Id, ReviewDecision.Link, reason, review.Version),
            cancellationToken);
    }

    public async ValueTask<ReviewCase> DecideReviewCaseAsync(
        ActorContext context,
        ReviewDecisionCommand command,
        CancellationToken cancellationToken)
    {
        EnsureReviewer(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Reason);
        var review = await store.GetReviewCaseAsync(context, command.ReviewCaseId, cancellationToken)
            ?? throw new RegistryNotFoundException("ReviewCase", command.ReviewCaseId.ToString());
        if (review.Version != command.ExpectedVersion)
        {
            throw new RegistryConcurrencyException("The review case was changed by another reviewer.");
        }

        if (review.Status is not ReviewCaseStatus.Pending and
            not ReviewCaseStatus.AwaitingSecondApproval)
        {
            throw new RegistryConcurrencyException("The review case has already been decided.");
        }

        var now = timeProvider.GetUtcNow();
        if (command.Decision == ReviewDecision.Supersede)
        {
            var subject = await store.GetCanonicalPatientAsync(
                context,
                review.SubjectEnterpriseId,
                cancellationToken) ?? throw new RegistryNotFoundException(
                "Patient",
                review.SubjectEnterpriseId.ToString());
            CanonicalPatient? candidate = null;
            if (review.Kind != ReviewCaseKind.Split)
            {
                candidate = await store.GetCanonicalPatientAsync(
                    context,
                    review.CandidateEnterpriseId,
                    cancellationToken) ?? throw new RegistryNotFoundException(
                    "Patient",
                    review.CandidateEnterpriseId.ToString());
            }

            var subjectChanged =
                !subject.IsActive ||
                (review.SubjectVersion > 0 && subject.Version != review.SubjectVersion);
            var candidateChanged =
                candidate is not null &&
                (!candidate.IsActive ||
                 (review.CandidateVersion.HasValue &&
                  candidate.Version != review.CandidateVersion.Value));
            if (!subjectChanged && !candidateChanged)
            {
                throw new RegistryConcurrencyException(
                    "This review is still current because its enterprise identities have not changed.");
            }

            var superseded = review with
            {
                Status = ReviewCaseStatus.Superseded,
                DecisionReason = command.Reason,
                DecidedBy = context.ActorId,
                UpdatedAt = now,
                Version = review.Version + 1
            };
            await store.CommitAsync(
                context,
                new RegistryMutation(
                    [],
                    [],
                    [],
                    [superseded],
                    [CreateReviewAudit(
                        context,
                        review,
                        "review-supersede",
                        command.Reason,
                        now)],
                    [new ExpectedVersion(
                        RegistryEntityKind.ReviewCase,
                        review.Id.ToString(),
                        review.Version)]),
                cancellationToken);
            RegistryTelemetry.RecordReviewDecision(command.Decision, context.TenantId);
            return superseded;
        }

        await EnsureReviewEvidenceIsCurrentAsync(
            context,
            review,
            cancellationToken);

        if (command.Decision == ReviewDecision.Reject)
        {
            var rejected = review with
            {
                Status = ReviewCaseStatus.Rejected,
                DecisionReason = command.Reason,
                DecidedBy = context.ActorId,
                UpdatedAt = now,
                Version = review.Version + 1
            };
            await store.CommitAsync(
                context,
                new RegistryMutation(
                    [],
                    [],
                    [],
                    [rejected],
                    [CreateReviewAudit(context, review, "review-reject", command.Reason, now)],
                    [new ExpectedVersion(
                        RegistryEntityKind.ReviewCase,
                        review.Id.ToString(),
                        review.Version)]),
                cancellationToken);
            RegistryTelemetry.RecordReviewDecision(command.Decision, context.TenantId);
            return rejected;
        }

        var expectedDecision = review.Kind == ReviewCaseKind.Split
            ? ReviewDecision.Split
            : ReviewDecision.Link;
        if (command.Decision != expectedDecision)
        {
            throw new ArgumentException(
                review.Kind == ReviewCaseKind.Split
                    ? "A split review can only be split or rejected."
                    : "A duplicate review can only be linked or rejected.",
                nameof(command));
        }

        var approvals = review.Approvals ?? [];
        var requiredApprovals = await GetEffectiveRequiredApprovalsAsync(
            context,
            review,
            cancellationToken);
        var thresholdAlreadyMet = approvals.Count >= requiredApprovals;
        var actorAlreadyApproved = approvals.Any(approval =>
            string.Equals(approval.Actor, context.ActorId, StringComparison.Ordinal));
        if (actorAlreadyApproved && !thresholdAlreadyMet)
        {
            throw new RegistryAuthorisationException(
                "A reviewer cannot approve the same identity operation twice.");
        }

        var updatedApprovals = thresholdAlreadyMet
            ? approvals.ToArray()
            : approvals
                .Append(new ReviewApproval(
                    context.ActorId,
                    command.Decision,
                    command.Reason,
                    now))
                .ToArray();
        if (updatedApprovals.Length < requiredApprovals)
        {
            var awaitingApproval = review with
            {
                Status = ReviewCaseStatus.AwaitingSecondApproval,
                Approvals = updatedApprovals,
                RequiredApprovals = requiredApprovals,
                UpdatedAt = now,
                Version = review.Version + 1
            };
            await store.CommitAsync(
                context,
                new RegistryMutation(
                    [],
                    [],
                    [],
                    [awaitingApproval],
                    [CreateReviewAudit(
                        context,
                        review,
                        $"review-{command.Decision.ToString().ToLowerInvariant()}-proposed",
                        command.Reason,
                        now)],
                    [new ExpectedVersion(
                        RegistryEntityKind.ReviewCase,
                        review.Id.ToString(),
                        review.Version)]),
                cancellationToken);
            return awaitingApproval;
        }

        var updatedReview = review with
        {
            Status = command.Decision == ReviewDecision.Split
                ? ReviewCaseStatus.Split
                : ReviewCaseStatus.Linked,
            DecisionReason = command.Reason,
            DecidedBy = context.ActorId,
            Approvals = updatedApprovals,
            RequiredApprovals = requiredApprovals,
            UpdatedAt = now,
            Version = review.Version + 1
        };

        var mutation = command.Decision == ReviewDecision.Split
            ? await CreateSplitMutationAsync(context, review, updatedReview, now, cancellationToken)
            : await CreateLinkMutationAsync(context, review, updatedReview, now, cancellationToken);

        await store.CommitAsync(context, mutation, cancellationToken);
        RegistryTelemetry.RecordReviewDecision(command.Decision, context.TenantId);
        return updatedReview;
    }

    public async ValueTask<PatientIdentityView> GetPatientIdentityViewAsync(
        ActorContext context,
        EnterpriseId enterpriseId,
        CancellationToken cancellationToken)
    {
        EnsureRegistryReader(context);
        var canonical = await store.GetCanonicalPatientAsync(
            context,
            enterpriseId,
            cancellationToken) ?? throw new RegistryNotFoundException(
            "Patient",
            enterpriseId.ToString());
        var person = await store.GetPersonAsync(
            context,
            enterpriseId,
            cancellationToken) ?? throw new RegistryNotFoundException(
            "Person",
            enterpriseId.ToString());
        var sources = new List<SourcePatientRecord>(canonical.Sources.Count);
        foreach (var sourceKey in canonical.Sources)
        {
            var source = await store.GetSourcePatientAsync(context, sourceKey, cancellationToken);
            if (source is not null)
            {
                sources.Add(source);
            }
        }

        return new PatientIdentityView(
            canonical,
            person,
            sources.OrderByDescending(static source => source.SourceTrust)
                .ThenByDescending(static source => source.LastUpdated)
                .ThenBy(static source => source.Key.ToString(), StringComparer.Ordinal)
                .ToArray());
    }

    public async ValueTask<ReviewCaseDetail> GetReviewCaseDetailAsync(
        ActorContext context,
        Guid reviewCaseId,
        CancellationToken cancellationToken)
    {
        EnsureReviewer(context);
        var review = await store.GetReviewCaseAsync(context, reviewCaseId, cancellationToken)
            ?? throw new RegistryNotFoundException("ReviewCase", reviewCaseId.ToString());
        var subject = await GetPatientIdentityViewAsync(
            context,
            review.SubjectEnterpriseId,
            cancellationToken);
        PatientIdentityView? candidate = null;
        if (review.Kind != ReviewCaseKind.Split)
        {
            candidate = await GetPatientIdentityViewAsync(
                context,
                review.CandidateEnterpriseId,
                cancellationToken);
        }

        var effectiveRequiredApprovals = await GetEffectiveRequiredApprovalsAsync(
            context,
            review,
            cancellationToken,
            subject.CanonicalPatient,
            candidate?.CanonicalPatient);
        return new ReviewCaseDetail(
            review,
            subject,
            candidate,
            effectiveRequiredApprovals);
    }

    public async ValueTask<DuplicateSearchResult> FindDuplicateCandidatesAsync(
        ActorContext context,
        EnterpriseId subjectEnterpriseId,
        int count,
        CancellationToken cancellationToken)
    {
        EnsureReviewer(context);
        var subject = await store.GetCanonicalPatientAsync(
            context,
            subjectEnterpriseId,
            cancellationToken) ?? throw new RegistryNotFoundException(
            "Patient",
            subjectEnterpriseId.ToString());
        if (!subject.IsActive)
        {
            throw new RegistryConcurrencyException(
                "Duplicate searches require an active enterprise identity.");
        }

        var configuration = await configurationProvider.GetConfigurationAsync(
            context.TenantId,
            cancellationToken);
        var normalised = IdentityNormaliser.Normalise(subject.Profile);
        var blockingKeys = BlockingKeyGenerator.Generate(normalised, configuration);
        var candidates = await store.FindCandidatesAsync(
            context,
            blockingKeys,
            configuration.MatchingProfile.MaximumCandidates,
            cancellationToken);
        if (candidates.IsTruncated)
        {
            throw new CandidateLimitExceededException(
                configuration.MatchingProfile.MaximumCandidates);
        }

        var matches = candidates.Items
            .Where(candidate => candidate.EnterpriseId != subjectEnterpriseId)
            .Select(candidate => WeightedIdentityMatcher.Match(
                normalised,
                candidate,
                configuration.MatchingProfile))
            .Where(static result => result.Grade != MatchGrade.None)
            .OrderByDescending(static result => result.Score)
            .ThenBy(static result => result.Patient.EnterpriseId.Value)
            .Take(Math.Clamp(count, 1, configuration.MatchingProfile.MaximumResultCount))
            .ToArray();
        return new DuplicateSearchResult(
            subjectEnterpriseId,
            matches,
            candidates.Items.Count,
            configuration.MatchingProfile.Version);
    }

    public async ValueTask<ReviewCase> CreateDuplicateReviewCaseAsync(
        ActorContext context,
        CreateDuplicateReviewCommand command,
        CancellationToken cancellationToken)
    {
        EnsureReviewer(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Reason);
        if (command.SubjectEnterpriseId == command.CandidateEnterpriseId)
        {
            throw new ArgumentException(
                "A duplicate review requires two different enterprise identities.",
                nameof(command));
        }

        var subject = await store.GetCanonicalPatientAsync(
            context,
            command.SubjectEnterpriseId,
            cancellationToken) ?? throw new RegistryNotFoundException(
            "Patient",
            command.SubjectEnterpriseId.ToString());
        var candidate = await store.GetCanonicalPatientAsync(
            context,
            command.CandidateEnterpriseId,
            cancellationToken) ?? throw new RegistryNotFoundException(
            "Patient",
            command.CandidateEnterpriseId.ToString());
        if (!subject.IsActive || !candidate.IsActive)
        {
            throw new RegistryConcurrencyException(
                "Manual duplicate reviews require two active enterprise identities.");
        }

        if (subject.Version != command.SubjectVersion ||
            candidate.Version != command.CandidateVersion)
        {
            throw new RegistryConcurrencyException(
                "One of the selected identities changed before the review was created.");
        }

        var configuration = await configurationProvider.GetConfigurationAsync(
            context.TenantId,
            cancellationToken);
        var match = WeightedIdentityMatcher.Match(
            IdentityNormaliser.Normalise(subject.Profile),
            candidate,
            configuration.MatchingProfile);
        var now = timeProvider.GetUtcNow();
        var review = new ReviewCase(
            Guid.CreateVersion7(),
            subject.EnterpriseId,
            candidate.EnterpriseId,
            match.Score,
            match.Grade,
            match.Evidence,
            configuration.MatchingProfile.Version,
            ReviewCaseStatus.Pending,
            now,
            now,
            1,
            Kind: ReviewCaseKind.ManualDuplicate,
            RequiredApprovals: match.HasHardConflict
                ? 2
                : configuration.RequiredLinkApprovals,
            Approvals: [],
            SourcesToMove: [],
            SubjectVersion: subject.Version,
            CandidateVersion: candidate.Version,
            ApprovalPolicyLocked: match.HasHardConflict);
        await store.CommitAsync(
            context,
            new RegistryMutation(
                [],
                [],
                [],
                [review],
                [CreateReviewAudit(
                    context,
                    review,
                    "manual-duplicate-review-create",
                    command.Reason,
                    now)],
                []),
            cancellationToken);
        RegistryTelemetry.RecordReviewsCreated(1, context.TenantId);
        return review;
    }

    public async ValueTask<ReviewCase> CreateSplitReviewCaseAsync(
        ActorContext context,
        CreateSplitReviewCommand command,
        CancellationToken cancellationToken)
    {
        EnsureReviewer(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Reason);
        var identity = await GetPatientIdentityViewAsync(
            context,
            command.EnterpriseId,
            cancellationToken);
        if (!identity.CanonicalPatient.IsActive)
        {
            throw new RegistryConcurrencyException(
                "Only active enterprise identities can be split.");
        }

        if (identity.CanonicalPatient.Version != command.ExpectedVersion)
        {
            throw new RegistryConcurrencyException(
                "The enterprise identity changed before the split review was created.");
        }

        var selected = command.SourcesToMove.Distinct().ToArray();
        if (selected.Length == 0 ||
            selected.Length >= identity.CanonicalPatient.Sources.Count ||
            selected.Any(source => !identity.CanonicalPatient.Sources.Contains(source)))
        {
            throw new ArgumentException(
                "A split must move at least one, but not all, source records from the identity.",
                nameof(command));
        }

        var configuration = await configurationProvider.GetConfigurationAsync(
            context.TenantId,
            cancellationToken);
        var now = timeProvider.GetUtcNow();
        var review = new ReviewCase(
            Guid.CreateVersion7(),
            identity.CanonicalPatient.EnterpriseId,
            EnterpriseId.New(),
            1,
            MatchGrade.Certain,
            [new FieldEvidence(
                "manual-split",
                1,
                1,
                "reviewer-selected-source-records",
                $"{selected.Length} source record(s) selected")],
            configuration.MatchingProfile.Version,
            ReviewCaseStatus.Pending,
            now,
            now,
            1,
            Kind: ReviewCaseKind.Split,
            RequiredApprovals: 2,
            Approvals: [],
            SourcesToMove: selected,
            SubjectVersion: identity.CanonicalPatient.Version,
            ApprovalPolicyLocked: true);
        await store.CommitAsync(
            context,
            new RegistryMutation(
                [],
                [],
                [],
                [review],
                [CreateReviewAudit(
                    context,
                    review,
                    "manual-split-review-create",
                    command.Reason,
                    now)],
                []),
            cancellationToken);
        RegistryTelemetry.RecordReviewsCreated(1, context.TenantId);
        return review;
    }

    public async ValueTask<TenantSettings> GetTenantSettingsAsync(
        ActorContext context,
        CancellationToken cancellationToken)
    {
        EnsureTenantAdministrator(context, allowReadOnly: true);
        var stored = await store.GetTenantSettingsAsync(context, cancellationToken);
        if (stored is not null)
        {
            return stored;
        }

        var configuration = await configurationProvider.GetConfigurationAsync(
            context.TenantId,
            cancellationToken);
        return new TenantSettings(
            context.TenantId,
            configuration.MatchingProfile.Version,
            configuration.MatchingProfile.PossibleThreshold,
            configuration.MatchingProfile.ProbableThreshold,
            configuration.RequiredLinkApprovals,
            configuration.SourceTrust
                .OrderBy(static pair => pair.Key.Value, StringComparer.Ordinal)
                .Select(pair => new SourceSystemSettings(
                    pair.Key,
                    pair.Value,
                    configuration.AuthoritativeSources.Contains(pair.Key)))
                .ToArray(),
            DateTimeOffset.MinValue,
            "deployment-configuration",
            0);
    }

    public async ValueTask<TenantSettings> UpdateTenantSettingsAsync(
        ActorContext context,
        UpdateTenantSettingsCommand command,
        CancellationToken cancellationToken)
    {
        EnsureTenantAdministrator(context, allowReadOnly: false);
        ValidateTenantSettings(command);
        var current = await store.GetTenantSettingsAsync(context, cancellationToken);
        var currentVersion = current?.Version ?? 0;
        if (currentVersion != command.ExpectedVersion)
        {
            throw new RegistryConcurrencyException(
                "Tenant settings changed before this update was submitted.");
        }

        var now = timeProvider.GetUtcNow();
        var settings = new TenantSettings(
            context.TenantId,
            command.MatchingProfileVersion.Trim(),
            command.PossibleThreshold,
            command.ProbableThreshold,
            command.RequiredLinkApprovals,
            command.Sources
                .DistinctBy(static source => source.SourceSystem)
                .OrderBy(static source => source.SourceSystem.Value, StringComparer.Ordinal)
                .ToArray(),
            now,
            context.ActorId,
            currentVersion + 1);
        IReadOnlyList<ExpectedVersion> expectedVersions = current is null
            ? []
            :
            [
                new ExpectedVersion(
                    RegistryEntityKind.TenantSettings,
                    context.TenantId.Value,
                    current.Version)
            ];
        await store.CommitAsync(
            context,
            new RegistryMutation(
                [],
                [],
                [],
                [],
                [new AuditRecord(
                    Guid.CreateVersion7(),
                    "tenant-settings-update",
                    context.ActorId,
                    "success",
                    command.Reason,
                    null,
                    null,
                    now,
                    context.CorrelationId)],
                expectedVersions,
                TenantSettings: settings),
            cancellationToken);
        configurationProvider.Invalidate(context.TenantId);
        return settings;
    }

    public ValueTask<Page<AuditRecord>> SearchAuditRecordsAsync(
        ActorContext context,
        AuditRecordSearch search,
        CancellationToken cancellationToken)
    {
        EnsureAuditor(context);
        return store.SearchAuditRecordsAsync(context, search, cancellationToken);
    }

    public async ValueTask<RegistryOperationalSummary> GetOperationalSummaryAsync(
        ActorContext context,
        CancellationToken cancellationToken)
    {
        EnsureOperationsReader(context);
        var healthTask = store.CheckHealthAsync(cancellationToken).AsTask();
        var pendingTask = store.SearchReviewCasesAsync(
            context,
            new ReviewCaseSearch(Status: ReviewCaseStatus.Pending, Count: 100),
            cancellationToken).AsTask();
        var awaitingTask = store.SearchReviewCasesAsync(
            context,
            new ReviewCaseSearch(
                Status: ReviewCaseStatus.AwaitingSecondApproval,
                Count: 100),
            cancellationToken).AsTask();
        var auditTask = store.SearchAuditRecordsAsync(
            context,
            new AuditRecordSearch(
                From: timeProvider.GetUtcNow().AddHours(-24),
                Count: 100),
            cancellationToken).AsTask();
        var settingsTask = GetTenantSettingsAsync(context, cancellationToken).AsTask();
        await Task.WhenAll(
            healthTask,
            pendingTask,
            awaitingTask,
            auditTask,
            settingsTask);

        var health = await healthTask;
        var pending = await pendingTask;
        var awaiting = await awaitingTask;
        var audit = await auditTask;
        var settings = await settingsTask;
        return new RegistryOperationalSummary(
            health,
            pending.Items.Count,
            awaiting.Items.Count,
            audit.Items.Count,
            settings,
            timeProvider.GetUtcNow());
    }

    public ValueTask<Page<ReviewCase>> SearchReviewCasesAsync(
        ActorContext context,
        ReviewCaseSearch search,
        CancellationToken cancellationToken)
    {
        EnsureReviewer(context);
        return store.SearchReviewCasesAsync(context, search, cancellationToken);
    }

    public ValueTask<ReviewCase?> GetReviewCaseAsync(
        ActorContext context,
        Guid id,
        CancellationToken cancellationToken)
    {
        EnsureReviewer(context);
        return store.GetReviewCaseAsync(context, id, cancellationToken);
    }

    public ValueTask<Page<CanonicalPatient>> SearchCanonicalPatientsAsync(
        ActorContext context,
        CanonicalPatientSearch search,
        CancellationToken cancellationToken)
    {
        EnsureRegistryReader(context);
        return store.SearchCanonicalPatientsAsync(context, search, cancellationToken);
    }

    public ValueTask<CanonicalPatient?> GetCanonicalPatientAsync(
        ActorContext context,
        EnterpriseId id,
        CancellationToken cancellationToken)
    {
        EnsureRegistryReader(context);
        return store.GetCanonicalPatientAsync(context, id, cancellationToken);
    }

    public ValueTask<EnterprisePerson?> GetPersonAsync(
        ActorContext context,
        EnterpriseId id,
        CancellationToken cancellationToken)
    {
        EnsureReviewer(context);
        return store.GetPersonAsync(context, id, cancellationToken);
    }

    public ValueTask<Page<EnterprisePerson>> SearchPersonsAsync(
        ActorContext context,
        PersonSearch search,
        CancellationToken cancellationToken)
    {
        EnsureReviewer(context);
        return store.SearchPersonsAsync(context, search, cancellationToken);
    }

    public ValueTask<SourcePatientRecord?> GetSourcePatientByResourceIdAsync(
        ActorContext context,
        string resourceId,
        CancellationToken cancellationToken) =>
        store.GetSourcePatientByResourceIdAsync(context, resourceId, cancellationToken);

    public ValueTask<IngestionReceipt?> GetIngestionReceiptAsync(
        ActorContext context,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        store.GetReceiptAsync(context, idempotencyKey, cancellationToken);

    public ValueTask<RegistryCommitResult> RecordIngestionReceiptAsync(
        ActorContext context,
        IngestionReceipt receipt,
        CancellationToken cancellationToken) =>
        store.CommitAsync(
            context,
            RegistryMutation.Empty with { Receipt = receipt },
            cancellationToken);

    private async ValueTask<int> GetEffectiveRequiredApprovalsAsync(
        ActorContext context,
        ReviewCase review,
        CancellationToken cancellationToken,
        CanonicalPatient? subject = null,
        CanonicalPatient? candidate = null)
    {
        var recordedRequirement = Math.Clamp(review.RequiredApprovals, 1, 2);
        if (review.Kind == ReviewCaseKind.Split || review.ApprovalPolicyLocked)
        {
            return recordedRequirement;
        }

        var configuration = await configurationProvider.GetConfigurationAsync(
            context.TenantId,
            cancellationToken);
        var currentRequirement = Math.Clamp(configuration.RequiredLinkApprovals, 1, 2);
        if (currentRequirement >= recordedRequirement)
        {
            return recordedRequirement;
        }

        subject ??= await store.GetCanonicalPatientAsync(
            context,
            review.SubjectEnterpriseId,
            cancellationToken) ?? throw new RegistryNotFoundException(
            "Patient",
            review.SubjectEnterpriseId.ToString());
        candidate ??= await store.GetCanonicalPatientAsync(
            context,
            review.CandidateEnterpriseId,
            cancellationToken) ?? throw new RegistryNotFoundException(
            "Patient",
            review.CandidateEnterpriseId.ToString());
        var match = WeightedIdentityMatcher.Match(
            IdentityNormaliser.Normalise(subject.Profile),
            candidate,
            configuration.MatchingProfile);
        return match.HasHardConflict
            ? recordedRequirement
            : currentRequirement;
    }

    private async ValueTask EnsureReviewEvidenceIsCurrentAsync(
        ActorContext context,
        ReviewCase review,
        CancellationToken cancellationToken)
    {
        var subject = await store.GetCanonicalPatientAsync(
            context,
            review.SubjectEnterpriseId,
            cancellationToken) ?? throw new RegistryNotFoundException(
            "Patient",
            review.SubjectEnterpriseId.ToString());
        CanonicalPatient? candidate = null;
        if (review.Kind != ReviewCaseKind.Split)
        {
            candidate = await store.GetCanonicalPatientAsync(
                context,
                review.CandidateEnterpriseId,
                cancellationToken) ?? throw new RegistryNotFoundException(
                "Patient",
                review.CandidateEnterpriseId.ToString());
        }

        if (!subject.IsActive || candidate is { IsActive: false })
        {
            throw new RegistryConcurrencyException(
                "This review has been superseded because one or more enterprise identities were replaced. Close it as superseded and review the current identities.");
        }

        if ((review.SubjectVersion > 0 && subject.Version != review.SubjectVersion) ||
            (candidate is not null &&
             review.CandidateVersion.HasValue &&
             candidate.Version != review.CandidateVersion.Value))
        {
            throw new RegistryConcurrencyException(
                "An enterprise identity changed after the review was created. Close this review as superseded and create a fresh comparison.");
        }
    }

    private async ValueTask<RegistryMutation> CreateSplitMutationAsync(
        ActorContext context,
        ReviewCase review,
        ReviewCase updatedReview,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var subject = await store.GetCanonicalPatientAsync(
            context,
            review.SubjectEnterpriseId,
            cancellationToken) ?? throw new RegistryNotFoundException(
            "Patient",
            review.SubjectEnterpriseId.ToString());
        var subjectPerson = await store.GetPersonAsync(
            context,
            review.SubjectEnterpriseId,
            cancellationToken) ?? throw new RegistryNotFoundException(
            "Person",
            review.SubjectEnterpriseId.ToString());
        if (review.SubjectVersion > 0 && subject.Version != review.SubjectVersion)
        {
            throw new RegistryConcurrencyException(
                "The enterprise identity changed after the split review was created.");
        }

        var selectedKeys = (review.SourcesToMove ?? []).Distinct().ToHashSet();
        if (selectedKeys.Count == 0 || selectedKeys.Count >= subject.Sources.Count)
        {
            throw new RegistryConcurrencyException(
                "The split review no longer contains a valid source-record selection.");
        }

        var selectedSources = new List<SourcePatientRecord>(selectedKeys.Count);
        var remainingSources = new List<SourcePatientRecord>(
            subject.Sources.Count - selectedKeys.Count);
        foreach (var sourceKey in subject.Sources)
        {
            var source = await store.GetSourcePatientAsync(context, sourceKey, cancellationToken)
                ?? throw new RegistryNotFoundException("Patient", sourceKey.ToString());
            if (selectedKeys.Contains(sourceKey))
            {
                selectedSources.Add(source);
            }
            else
            {
                remainingSources.Add(source);
            }
        }

        var configuration = await configurationProvider.GetConfigurationAsync(
            context.TenantId,
            cancellationToken);
        var remainingProfile = BuildSurvivorshipProfile(remainingSources);
        var selectedProfile = BuildSurvivorshipProfile(selectedSources);
        var remainingCanonical = subject with
        {
            Profile = remainingProfile,
            Sources = remainingSources.Select(static source => source.Key).ToArray(),
            BlockingKeys = BlockingKeyGenerator.Generate(
                IdentityNormaliser.Normalise(remainingProfile),
                configuration),
            SurvivorshipTrust = remainingSources.Max(static source => source.SourceTrust),
            LastUpdated = now,
            Version = subject.Version + 1
        };
        var splitCanonical = new CanonicalPatient(
            review.CandidateEnterpriseId,
            selectedProfile,
            selectedSources.Select(static source => source.Key).ToArray(),
            BlockingKeyGenerator.Generate(
                IdentityNormaliser.Normalise(selectedProfile),
                configuration),
            selectedSources.Max(static source => source.SourceTrust),
            now,
            now,
            1);
        var remainingPerson = subjectPerson with
        {
            Links = subjectPerson.Links
                .Where(link => !selectedKeys.Contains(link.Source))
                .ToArray(),
            LastUpdated = now,
            Version = subjectPerson.Version + 1
        };
        var splitPerson = new EnterprisePerson(
            review.CandidateEnterpriseId,
            subjectPerson.Links
                .Where(link => selectedKeys.Contains(link.Source))
                .Select(link => link with
                {
                    LinkedAt = now,
                    Reason = $"Split approved in review {review.Id:D}"
                })
                .ToArray(),
            now,
            now,
            1);
        var movedSources = selectedSources.Select(source => source with
        {
            EnterpriseId = review.CandidateEnterpriseId,
            LastUpdated = now,
            Version = source.Version + 1
        }).ToArray();
        var expected = new List<ExpectedVersion>
        {
            new(RegistryEntityKind.ReviewCase, review.Id.ToString(), review.Version),
            new(RegistryEntityKind.CanonicalPatient, subject.EnterpriseId.ToString(), subject.Version),
            new(RegistryEntityKind.Person, subjectPerson.EnterpriseId.ToString(), subjectPerson.Version)
        };
        expected.AddRange(movedSources.Select(source =>
            new ExpectedVersion(
                RegistryEntityKind.SourcePatient,
                source.Key.ToString(),
                source.Version - 1)));
        return new RegistryMutation(
            movedSources,
            [remainingCanonical, splitCanonical],
            [remainingPerson, splitPerson],
            [updatedReview],
            [CreateReviewAudit(
                context,
                review,
                "review-split",
                updatedReview.DecisionReason!,
                now)],
            expected);
    }

    private async ValueTask<RegistryMutation> CreateLinkMutationAsync(
        ActorContext context,
        ReviewCase review,
        ReviewCase updatedReview,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var subject = await store.GetCanonicalPatientAsync(
            context,
            review.SubjectEnterpriseId,
            cancellationToken)
            ?? throw new RegistryNotFoundException("Patient", review.SubjectEnterpriseId.ToString());
        var candidate = await store.GetCanonicalPatientAsync(
            context,
            review.CandidateEnterpriseId,
            cancellationToken)
            ?? throw new RegistryNotFoundException("Patient", review.CandidateEnterpriseId.ToString());
        if (!subject.IsActive || !candidate.IsActive)
        {
            throw new RegistryConcurrencyException(
                "This review has been superseded because one or more enterprise identities were replaced. Close it as superseded and review the current identities.");
        }

        if ((review.SubjectVersion > 0 && subject.Version != review.SubjectVersion) ||
            (review.CandidateVersion.HasValue &&
             candidate.Version != review.CandidateVersion.Value))
        {
            throw new RegistryConcurrencyException(
                "An enterprise identity changed after the review was created.");
        }

        var subjectPerson = await store.GetPersonAsync(context, subject.EnterpriseId, cancellationToken)
            ?? throw new RegistryNotFoundException("Person", subject.EnterpriseId.ToString());
        var candidatePerson = await store.GetPersonAsync(context, candidate.EnterpriseId, cancellationToken)
            ?? throw new RegistryNotFoundException("Person", candidate.EnterpriseId.ToString());
        var configuration = await configurationProvider.GetConfigurationAsync(
            context.TenantId,
            cancellationToken);
        var mergedProfile = SurvivorshipService.Merge(
            candidate.Profile,
            candidate.SurvivorshipTrust,
            subject.Profile,
            subject.SurvivorshipTrust,
            candidate.LastUpdated,
            subject.LastUpdated,
            candidate.EnterpriseId.ToString(),
            subject.EnterpriseId.ToString());
        var mergedCandidate = candidate with
        {
            Profile = mergedProfile,
            Sources = candidate.Sources.Concat(subject.Sources).Distinct().ToArray(),
            BlockingKeys = BlockingKeyGenerator.Generate(IdentityNormaliser.Normalise(mergedProfile), configuration),
            SurvivorshipTrust = Math.Max(candidate.SurvivorshipTrust, subject.SurvivorshipTrust),
            LastUpdated = now,
            Version = candidate.Version + 1
        };
        var retiredSubject = subject with
        {
            IsActive = false,
            ReplacedBy = candidate.EnterpriseId,
            LastUpdated = now,
            Version = subject.Version + 1
        };
        var mergedPerson = candidatePerson with
        {
            Links = candidatePerson.Links.Concat(subjectPerson.Links).Distinct().ToArray(),
            LastUpdated = now,
            Version = candidatePerson.Version + 1
        };
        var retiredPerson = subjectPerson with
        {
            IsActive = false,
            ReplacedBy = candidate.EnterpriseId,
            LastUpdated = now,
            Version = subjectPerson.Version + 1
        };

        var movedSources = new List<SourcePatientRecord>(subject.Sources.Count);
        foreach (var sourceKey in subject.Sources)
        {
            var source = await store.GetSourcePatientAsync(context, sourceKey, cancellationToken);
            if (source is not null)
            {
                movedSources.Add(source with
                {
                    EnterpriseId = candidate.EnterpriseId,
                    LastUpdated = now,
                    Version = source.Version + 1
                });
            }
        }

        var expected = new List<ExpectedVersion>
        {
            new(RegistryEntityKind.ReviewCase, review.Id.ToString(), review.Version),
            new(RegistryEntityKind.CanonicalPatient, subject.EnterpriseId.ToString(), subject.Version),
            new(RegistryEntityKind.CanonicalPatient, candidate.EnterpriseId.ToString(), candidate.Version),
            new(RegistryEntityKind.Person, subjectPerson.EnterpriseId.ToString(), subjectPerson.Version),
            new(RegistryEntityKind.Person, candidatePerson.EnterpriseId.ToString(), candidatePerson.Version)
        };
        expected.AddRange(movedSources.Select(source =>
            new ExpectedVersion(
                RegistryEntityKind.SourcePatient,
                source.Key.ToString(),
                source.Version - 1)));

        return new RegistryMutation(
            movedSources,
            [mergedCandidate, retiredSubject],
            [mergedPerson, retiredPerson],
            [updatedReview],
            [CreateReviewAudit(context, review, "review-link", updatedReview.DecisionReason!, now)],
            expected);
    }

    private static IdentityProfile BuildSurvivorshipProfile(
        List<SourcePatientRecord> sources)
    {
        if (sources.Count == 0)
        {
            throw new InvalidOperationException(
                "A canonical identity requires at least one source record.");
        }

        var ordered = sources
            .OrderByDescending(static source => source.SourceTrust)
            .ThenByDescending(static source => source.LastUpdated)
            .ThenBy(static source => source.Key.ToString(), StringComparer.Ordinal)
            .ToArray();
        var winner = ordered[0];
        var profile = winner.Profile;
        foreach (var source in ordered.Skip(1))
        {
            profile = SurvivorshipService.Merge(
                profile,
                winner.SourceTrust,
                source.Profile,
                source.SourceTrust,
                winner.LastUpdated,
                source.LastUpdated,
                winner.Key.ToString(),
                source.Key.ToString());
        }

        return profile;
    }

    private static AuditRecord CreateReviewAudit(
        ActorContext context,
        ReviewCase review,
        string action,
        string reason,
        DateTimeOffset now) =>
        new(
            Guid.CreateVersion7(),
            action,
            context.ActorId,
            "success",
            reason,
            review.SubjectEnterpriseId,
            null,
            now,
            context.CorrelationId);

    private static IReadOnlyList<T> AppendDistinct<T>(IReadOnlyList<T> current, T item) =>
        current.Contains(item) ? current : current.Append(item).ToArray();

    private static PersonLink[] UpsertLink(
        IReadOnlyList<PersonLink> current,
        PersonLink link) =>
        current.Where(existing => existing.Source != link.Source).Append(link).ToArray();

    private static List<ExpectedVersion> CreateExpectedVersions(
        SourcePatientRecord? source,
        CanonicalPatient? canonical,
        EnterprisePerson? person)
    {
        var expected = new List<ExpectedVersion>(3);
        if (source is not null)
        {
            expected.Add(new ExpectedVersion(
                RegistryEntityKind.SourcePatient,
                source.Key.ToString(),
                source.Version));
        }

        if (canonical is not null)
        {
            expected.Add(new ExpectedVersion(
                RegistryEntityKind.CanonicalPatient,
                canonical.EnterpriseId.ToString(),
                canonical.Version));
        }

        if (person is not null)
        {
            expected.Add(new ExpectedVersion(
                RegistryEntityKind.Person,
                person.EnterpriseId.ToString(),
                person.Version));
        }

        return expected;
    }

    private static void EnsureSourceActor(ActorContext context, SourceSystemId sourceSystem)
    {
        if (context.SourceSystem is null ||
            context.SourceSystem.Value != sourceSystem)
        {
            throw new RegistryAuthorisationException(
                "The authenticated source system does not own this patient record.");
        }
    }

    private static void EnsureRegistryReader(ActorContext context)
    {
        if (context.HasScope("mpi.match") ||
            context.HasScope("mpi.review") ||
            context.HasScope("mpi.admin") ||
            context.Scopes.Any(static scope =>
                scope.StartsWith("system/Patient.", StringComparison.Ordinal) &&
                (scope.EndsWith(".read", StringComparison.Ordinal) ||
                 scope.EndsWith(".rs", StringComparison.Ordinal) ||
                 scope.EndsWith(".*", StringComparison.Ordinal))))
        {
            return;
        }

        throw new RegistryAuthorisationException(
            "Patient registry read permission is required.");
    }

    private static void EnsureReviewer(ActorContext context)
    {
        if (!context.HasScope("mpi.review") && !context.HasScope("mpi.admin"))
        {
            throw new RegistryAuthorisationException("The mpi.review scope is required.");
        }
    }

    private static void EnsureAuditor(ActorContext context)
    {
        if (!context.HasScope("mpi.audit") && !context.HasScope("mpi.admin"))
        {
            throw new RegistryAuthorisationException("The mpi.audit scope is required.");
        }
    }

    private static void EnsureOperationsReader(ActorContext context)
    {
        if (!context.HasScope("mpi.operations") &&
            !context.HasScope("mpi.review") &&
            !context.HasScope("mpi.admin"))
        {
            throw new RegistryAuthorisationException(
                "The mpi.operations scope is required.");
        }
    }

    private static void EnsureTenantAdministrator(
        ActorContext context,
        bool allowReadOnly)
    {
        var permitted = context.HasScope("mpi.admin") ||
                        context.HasScope("mpi.config.write") ||
                        (allowReadOnly &&
                         (context.HasScope("mpi.config.read") ||
                          context.HasScope("mpi.operations")));
        if (!permitted)
        {
            throw new RegistryAuthorisationException(
                allowReadOnly
                    ? "Tenant configuration read permission is required."
                    : "The mpi.config.write scope is required.");
        }
    }

    private static void ValidateTenantSettings(UpdateTenantSettingsCommand command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.MatchingProfileVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Reason);
        if (command.MatchingProfileVersion.Length > 64)
        {
            throw new ArgumentException(
                "The matching-profile version cannot exceed 64 characters.",
                nameof(command));
        }

        if (command.PossibleThreshold is < 0 or > 1 ||
            command.ProbableThreshold is < 0 or > 1 ||
            command.PossibleThreshold >= command.ProbableThreshold)
        {
            throw new ArgumentException(
                "Matching thresholds must be between zero and one, with possible below probable.",
                nameof(command));
        }

        if (command.RequiredLinkApprovals is < 1 or > 2)
        {
            throw new ArgumentException(
                "Required link approvals must be one or two.",
                nameof(command));
        }

        if (command.Sources.Count == 0 ||
            command.Sources.Select(static source => source.SourceSystem).Distinct().Count() !=
            command.Sources.Count ||
            command.Sources.Any(static source => source.Trust is < 0 or > 100))
        {
            throw new ArgumentException(
                "Source systems must be unique and use trust values between zero and 100.",
                nameof(command));
        }
    }

    private static void ValidateIdempotency(UpsertPatientCommand command)
    {
        if (command.IdempotencyKey is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(command.IdempotencyKey) ||
            command.IdempotencyKey.Length > 512)
        {
            throw new ArgumentException(
                "The idempotency key must contain 1-512 characters.",
                nameof(command));
        }

        if (string.IsNullOrWhiteSpace(command.PayloadDigest) ||
            command.PayloadDigest.Length > 128)
        {
            throw new ArgumentException(
                "An idempotent command requires a bounded payload digest.",
                nameof(command));
        }
    }

    private static IdentityProfile ApplyIdentifierAuthority(
        IdentityProfile profile,
        MatchingProfile matchingProfile,
        bool isAuthoritative)
    {
        var identifiers = profile.Identifiers.Select(identifier =>
        {
            var authoritativeSystem =
                matchingProfile.AuthoritativeIdentifierSystems.Contains(identifier.System);
            var valid = !string.Equals(
                            identifier.System,
                            NhsNumberValidator.IdentifierSystem,
                            StringComparison.Ordinal) ||
                        NhsNumberValidator.IsValid(identifier.Value);
            var trusted = isAuthoritative && authoritativeSystem && valid;
            return identifier with
            {
                IsVerified = trusted,
                IsAuthoritative = trusted
            };
        }).ToArray();
        return profile with { Identifiers = identifiers };
    }
}
