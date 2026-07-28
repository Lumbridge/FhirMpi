using System.Net;
using System.Security.Cryptography;
using System.Text;
using UnifyEmpi.Application.Configuration;
using UnifyEmpi.Application.Matching;
using UnifyEmpi.Application.Normalisation;
using UnifyEmpi.Domain;
using UnifyEmpi.Storage.Abstractions;

namespace UnifyEmpi.Application;

public sealed class RegistryMaintenanceService(
    IIdentityRegistryStore store,
    ITenantConfigurationProvider configurationProvider,
    IExternalPatientSourceRegistry externalSources,
    RegistryService registry,
    TimeProvider timeProvider)
{
    private static readonly RegistryMaintenanceJobStatus[] ActiveStatuses =
    [
        RegistryMaintenanceJobStatus.Queued,
        RegistryMaintenanceJobStatus.Running
    ];

    public async ValueTask<RegistryMaintenanceJob> StartReindexAsync(
        ActorContext context,
        StartReindexCommand command,
        CancellationToken cancellationToken)
    {
        EnsureMaintenanceAdministrator(context);
        ValidateStart(command.Reason, command.BatchSize);
        await EnsureNoActiveJobAsync(
            context,
            RegistryMaintenanceJobKind.Reindex,
            null,
            cancellationToken);
        var configuration = await configurationProvider.GetConfigurationAsync(
            context.TenantId,
            cancellationToken);
        var now = timeProvider.GetUtcNow();
        var job = new RegistryMaintenanceJob(
            Guid.CreateVersion7(),
            context.TenantId,
            RegistryMaintenanceJobKind.Reindex,
            RegistryMaintenanceJobStatus.Queued,
            RegistryMaintenanceJobPhase.Validating,
            RegistryMaintenanceTrigger.Manual,
            context.ActorId,
            command.Reason.Trim(),
            now,
            MaintenanceConfigurationFingerprint.Create(configuration),
            configuration.MatchingProfile.Version,
            Math.Clamp(command.BatchSize, 1, 25),
            1);
        await CreateJobAsync(context, job, cancellationToken);
        return job;
    }

    public async ValueTask<RegistryMaintenanceJob> StartPopulationReconciliationAsync(
        ActorContext context,
        StartPopulationReconciliationCommand command,
        CancellationToken cancellationToken)
    {
        EnsureMaintenanceAdministrator(context);
        ValidateStart(command.Reason, command.BatchSize);
        if (command.ExternalSourceSystem is { } sourceSystem &&
            externalSources.Find(context.TenantId, sourceSystem) is null)
        {
            throw new ArgumentException(
                $"External FHIR source '{sourceSystem}' is not configured for this tenant.",
                nameof(command));
        }

        await EnsureNoActiveJobAsync(
            context,
            RegistryMaintenanceJobKind.PopulationReconciliation,
            command.ExternalSourceSystem,
            cancellationToken);
        var configuration = await configurationProvider.GetConfigurationAsync(
            context.TenantId,
            cancellationToken);
        var now = timeProvider.GetUtcNow();
        var job = new RegistryMaintenanceJob(
            command.JobId ?? Guid.CreateVersion7(),
            context.TenantId,
            RegistryMaintenanceJobKind.PopulationReconciliation,
            RegistryMaintenanceJobStatus.Queued,
            command.ExternalSourceSystem.HasValue
                ? RegistryMaintenanceJobPhase.Importing
                : RegistryMaintenanceJobPhase.Rebuilding,
            command.Trigger,
            context.ActorId,
            command.Reason.Trim(),
            now,
            MaintenanceConfigurationFingerprint.Create(configuration),
            configuration.MatchingProfile.Version,
            Math.Clamp(command.BatchSize, 1, 25),
            1)
        {
            ExternalSourceSystem = command.ExternalSourceSystem,
            ScheduleKey = command.ScheduleKey,
            WindowStart = command.ChangedSince,
            WindowEnd = now
        };
        try
        {
            await CreateJobAsync(context, job, cancellationToken);
            return job;
        }
        catch (RegistryConcurrencyException) when (command.JobId.HasValue)
        {
            var existing = await store.GetMaintenanceJobAsync(
                context,
                job.Id,
                cancellationToken);
            if (existing is null)
            {
                throw;
            }

            return existing;
        }
    }

    public ValueTask<RegistryMaintenanceJob?> GetJobAsync(
        ActorContext context,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        EnsureMaintenanceReader(context);
        return store.GetMaintenanceJobAsync(context, jobId, cancellationToken);
    }

    public ValueTask<Page<RegistryMaintenanceJob>> SearchJobsAsync(
        ActorContext context,
        MaintenanceJobSearch search,
        CancellationToken cancellationToken)
    {
        EnsureMaintenanceReader(context);
        return store.SearchMaintenanceJobsAsync(context, search, cancellationToken);
    }

    public async ValueTask<RegistryMaintenanceJob> CancelJobAsync(
        ActorContext context,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        EnsureMaintenanceAdministrator(context);
        var job = await store.GetMaintenanceJobAsync(context, jobId, cancellationToken) ??
                  throw new RegistryNotFoundException("MaintenanceJob", jobId.ToString("D"));
        if (IsTerminal(job.Status))
        {
            return job;
        }

        var now = timeProvider.GetUtcNow();
        var updated = job with
        {
            CancellationRequested = true,
            Status = job.Status == RegistryMaintenanceJobStatus.Queued
                ? RegistryMaintenanceJobStatus.Cancelled
                : job.Status,
            CompletedAt = job.Status == RegistryMaintenanceJobStatus.Queued ? now : null,
            LeaseOwner = null,
            LeaseExpiresAt = null,
            Version = job.Version + 1
        };
        await CommitJobUpdateAsync(
            context,
            job,
            updated,
            updated.Status == RegistryMaintenanceJobStatus.Cancelled
                ? [CreateJobAudit(context, updated, "maintenance-job-cancel", now)]
                : [],
            cancellationToken);
        return updated;
    }

    public async ValueTask<bool> ProcessNextBatchAsync(
        ActorContext context,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        EnsureMaintenanceAdministrator(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        if (leaseDuration < TimeSpan.FromSeconds(10))
        {
            throw new ArgumentOutOfRangeException(
                nameof(leaseDuration),
                "Maintenance leases must be at least ten seconds.");
        }

        var candidates = new List<RegistryMaintenanceJob>();
        foreach (var status in ActiveStatuses)
        {
            var page = await store.SearchMaintenanceJobsAsync(
                context,
                new MaintenanceJobSearch(Status: status, Count: 25),
                cancellationToken);
            candidates.AddRange(page.Items);
        }

        foreach (var job in candidates
                     .Where(job => job.NextAttemptAt is null ||
                                   job.NextAttemptAt <= timeProvider.GetUtcNow())
                     .OrderBy(static job => job.RequestedAt)
                     .ThenBy(static job => job.Id))
        {
            if (await ProcessJobBatchAsync(
                    context,
                    job.Id,
                    workerId,
                    leaseDuration,
                    cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    public async ValueTask<bool> ProcessJobBatchAsync(
        ActorContext context,
        Guid jobId,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        EnsureMaintenanceAdministrator(context);
        var leased = await TryAcquireLeaseAsync(
            context,
            jobId,
            workerId,
            leaseDuration,
            cancellationToken);
        if (leased is null)
        {
            return false;
        }

        if (leased.CancellationRequested)
        {
            await CompleteCancellationAsync(context, leased, cancellationToken);
            return true;
        }

        try
        {
            var configuration = await configurationProvider.GetConfigurationAsync(
                context.TenantId,
                cancellationToken);
            if (!string.Equals(
                    leased.ConfigurationFingerprint,
                    MaintenanceConfigurationFingerprint.Create(configuration),
                    StringComparison.Ordinal))
            {
                await FailJobAsync(
                    context,
                    leased,
                    "The tenant matching configuration changed while the job was running. Start a new job against the current configuration.",
                    cancellationToken);
                return true;
            }

            if (leased.Kind == RegistryMaintenanceJobKind.Reindex)
            {
                await ProcessReindexBatchAsync(context, leased, configuration, cancellationToken);
            }
            else
            {
                await ProcessReconciliationBatchAsync(
                    context,
                    leased,
                    configuration,
                    cancellationToken);
            }

            return true;
        }
        catch (RegistryConcurrencyException)
        {
            await ReleaseForRetryAsync(
                context,
                leased.Id,
                "A concurrent registry update was detected; the batch will be retried.",
                TimeSpan.FromSeconds(2),
                cancellationToken);
            return true;
        }
        catch (HttpRequestException exception) when (IsTransient(exception.StatusCode))
        {
            var delay = TimeSpan.FromSeconds(
                Math.Min(300, Math.Pow(2, Math.Min(8, leased.FailedItems + 1))));
            await ReleaseForRetryAsync(
                context,
                leased.Id,
                "The external FHIR source was temporarily unavailable; the batch will be retried.",
                delay,
                cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await FailJobAsync(
                context,
                leased,
                SafeFailureMessage(exception),
                cancellationToken);
            return true;
        }
    }

    private async ValueTask ProcessReindexBatchAsync(
        ActorContext context,
        RegistryMaintenanceJob job,
        TenantMatchingConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (job.Phase == RegistryMaintenanceJobPhase.Validating)
        {
            var page = await store.SearchCanonicalPatientsAsync(
                context,
                new CanonicalPatientSearch(Count: job.BatchSize, Cursor: job.Cursor),
                cancellationToken);
            foreach (var patient in page.Items)
            {
                var target = GenerateBlockingKeys(patient.Profile, configuration);
                if (patient.BlockingKeys.Count > 0 &&
                    !patient.BlockingKeys.Intersect(target).Any())
                {
                    await FailJobAsync(
                        context,
                        job,
                        "Online re-index safety validation failed because the old and target indexes do not overlap. Stage the change by retaining the previous HMAC key and enabling the union of old and new blocking rules, then retry.",
                        cancellationToken);
                    return;
                }
            }

            var updated = ReleaseLease(job with
            {
                Cursor = page.NextCursor,
                Validated = job.Validated + page.Items.Count,
                Phase = page.NextCursor is null
                    ? RegistryMaintenanceJobPhase.Rebuilding
                    : RegistryMaintenanceJobPhase.Validating,
                LastError = null,
                Version = job.Version + 1
            });
            if (page.NextCursor is null)
            {
                updated = updated with { Cursor = null };
            }

            await CommitJobUpdateAsync(context, job, updated, [], cancellationToken);
            return;
        }

        if (job.Phase != RegistryMaintenanceJobPhase.Rebuilding)
        {
            throw new InvalidOperationException("The re-index job is in an invalid phase.");
        }

        var patientPage = await store.SearchCanonicalPatientsAsync(
            context,
            new CanonicalPatientSearch(Count: job.BatchSize, Cursor: job.Cursor),
            cancellationToken);
        var now = timeProvider.GetUtcNow();
        var changed = new List<CanonicalPatient>();
        var expected = new List<ExpectedVersion>();
        foreach (var patient in patientPage.Items)
        {
            var target = GenerateBlockingKeys(patient.Profile, configuration);
            if (SameSet(patient.BlockingKeys, target))
            {
                continue;
            }

            changed.Add(patient with
            {
                BlockingKeys = target,
                LastUpdated = now,
                Version = patient.Version + 1
            });
            expected.Add(new ExpectedVersion(
                RegistryEntityKind.CanonicalPatient,
                patient.EnterpriseId.ToString(),
                patient.Version));
        }

        var completed = patientPage.NextCursor is null;
        var updatedJob = ReleaseLease(job with
        {
            Status = completed
                ? RegistryMaintenanceJobStatus.Completed
                : RegistryMaintenanceJobStatus.Running,
            Phase = completed
                ? RegistryMaintenanceJobPhase.Completed
                : RegistryMaintenanceJobPhase.Rebuilding,
            Cursor = patientPage.NextCursor,
            CompletedAt = completed ? now : null,
            Scanned = job.Scanned + patientPage.Items.Count,
            Updated = job.Updated + changed.Count,
            Unchanged = job.Unchanged + patientPage.Items.Count - changed.Count,
            LastError = null,
            Version = job.Version + 1
        });
        expected.Add(JobExpected(job));
        await store.CommitAsync(
            context,
            new RegistryMutation(
                [],
                changed,
                [],
                [],
                completed
                    ? [CreateJobAudit(context, updatedJob, "maintenance-reindex-complete", now)]
                    : [],
                expected,
                MaintenanceJobs: [updatedJob]),
            cancellationToken);
        RegistryTelemetry.RecordMaintenanceBatch(updatedJob, patientPage.Items.Count);
    }

    private async ValueTask ProcessReconciliationBatchAsync(
        ActorContext context,
        RegistryMaintenanceJob job,
        TenantMatchingConfiguration configuration,
        CancellationToken cancellationToken)
    {
        switch (job.Phase)
        {
            case RegistryMaintenanceJobPhase.Importing:
                await ImportExternalPatientsAsync(context, job, cancellationToken);
                return;
            case RegistryMaintenanceJobPhase.Rebuilding:
                await RebuildPopulationBatchAsync(context, job, configuration, cancellationToken);
                return;
            case RegistryMaintenanceJobPhase.Matching:
                await MatchPopulationBatchAsync(context, job, configuration, cancellationToken);
                return;
            default:
                throw new InvalidOperationException(
                    "The population-reconciliation job is in an invalid phase.");
        }
    }

    private async ValueTask ImportExternalPatientsAsync(
        ActorContext context,
        RegistryMaintenanceJob job,
        CancellationToken cancellationToken)
    {
        var sourceSystem = job.ExternalSourceSystem ??
                           throw new InvalidOperationException(
                               "The importing phase requires an external source.");
        var source = externalSources.Find(context.TenantId, sourceSystem) ??
                     throw new InvalidOperationException(
                         "The configured external FHIR source is unavailable.");
        var page = await source.ReadPageAsync(
            job.WindowStart,
            job.WindowEnd ?? job.RequestedAt,
            job.ExternalCursor,
            job.BatchSize,
            cancellationToken);
        if (page.NextCursor is not null &&
            string.Equals(page.NextCursor, job.ExternalCursor, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The external FHIR server repeated the same paging cursor.");
        }

        long imported = 0;
        long unchanged = 0;
        foreach (var patient in page.Items)
        {
            var sourceContext = new ActorContext(
                context.TenantId,
                $"fhir-reconciliation:{sourceSystem.Value}",
                sourceSystem,
                new HashSet<string>(StringComparer.Ordinal),
                context.CorrelationId);
            var key = new SourceRecordKey(sourceSystem, patient.LocalId);
            var current = await store.GetSourcePatientAsync(
                sourceContext,
                key,
                cancellationToken);
            var result = await registry.UpsertPatientAsync(
                sourceContext,
                new UpsertPatientCommand(
                    key,
                    patient.Profile,
                    $"fhir-reconcile:{sourceSystem.Value}:{patient.ResourceId}:{patient.SourceVersion}",
                    patient.PayloadDigest,
                    ExpectedVersion: current?.Version),
                cancellationToken);
            if (result.WasIdempotent)
            {
                unchanged++;
            }
            else
            {
                imported++;
            }
        }

        var updated = ReleaseLease(job with
        {
            Phase = page.NextCursor is null
                ? RegistryMaintenanceJobPhase.Rebuilding
                : RegistryMaintenanceJobPhase.Importing,
            ExternalCursor = page.NextCursor,
            Cursor = null,
            Imported = job.Imported + imported,
            Unchanged = job.Unchanged + unchanged,
            Scanned = job.Scanned + page.Items.Count,
            LastError = null,
            Version = job.Version + 1
        });
        await CommitJobUpdateAsync(context, job, updated, [], cancellationToken);
        RegistryTelemetry.RecordMaintenanceBatch(updated, page.Items.Count);
    }

    private async ValueTask RebuildPopulationBatchAsync(
        ActorContext context,
        RegistryMaintenanceJob job,
        TenantMatchingConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var page = await store.SearchCanonicalPatientsAsync(
            context,
            new CanonicalPatientSearch(Count: job.BatchSize, Cursor: job.Cursor),
            cancellationToken);
        var now = timeProvider.GetUtcNow();
        var sources = new List<SourcePatientRecord>();
        var canonicals = new List<CanonicalPatient>();
        var persons = new List<EnterprisePerson>();
        var expected = new List<ExpectedVersion>();
        long warnings = 0;
        long unchanged = 0;

        foreach (var canonical in page.Items)
        {
            var sourceRecords = new List<SourcePatientRecord>();
            var missingSource = false;
            foreach (var sourceKey in canonical.Sources)
            {
                var stored = await store.GetSourcePatientAsync(
                    context,
                    sourceKey,
                    cancellationToken);
                if (stored is null)
                {
                    missingSource = true;
                    warnings++;
                    continue;
                }

                if (stored.EnterpriseId != canonical.EnterpriseId)
                {
                    missingSource = true;
                    warnings++;
                    continue;
                }

                var trust = configuration.SourceTrust.TryGetValue(
                    sourceKey.SourceSystem,
                    out var configuredTrust)
                    ? configuredTrust
                    : 0;
                var governedProfile = RegistryService.ApplyIdentifierAuthority(
                    stored.Profile,
                    configuration.MatchingProfile,
                    configuration.AuthoritativeSources.Contains(sourceKey.SourceSystem));
                if (stored.SourceTrust != trust ||
                    !IdentityProfilesEqual(stored.Profile, governedProfile))
                {
                    var updatedSource = stored with
                    {
                        Profile = governedProfile,
                        SourceTrust = trust,
                        LastUpdated = now,
                        Version = stored.Version + 1
                    };
                    sources.Add(updatedSource);
                    expected.Add(new ExpectedVersion(
                        RegistryEntityKind.SourcePatient,
                        stored.Key.ToString(),
                        stored.Version));
                    sourceRecords.Add(updatedSource);
                }
                else
                {
                    sourceRecords.Add(stored);
                }
            }

            if (missingSource || sourceRecords.Count == 0)
            {
                continue;
            }

            var rebuiltProfile = BuildSurvivorshipProfile(sourceRecords);
            var rebuiltKeys = GenerateBlockingKeys(rebuiltProfile, configuration);
            var rebuiltTrust = sourceRecords.Max(static source => source.SourceTrust);
            var canonicalChanged =
                !IdentityProfilesEqual(canonical.Profile, rebuiltProfile) ||
                !SameSet(canonical.BlockingKeys, rebuiltKeys) ||
                canonical.SurvivorshipTrust != rebuiltTrust;
            if (canonicalChanged)
            {
                canonicals.Add(canonical with
                {
                    Profile = rebuiltProfile,
                    BlockingKeys = rebuiltKeys,
                    SurvivorshipTrust = rebuiltTrust,
                    LastUpdated = now,
                    Version = canonical.Version + 1
                });
                expected.Add(new ExpectedVersion(
                    RegistryEntityKind.CanonicalPatient,
                    canonical.EnterpriseId.ToString(),
                    canonical.Version));
            }

            var person = await store.GetPersonAsync(
                context,
                canonical.EnterpriseId,
                cancellationToken);
            var links = BuildPersonLinks(person?.Links ?? [], sourceRecords, now);
            if (person is null)
            {
                persons.Add(new EnterprisePerson(
                    canonical.EnterpriseId,
                    links,
                    canonical.CreatedAt,
                    now,
                    1));
            }
            else if (!person.Links.SequenceEqual(links))
            {
                persons.Add(person with
                {
                    Links = links,
                    LastUpdated = now,
                    Version = person.Version + 1
                });
                expected.Add(new ExpectedVersion(
                    RegistryEntityKind.Person,
                    person.EnterpriseId.ToString(),
                    person.Version));
            }

            if (!canonicalChanged &&
                person is not null &&
                person.Links.SequenceEqual(links) &&
                !sources.Any(source => source.EnterpriseId == canonical.EnterpriseId))
            {
                unchanged++;
            }
        }

        var finalRebuildPage = page.NextCursor is null;
        var updatedJob = ReleaseLease(job with
        {
            Phase = finalRebuildPage
                ? RegistryMaintenanceJobPhase.Matching
                : RegistryMaintenanceJobPhase.Rebuilding,
            Cursor = finalRebuildPage ? null : page.NextCursor,
            Scanned = job.Scanned + page.Items.Count,
            Updated = job.Updated + canonicals.Count + persons.Count + sources.Count,
            Unchanged = job.Unchanged + unchanged,
            Warnings = job.Warnings + warnings,
            LastError = null,
            Version = job.Version + 1
        });
        expected.Add(JobExpected(job));
        await store.CommitAsync(
            context,
            new RegistryMutation(
                sources,
                canonicals,
                persons,
                [],
                [],
                expected,
                MaintenanceJobs: [updatedJob]),
            cancellationToken);
        RegistryTelemetry.RecordMaintenanceBatch(updatedJob, page.Items.Count);
    }

    private async ValueTask MatchPopulationBatchAsync(
        ActorContext context,
        RegistryMaintenanceJob job,
        TenantMatchingConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var page = await store.SearchCanonicalPatientsAsync(
            context,
            new CanonicalPatientSearch(Count: job.BatchSize, Cursor: job.Cursor),
            cancellationToken);
        long warnings = 0;
        long reviewsCreated = 0;
        foreach (var subject in page.Items)
        {
            var normalised = IdentityNormaliser.Normalise(subject.Profile);
            var keys = BlockingKeyGenerator.Generate(normalised, configuration);
            var candidates = await store.FindCandidatesAsync(
                context,
                keys,
                configuration.MatchingProfile.MaximumCandidates,
                cancellationToken);
            if (candidates.IsTruncated)
            {
                warnings++;
                continue;
            }

            foreach (var candidate in candidates.Items.Where(candidate =>
                         candidate.EnterpriseId.Value.CompareTo(subject.EnterpriseId.Value) > 0))
            {
                var match = WeightedIdentityMatcher.Match(
                    normalised,
                    candidate,
                    configuration.MatchingProfile);
                if (match.Grade < MatchGrade.Probable)
                {
                    continue;
                }

                var reviewId = CreateReconciliationReviewId(
                    context.TenantId,
                    subject,
                    candidate,
                    configuration.MatchingProfile.Version);
                if (await store.GetReviewCaseAsync(context, reviewId, cancellationToken) is not null)
                {
                    continue;
                }

                var now = timeProvider.GetUtcNow();
                var review = new ReviewCase(
                    reviewId,
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
                    Kind: ReviewCaseKind.PopulationReconciliation,
                    RequiredApprovals: match.HasHardConflict
                        ? 2
                        : configuration.RequiredLinkApprovals,
                    Approvals: [],
                    SourcesToMove: [],
                    SubjectVersion: subject.Version,
                    CandidateVersion: candidate.Version,
                    ApprovalPolicyLocked: match.HasHardConflict);
                try
                {
                    await store.CommitAsync(
                        context,
                        new RegistryMutation(
                            [],
                            [],
                            [],
                            [review],
                            [new AuditRecord(
                                Guid.CreateVersion7(),
                                "population-reconciliation-review-create",
                                context.ActorId,
                                "success",
                                "Scheduled population reconciliation found a probable duplicate.",
                                subject.EnterpriseId,
                                null,
                                now,
                                context.CorrelationId)],
                            []),
                        cancellationToken);
                    reviewsCreated++;
                }
                catch (RegistryConcurrencyException)
                {
                    // The deterministic review ID makes concurrent discovery idempotent.
                }
            }
        }

        var nowCompleted = timeProvider.GetUtcNow();
        var completed = page.NextCursor is null;
        var updated = ReleaseLease(job with
        {
            Status = completed
                ? RegistryMaintenanceJobStatus.Completed
                : RegistryMaintenanceJobStatus.Running,
            Phase = completed
                ? RegistryMaintenanceJobPhase.Completed
                : RegistryMaintenanceJobPhase.Matching,
            Cursor = page.NextCursor,
            CompletedAt = completed ? nowCompleted : null,
            Scanned = job.Scanned + page.Items.Count,
            ReviewCasesCreated = job.ReviewCasesCreated + reviewsCreated,
            Warnings = job.Warnings + warnings,
            LastError = null,
            Version = job.Version + 1
        });
        await CommitJobUpdateAsync(
            context,
            job,
            updated,
            completed
                ? [CreateJobAudit(
                    context,
                    updated,
                    "maintenance-reconciliation-complete",
                    nowCompleted)]
                : [],
            cancellationToken);
        RegistryTelemetry.RecordMaintenanceBatch(updated, page.Items.Count);
        RegistryTelemetry.RecordReviewsCreated((int)Math.Min(int.MaxValue, reviewsCreated), context.TenantId);
    }

    private async ValueTask<RegistryMaintenanceJob?> TryAcquireLeaseAsync(
        ActorContext context,
        Guid jobId,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var job = await store.GetMaintenanceJobAsync(context, jobId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (job is null ||
            IsTerminal(job.Status) ||
            job.NextAttemptAt > now ||
            (job.LeaseExpiresAt > now &&
             !string.Equals(job.LeaseOwner, workerId, StringComparison.Ordinal)))
        {
            return null;
        }

        var leased = job with
        {
            Status = RegistryMaintenanceJobStatus.Running,
            StartedAt = job.StartedAt ?? now,
            LeaseOwner = workerId,
            LeaseExpiresAt = now.Add(leaseDuration),
            NextAttemptAt = null,
            Attempts = job.Attempts + 1,
            Version = job.Version + 1
        };
        try
        {
            await CommitJobUpdateAsync(context, job, leased, [], cancellationToken);
            return leased;
        }
        catch (RegistryConcurrencyException)
        {
            return null;
        }
    }

    private async ValueTask ReleaseForRetryAsync(
        ActorContext context,
        Guid jobId,
        string message,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        var current = await store.GetMaintenanceJobAsync(context, jobId, cancellationToken);
        if (current is null || IsTerminal(current.Status))
        {
            return;
        }

        var updated = ReleaseLease(current with
        {
            LastError = message,
            NextAttemptAt = timeProvider.GetUtcNow().Add(delay),
            FailedItems = current.FailedItems + 1,
            Version = current.Version + 1
        });
        try
        {
            await CommitJobUpdateAsync(context, current, updated, [], cancellationToken);
        }
        catch (RegistryConcurrencyException)
        {
            // Another worker renewed or completed the job after this batch lost its lease.
        }
    }

    private async ValueTask FailJobAsync(
        ActorContext context,
        RegistryMaintenanceJob job,
        string message,
        CancellationToken cancellationToken)
    {
        var current = await store.GetMaintenanceJobAsync(context, job.Id, cancellationToken) ?? job;
        if (IsTerminal(current.Status))
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var failed = ReleaseLease(current with
        {
            Status = RegistryMaintenanceJobStatus.Failed,
            LastError = message,
            CompletedAt = now,
            FailedItems = current.FailedItems + 1,
            Version = current.Version + 1
        });
        await CommitJobUpdateAsync(
            context,
            current,
            failed,
            [CreateJobAudit(context, failed, "maintenance-job-fail", now)],
            cancellationToken);
    }

    private async ValueTask CompleteCancellationAsync(
        ActorContext context,
        RegistryMaintenanceJob job,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var cancelled = ReleaseLease(job with
        {
            Status = RegistryMaintenanceJobStatus.Cancelled,
            CompletedAt = now,
            Version = job.Version + 1
        });
        await CommitJobUpdateAsync(
            context,
            job,
            cancelled,
            [CreateJobAudit(context, cancelled, "maintenance-job-cancel", now)],
            cancellationToken);
    }

    private async ValueTask EnsureNoActiveJobAsync(
        ActorContext context,
        RegistryMaintenanceJobKind kind,
        SourceSystemId? sourceSystem,
        CancellationToken cancellationToken)
    {
        foreach (var status in ActiveStatuses)
        {
            var jobs = await store.SearchMaintenanceJobsAsync(
                context,
                new MaintenanceJobSearch(
                    kind,
                    status,
                    sourceSystem,
                    Count: 10),
                cancellationToken);
            if (jobs.Items.Any(job => job.ExternalSourceSystem == sourceSystem))
            {
                throw new RegistryConcurrencyException(
                    $"An active {kind} job already exists for this tenant and source.");
            }
        }
    }

    private async ValueTask CreateJobAsync(
        ActorContext context,
        RegistryMaintenanceJob job,
        CancellationToken cancellationToken)
    {
        await store.CommitAsync(
            context,
            new RegistryMutation(
                [],
                [],
                [],
                [],
                [CreateJobAudit(context, job, "maintenance-job-create", job.RequestedAt)],
                [],
                MaintenanceJobs: [job]),
            cancellationToken);
        RegistryTelemetry.RecordMaintenanceStarted(job);
    }

    private ValueTask<RegistryCommitResult> CommitJobUpdateAsync(
        ActorContext context,
        RegistryMaintenanceJob current,
        RegistryMaintenanceJob updated,
        IReadOnlyList<AuditRecord> auditRecords,
        CancellationToken cancellationToken) =>
        store.CommitAsync(
            context,
            new RegistryMutation(
                [],
                [],
                [],
                [],
                auditRecords,
                [JobExpected(current)],
                MaintenanceJobs: [updated]),
            cancellationToken);

    private static ExpectedVersion JobExpected(RegistryMaintenanceJob job) =>
        new(RegistryEntityKind.MaintenanceJob, job.Id.ToString("D"), job.Version);

    private static RegistryMaintenanceJob ReleaseLease(RegistryMaintenanceJob job) =>
        job with
        {
            LeaseOwner = null,
            LeaseExpiresAt = null
        };

    private static IReadOnlyList<BlockingKey> GenerateBlockingKeys(
        IdentityProfile profile,
        TenantMatchingConfiguration configuration) =>
        BlockingKeyGenerator.Generate(IdentityNormaliser.Normalise(profile), configuration);

    private static IdentityProfile BuildSurvivorshipProfile(
        IReadOnlyList<SourcePatientRecord> sources)
    {
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

    private static PersonLink[] BuildPersonLinks(
        IReadOnlyList<PersonLink> existing,
        IReadOnlyList<SourcePatientRecord> sources,
        DateTimeOffset now)
    {
        var bySource = existing.ToDictionary(static link => link.Source);
        return sources
            .OrderBy(static source => source.Key.ToString(), StringComparer.Ordinal)
            .Select(source => bySource.TryGetValue(source.Key, out var current) &&
                              string.Equals(
                                  current.SourceResourceId,
                                  source.ResourceId,
                                  StringComparison.Ordinal)
                ? current
                : new PersonLink(
                    source.Key,
                    source.ResourceId,
                    LinkAssurance.Level2,
                    now,
                    "Population reconciliation"))
            .ToArray();
    }

    private static Guid CreateReconciliationReviewId(
        TenantId tenant,
        CanonicalPatient subject,
        CanonicalPatient candidate,
        string profileVersion)
    {
        var left = subject.EnterpriseId.Value.CompareTo(candidate.EnterpriseId.Value) < 0
            ? subject
            : candidate;
        var right = left == subject ? candidate : subject;
        var value = string.Join(
            '\0',
            tenant.Value,
            left.EnterpriseId.ToString(),
            left.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            right.EnterpriseId.ToString(),
            right.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            profileVersion);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        Span<byte> guidBytes = stackalloc byte[16];
        bytes.AsSpan(0, 16).CopyTo(guidBytes);
        guidBytes[7] = (byte)((guidBytes[7] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
        return new Guid(guidBytes);
    }

    private static bool SameSet<T>(IReadOnlyCollection<T> left, IReadOnlyCollection<T> right) =>
        left.Count == right.Count && left.ToHashSet().SetEquals(right);

    private static bool IdentityProfilesEqual(IdentityProfile left, IdentityProfile right) =>
        left.Identifiers.SequenceEqual(right.Identifiers) &&
        left.Names.Count == right.Names.Count &&
        left.Names.Zip(right.Names).All(static pair =>
            string.Equals(pair.First.Family, pair.Second.Family, StringComparison.Ordinal) &&
            pair.First.Given.SequenceEqual(pair.Second.Given, StringComparer.Ordinal) &&
            pair.First.Use == pair.Second.Use &&
            string.Equals(pair.First.Prefix, pair.Second.Prefix, StringComparison.Ordinal) &&
            string.Equals(pair.First.Suffix, pair.Second.Suffix, StringComparison.Ordinal)) &&
        left.BirthDate == right.BirthDate &&
        left.Gender == right.Gender &&
        left.Addresses.Count == right.Addresses.Count &&
        left.Addresses.Zip(right.Addresses).All(static pair =>
            pair.First.Lines.SequenceEqual(pair.Second.Lines, StringComparer.Ordinal) &&
            string.Equals(pair.First.City, pair.Second.City, StringComparison.Ordinal) &&
            string.Equals(pair.First.District, pair.Second.District, StringComparison.Ordinal) &&
            string.Equals(pair.First.PostalCode, pair.Second.PostalCode, StringComparison.Ordinal) &&
            string.Equals(pair.First.Country, pair.Second.Country, StringComparison.Ordinal) &&
            pair.First.Use == pair.Second.Use) &&
        left.Telecoms.SequenceEqual(right.Telecoms) &&
        left.IsDeceased == right.IsDeceased &&
        left.Tags.SequenceEqual(right.Tags);

    private static AuditRecord CreateJobAudit(
        ActorContext context,
        RegistryMaintenanceJob job,
        string action,
        DateTimeOffset now) =>
        new(
            Guid.CreateVersion7(),
            action,
            context.ActorId,
            job.Status == RegistryMaintenanceJobStatus.Failed ? "failure" : "success",
            job.Status == RegistryMaintenanceJobStatus.Failed
                ? job.LastError ?? job.Reason
                : job.Reason,
            null,
            null,
            now,
            context.CorrelationId);

    private static bool IsTerminal(RegistryMaintenanceJobStatus status) =>
        status is RegistryMaintenanceJobStatus.Completed or
            RegistryMaintenanceJobStatus.Failed or
            RegistryMaintenanceJobStatus.Cancelled;

    private static bool IsTransient(HttpStatusCode? statusCode) =>
        statusCode is null or HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests ||
        (int)statusCode.Value >= 500;

    private static string SafeFailureMessage(Exception exception) =>
        exception switch
        {
            InsufficientIdentityDataException =>
                "A registry record does not contain the fields required by the target blocking rules.",
            HttpRequestException =>
                "The external FHIR source rejected the reconciliation request.",
            InvalidOperationException =>
                "The maintenance batch encountered inconsistent registry or integration state. Consult the correlated server logs.",
            ArgumentException =>
                "The maintenance batch encountered invalid registry or integration data. Consult the correlated server logs.",
            _ => "The maintenance batch failed. Consult the correlated server logs for details."
        };

    private static void ValidateStart(string reason, int batchSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (reason.Length > 512)
        {
            throw new ArgumentException("The maintenance reason cannot exceed 512 characters.");
        }

        if (batchSize is < 1 or > 25)
        {
            throw new ArgumentOutOfRangeException(
                nameof(batchSize),
                "Maintenance batch size must be between 1 and 25.");
        }
    }

    private static void EnsureMaintenanceReader(ActorContext context)
    {
        if (!context.HasScope("mpi.operations") && !context.HasScope("mpi.admin"))
        {
            throw new RegistryAuthorisationException(
                "The mpi.operations scope is required to read maintenance jobs.");
        }
    }

    private static void EnsureMaintenanceAdministrator(ActorContext context)
    {
        if (!context.HasScope("mpi.admin"))
        {
            throw new RegistryAuthorisationException(
                "The mpi.admin scope is required to manage registry maintenance.");
        }
    }
}
