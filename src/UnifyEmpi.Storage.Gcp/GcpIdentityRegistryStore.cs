using System.Globalization;
using Hl7.Fhir.Model;
using UnifyEmpi.Domain;
using UnifyEmpi.Fhir.R4;
using UnifyEmpi.Storage.Abstractions;

namespace UnifyEmpi.Storage.Gcp;

public sealed class GcpIdentityRegistryStore(IGcpFhirClient client) : IIdentityRegistryStore
{
    public async ValueTask<SourcePatientRecord?> GetSourcePatientAsync(
        ActorContext context,
        SourceRecordKey key,
        CancellationToken cancellationToken)
    {
        if (context.SourceSystem is { } sourceSystem && sourceSystem != key.SourceSystem)
        {
            throw new RegistryAuthorisationException(
                "The authenticated source system cannot read another source profile.");
        }

        var page = await SearchAsync(
            context,
            "Patient",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["identifier"] =
                    $"{GcpDomainResourceMapper.SourceKeySystemPrefix}{Uri.EscapeDataString(key.SourceSystem.Value)}|{key.LocalId}",
                ["_tag"] = $"{GcpDomainResourceMapper.InternalSystem}|source-patient",
                ["_count"] = "2"
            },
            cancellationToken);
        var items = page.Entry
            .Select(static entry => entry.Resource)
            .OfType<Patient>()
            .Select(resource => GcpDomainResourceMapper.ToSourcePatient(resource, context.TenantId))
            .Where(source => source.Key == key)
            .ToArray();
        return items.Length switch
        {
            0 => null,
            1 => items[0],
            _ => throw new InvalidOperationException("The source key is not unique in the registry.")
        };
    }

    public async ValueTask<SourcePatientRecord?> GetSourcePatientByResourceIdAsync(
        ActorContext context,
        string resourceId,
        CancellationToken cancellationToken)
    {
        var resource = await client.ReadAsync("Patient", resourceId, cancellationToken);
        if (resource is null)
        {
            return null;
        }

        GcpDomainResourceMapper.AssertTenant(resource, context.TenantId);
        var source = GcpDomainResourceMapper.ToSourcePatient(resource, context.TenantId);
        if (context.SourceSystem is { } sourceSystem &&
            source.Key.SourceSystem != sourceSystem)
        {
            throw new RegistryAuthorisationException(
                "The authenticated source system cannot read another source profile.");
        }

        return source;
    }

    public async ValueTask<CanonicalPatient?> GetCanonicalPatientAsync(
        ActorContext context,
        EnterpriseId enterpriseId,
        CancellationToken cancellationToken)
    {
        var resource = await client.ReadAsync(
            "Patient",
            enterpriseId.ToString(),
            cancellationToken);
        if (resource is null)
        {
            return null;
        }

        GcpDomainResourceMapper.AssertTenant(resource, context.TenantId);
        return GcpDomainResourceMapper.ToCanonicalPatient(resource, context.TenantId);
    }

    public async ValueTask<EnterprisePerson?> GetPersonAsync(
        ActorContext context,
        EnterpriseId enterpriseId,
        CancellationToken cancellationToken)
    {
        var resource = await client.ReadAsync(
            "Person",
            enterpriseId.ToString(),
            cancellationToken);
        if (resource is null)
        {
            return null;
        }

        GcpDomainResourceMapper.AssertTenant(resource, context.TenantId);
        return GcpDomainResourceMapper.ToPerson(resource, context.TenantId);
    }

    public async ValueTask<CandidatePage> FindCandidatesAsync(
        ActorContext context,
        IReadOnlyCollection<BlockingKey> blockingKeys,
        int maximumCandidates,
        CancellationToken cancellationToken)
    {
        if (blockingKeys.Count == 0)
        {
            return new CandidatePage([], false);
        }

        if (maximumCandidates is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCandidates));
        }

        var tagUnion = string.Join(
            ",",
            blockingKeys
                .Distinct()
                .Select(static key =>
                    $"{GcpDomainResourceMapper.BlockingSystemPrefix}{key.Version}|{key.Value}"));
        var bundle = await SearchAsync(
            context,
            "Patient",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["_tag"] = tagUnion,
                ["_count"] = (maximumCandidates + 1).ToString(CultureInfo.InvariantCulture)
            },
            cancellationToken);
        var items = bundle.Entry
            .Select(static entry => entry.Resource)
            .OfType<Patient>()
            .Where(static patient => HasInternalTag(patient, "canonical-patient"))
            .Select(resource => GcpDomainResourceMapper.ToCanonicalPatient(
                resource,
                context.TenantId))
            .Where(static patient => patient.IsActive)
            .GroupBy(static patient => patient.EnterpriseId)
            .Select(static group => group.First())
            .Take(maximumCandidates + 1)
            .ToArray();
        return new CandidatePage(items.Take(maximumCandidates).ToArray(), items.Length > maximumCandidates);
    }

    public async ValueTask<Page<CanonicalPatient>> SearchCanonicalPatientsAsync(
        ActorContext context,
        CanonicalPatientSearch search,
        CancellationToken cancellationToken)
    {
        var parameters = NewSearch(context, search.Count, search.Cursor);
        parameters["_tag"] = $"{GcpDomainResourceMapper.InternalSystem}|canonical-patient";
        if (!string.IsNullOrWhiteSpace(search.IdentifierValue))
        {
            parameters["identifier"] = search.IdentifierSystem is null
                ? search.IdentifierValue
                : $"{search.IdentifierSystem}|{search.IdentifierValue}";
        }

        if (!string.IsNullOrWhiteSpace(search.FamilyName))
        {
            parameters["family"] = search.FamilyName;
        }

        if (search.BirthDate.HasValue)
        {
            parameters["birthdate"] = search.BirthDate.Value.ToString(
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture);
        }

        var bundle = await SearchAsync(context, "Patient", parameters, cancellationToken);
        var items = bundle.Entry
            .Select(static entry => entry.Resource)
            .OfType<Patient>()
            .Select(resource => GcpDomainResourceMapper.ToCanonicalPatient(
                resource,
                context.TenantId))
            .Where(static patient => patient.IsActive)
            .ToArray();
        return new Page<CanonicalPatient>(items, GetNextCursor(bundle));
    }

    public async ValueTask<Page<EnterprisePerson>> SearchPersonsAsync(
        ActorContext context,
        PersonSearch search,
        CancellationToken cancellationToken)
    {
        var parameters = NewSearch(context, search.Count, search.Cursor);
        parameters["_tag"] = $"{GcpDomainResourceMapper.InternalSystem}|enterprise-person";
        if (search.EnterpriseId is { } enterpriseId)
        {
            parameters["identifier"] =
                $"{FhirR4Constants.EnterpriseIdentifierSystem}|{enterpriseId}";
        }

        var bundle = await SearchAsync(context, "Person", parameters, cancellationToken);
        var items = bundle.Entry
            .Select(static entry => entry.Resource)
            .OfType<Person>()
            .Select(resource => GcpDomainResourceMapper.ToPerson(resource, context.TenantId))
            .Where(static person => person.IsActive)
            .ToArray();
        return new Page<EnterprisePerson>(items, GetNextCursor(bundle));
    }

    public async ValueTask<ReviewCase?> GetReviewCaseAsync(
        ActorContext context,
        Guid reviewCaseId,
        CancellationToken cancellationToken)
    {
        var resource = await client.ReadAsync(
            "Task",
            reviewCaseId.ToString("D"),
            cancellationToken);
        if (resource is null)
        {
            return null;
        }

        GcpDomainResourceMapper.AssertTenant(resource, context.TenantId);
        return GcpDomainResourceMapper.ToReviewCase(resource, context.TenantId);
    }

    public async ValueTask<Page<ReviewCase>> SearchReviewCasesAsync(
        ActorContext context,
        ReviewCaseSearch search,
        CancellationToken cancellationToken)
    {
        var parameters = NewSearch(context, search.Count, search.Cursor);
        parameters["_tag"] = $"{GcpDomainResourceMapper.InternalSystem}|review-case";
        if (search.Status.HasValue)
        {
            parameters["status"] = search.Status.Value switch
            {
                ReviewCaseStatus.Pending => "requested",
                ReviewCaseStatus.AwaitingSecondApproval => "accepted",
                ReviewCaseStatus.Linked => "completed",
                ReviewCaseStatus.Split => "completed",
                ReviewCaseStatus.Rejected => "rejected",
                ReviewCaseStatus.Superseded => "cancelled",
                _ => throw new ArgumentOutOfRangeException(nameof(search))
            };
        }

        var bundle = await SearchAsync(context, "Task", parameters, cancellationToken);
        var items = bundle.Entry
            .Select(static entry => entry.Resource)
            .OfType<Hl7.Fhir.Model.Task>()
            .Select(resource => GcpDomainResourceMapper.ToReviewCase(
                resource,
                context.TenantId))
            .Where(review => !search.Status.HasValue || review.Status == search.Status.Value)
            .Where(review => !search.Kind.HasValue || review.Kind == search.Kind.Value)
            .ToArray();
        return new Page<ReviewCase>(items, GetNextCursor(bundle));
    }

    public async ValueTask<Page<AuditRecord>> SearchAuditRecordsAsync(
        ActorContext context,
        AuditRecordSearch search,
        CancellationToken cancellationToken)
    {
        var parameters = NewSearch(context, search.Count, search.Cursor);
        parameters["_tag"] = $"{GcpDomainResourceMapper.InternalSystem}|audit";
        if (!string.IsNullOrWhiteSpace(search.Action))
        {
            parameters["type"] =
                $"{GcpDomainResourceMapper.InternalSystem}|{search.Action}";
        }

        if (search.From.HasValue)
        {
            parameters["date"] =
                $"ge{search.From.Value.ToString("O", CultureInfo.InvariantCulture)}";
        }
        else if (search.To.HasValue)
        {
            parameters["date"] =
                $"le{search.To.Value.ToString("O", CultureInfo.InvariantCulture)}";
        }

        var bundle = await SearchAsync(context, "AuditEvent", parameters, cancellationToken);
        var items = bundle.Entry
            .Select(static entry => entry.Resource)
            .OfType<AuditEvent>()
            .Select(resource => GcpDomainResourceMapper.ToAuditRecord(resource, context.TenantId))
            .Where(record =>
                string.IsNullOrWhiteSpace(search.Actor) ||
                record.Actor.Contains(search.Actor, StringComparison.OrdinalIgnoreCase))
            .Where(record =>
                string.IsNullOrWhiteSpace(search.Outcome) ||
                string.Equals(record.Outcome, search.Outcome, StringComparison.OrdinalIgnoreCase))
            .Where(record =>
                !search.EnterpriseId.HasValue ||
                record.EnterpriseId == search.EnterpriseId)
            .Where(record => !search.To.HasValue || record.RecordedAt <= search.To.Value)
            .ToArray();
        return new Page<AuditRecord>(items, GetNextCursor(bundle));
    }

    public async ValueTask<TenantSettings?> GetTenantSettingsAsync(
        ActorContext context,
        CancellationToken cancellationToken)
    {
        var resource = await client.ReadAsync(
            "Basic",
            GcpDomainResourceMapper.TenantSettingsResourceId(context.TenantId),
            cancellationToken);
        if (resource is null)
        {
            return null;
        }

        GcpDomainResourceMapper.AssertTenant(resource, context.TenantId);
        return GcpDomainResourceMapper.ToTenantSettings(resource, context.TenantId);
    }

    public async ValueTask<RegistryMaintenanceJob?> GetMaintenanceJobAsync(
        ActorContext context,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var resource = await client.ReadAsync(
            "Task",
            jobId.ToString("D"),
            cancellationToken);
        if (resource is null)
        {
            return null;
        }

        GcpDomainResourceMapper.AssertTenant(resource, context.TenantId);
        return GcpDomainResourceMapper.ToMaintenanceJob(resource, context.TenantId);
    }

    public async ValueTask<Page<RegistryMaintenanceJob>> SearchMaintenanceJobsAsync(
        ActorContext context,
        MaintenanceJobSearch search,
        CancellationToken cancellationToken)
    {
        var parameters = NewSearch(context, search.Count, search.Cursor);
        parameters["_tag"] = $"{GcpDomainResourceMapper.InternalSystem}|maintenance-job";
        if (search.Status.HasValue)
        {
            parameters["status"] = search.Status.Value switch
            {
                RegistryMaintenanceJobStatus.Queued => "requested",
                RegistryMaintenanceJobStatus.Running => "in-progress",
                RegistryMaintenanceJobStatus.Completed => "completed",
                RegistryMaintenanceJobStatus.Failed => "failed",
                RegistryMaintenanceJobStatus.Cancelled => "cancelled",
                _ => throw new ArgumentOutOfRangeException(nameof(search))
            };
        }

        var bundle = await SearchAsync(context, "Task", parameters, cancellationToken);
        var items = bundle.Entry
            .Select(static entry => entry.Resource)
            .OfType<Hl7.Fhir.Model.Task>()
            .Select(resource => GcpDomainResourceMapper.ToMaintenanceJob(
                resource,
                context.TenantId))
            .Where(job => !search.Kind.HasValue || job.Kind == search.Kind.Value)
            .Where(job =>
                !search.ExternalSourceSystem.HasValue ||
                job.ExternalSourceSystem == search.ExternalSourceSystem.Value)
            .Where(job =>
                string.IsNullOrWhiteSpace(search.ScheduleKey) ||
                string.Equals(job.ScheduleKey, search.ScheduleKey, StringComparison.Ordinal))
            .ToArray();
        return new Page<RegistryMaintenanceJob>(items, GetNextCursor(bundle));
    }

    public async ValueTask<IngestionReceipt?> GetReceiptAsync(
        ActorContext context,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var resource = await client.ReadAsync(
            "Basic",
            GcpDomainResourceMapper.ReceiptResourceId(context.TenantId, idempotencyKey),
            cancellationToken);
        if (resource is null)
        {
            return null;
        }

        GcpDomainResourceMapper.AssertTenant(resource, context.TenantId);
        return GcpDomainResourceMapper.ToReceipt(resource, context.TenantId);
    }

    public async ValueTask<RegistryCommitResult> CommitAsync(
        ActorContext context,
        RegistryMutation mutation,
        CancellationToken cancellationToken)
    {
        if (mutation.Receipt is not null)
        {
            var existing = await GetReceiptAsync(
                context,
                mutation.Receipt.IdempotencyKey,
                cancellationToken);
            if (existing is not null)
            {
                if (!string.Equals(
                        existing.PayloadDigest,
                        mutation.Receipt.PayloadDigest,
                        StringComparison.Ordinal))
                {
                    throw new IdempotencyConflictException(mutation.Receipt.IdempotencyKey);
                }

                return new RegistryCommitResult(false, true);
            }
        }

        var transaction = await BuildTransactionAsync(
            context,
            mutation,
            cancellationToken);
        try
        {
            var response = await client.ExecuteTransactionAsync(transaction, cancellationToken);
            ValidateTransactionResponse(response, transaction.Entry.Count);
            return new RegistryCommitResult(true, false);
        }
        catch (HttpRequestException exception) when (
            exception.StatusCode is System.Net.HttpStatusCode.Conflict or
                System.Net.HttpStatusCode.PreconditionFailed)
        {
            throw new RegistryConcurrencyException(
                "The GCP registry transaction failed optimistic concurrency checks.");
        }
    }

    public async ValueTask<RegistryStoreHealth> CheckHealthAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var healthy = await client.CheckHealthAsync(cancellationToken);
            return new RegistryStoreHealth(
                healthy,
                "gcp-healthcare-r4",
                RequiredCapabilities,
                healthy ? null : "The GCP FHIR metadata endpoint was not healthy.");
        }
        catch (HttpRequestException)
        {
            return new RegistryStoreHealth(
                false,
                "gcp-healthcare-r4",
                RequiredCapabilities,
                "The GCP FHIR metadata endpoint could not be reached.");
        }
    }

    private static RegistryStoreCapabilities RequiredCapabilities { get; } =
        new(true, true, true, true, 500);

    private async ValueTask<Bundle> SearchAsync(
        ActorContext context,
        string resourceType,
        Dictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        parameters["_security"] =
            $"{FhirR4Constants.TenantSecuritySystem}|{context.TenantId.Value}";
        var bundle = await client.SearchAsync(resourceType, parameters, cancellationToken);
        VerifySelfLinkRetainedTenant(bundle, context.TenantId);
        foreach (var resource in bundle.Entry
                     .Select(static entry => entry.Resource)
                     .Where(static resource => resource is not null))
        {
            GcpDomainResourceMapper.AssertTenant(resource!, context.TenantId);
        }

        return bundle;
    }

    private static Dictionary<string, string> NewSearch(
        ActorContext context,
        int count,
        string? cursor)
    {
        _ = context;
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["_count"] = Math.Clamp(count, 1, 100).ToString(CultureInfo.InvariantCulture)
        };
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            parameters["_page_token"] = cursor;
        }

        return parameters;
    }

    private async ValueTask<Bundle> BuildTransactionAsync(
        ActorContext context,
        RegistryMutation mutation,
        CancellationToken cancellationToken)
    {
        var bundle = new Bundle
        {
            Type = Bundle.BundleType.Transaction
        };
        foreach (var source in mutation.SourcePatients)
        {
            await AddEntryAsync(
                bundle,
                context,
                GcpDomainResourceMapper.ToSourcePatient(source, context.TenantId),
                "Patient",
                source.ResourceId,
                ExpectedVersionFor(
                    mutation,
                    RegistryEntityKind.SourcePatient,
                    source.Key.ToString()),
                source.Version,
                cancellationToken);
        }

        foreach (var patient in mutation.CanonicalPatients)
        {
            await AddEntryAsync(
                bundle,
                context,
                GcpDomainResourceMapper.ToCanonicalPatient(patient, context.TenantId),
                "Patient",
                patient.EnterpriseId.ToString(),
                ExpectedVersionFor(
                    mutation,
                    RegistryEntityKind.CanonicalPatient,
                    patient.EnterpriseId.ToString()),
                patient.Version,
                cancellationToken);
        }

        foreach (var person in mutation.Persons)
        {
            await AddEntryAsync(
                bundle,
                context,
                GcpDomainResourceMapper.ToPerson(person, context.TenantId),
                "Person",
                person.EnterpriseId.ToString(),
                ExpectedVersionFor(
                    mutation,
                    RegistryEntityKind.Person,
                    person.EnterpriseId.ToString()),
                person.Version,
                cancellationToken);
        }

        foreach (var review in mutation.ReviewCases)
        {
            await AddEntryAsync(
                bundle,
                context,
                GcpDomainResourceMapper.ToReviewTask(review, context.TenantId),
                "Task",
                review.Id.ToString("D"),
                ExpectedVersionFor(
                    mutation,
                    RegistryEntityKind.ReviewCase,
                    review.Id.ToString()),
                review.Version,
                cancellationToken);
        }

        foreach (var job in mutation.EffectiveMaintenanceJobs)
        {
            await AddEntryAsync(
                bundle,
                context,
                GcpDomainResourceMapper.ToMaintenanceTask(job, context.TenantId),
                "Task",
                job.Id.ToString("D"),
                ExpectedVersionFor(
                    mutation,
                    RegistryEntityKind.MaintenanceJob,
                    job.Id.ToString("D")),
                job.Version,
                cancellationToken);
        }

        foreach (var audit in mutation.AuditRecords)
        {
            await AddEntryAsync(
                bundle,
                context,
                GcpDomainResourceMapper.ToAuditEvent(audit, context.TenantId),
                "AuditEvent",
                audit.Id.ToString("D"),
                null,
                1,
                cancellationToken);
        }

        if (mutation.TenantSettings is not null)
        {
            var settings = GcpDomainResourceMapper.ToTenantSettings(
                mutation.TenantSettings,
                context.TenantId);
            await AddEntryAsync(
                bundle,
                context,
                settings,
                "Basic",
                settings.Id ??
                throw new InvalidOperationException("Tenant-settings ID was not generated."),
                ExpectedVersionFor(
                    mutation,
                    RegistryEntityKind.TenantSettings,
                    context.TenantId.Value),
                mutation.TenantSettings.Version,
                cancellationToken);
        }

        if (mutation.Receipt is not null)
        {
            var receipt = GcpDomainResourceMapper.ToReceipt(
                mutation.Receipt,
                context.TenantId);
            await AddEntryAsync(
                bundle,
                context,
                receipt,
                "Basic",
                receipt.Id ?? throw new InvalidOperationException("Receipt ID was not generated."),
                null,
                1,
                cancellationToken);
        }

        return bundle;
    }

    private static long? ExpectedVersionFor(
        RegistryMutation mutation,
        RegistryEntityKind kind,
        string id) =>
        mutation.ExpectedVersions.FirstOrDefault(expected =>
            expected.Kind == kind &&
            string.Equals(expected.Id, id, StringComparison.Ordinal))?.Version;

    private async ValueTask AddEntryAsync(
        Bundle bundle,
        ActorContext context,
        Resource resource,
        string resourceType,
        string resourceId,
        long? expectedVersion,
        long nextVersion,
        CancellationToken cancellationToken)
    {
        if (nextVersion > 1 && !expectedVersion.HasValue)
        {
            throw new RegistryConcurrencyException(
                $"An expected version is required to update {resourceType}/{resourceId}.");
        }

        string? backendVersion = null;
        if (expectedVersion.HasValue)
        {
            var current = await client.ReadAsync(
                resourceType,
                resourceId,
                cancellationToken);
            if (current is null)
            {
                throw new RegistryConcurrencyException(
                    $"The expected {resourceType}/{resourceId} resource does not exist.");
            }

            GcpDomainResourceMapper.AssertTenant(current, context.TenantId);
            if (GcpDomainResourceMapper.ParseVersion(current) != expectedVersion.Value)
            {
                throw new RegistryConcurrencyException(
                    $"The expected version of {resourceType}/{resourceId} is no longer current.");
            }

            backendVersion = current.Meta?.VersionId;
            if (string.IsNullOrWhiteSpace(backendVersion))
            {
                throw new InvalidOperationException(
                    $"The FHIR store omitted the version identifier for {resourceType}/{resourceId}.");
            }
        }

        var request = new Bundle.RequestComponent
        {
            Method = Bundle.HTTPVerb.PUT,
            Url = $"{resourceType}/{resourceId}"
        };
        if (backendVersion is not null)
        {
            request.IfMatch = $"W/\"{backendVersion}\"";
        }
        else
        {
            request.IfNoneMatch = "*";
        }

        // Version identifiers and last-updated instants are assigned by the FHIR server.
        // Sending client-generated values is rejected by durable FHIR stores even though
        // the domain uses them for optimistic concurrency and deterministic tests.
        if (resource.Meta is not null)
        {
            resource.Meta.VersionId = null;
            resource.Meta.LastUpdated = null;
        }

        bundle.Entry.Add(new Bundle.EntryComponent
        {
            Resource = resource,
            Request = request
        });
    }

    private static void ValidateTransactionResponse(Bundle response, int expectedEntries)
    {
        if (response.Type != Bundle.BundleType.TransactionResponse ||
            response.Entry.Count != expectedEntries)
        {
            throw new InvalidOperationException(
                "The GCP FHIR transaction response was incomplete.");
        }

        foreach (var entry in response.Entry)
        {
            var status = entry.Response?.Status;
            if (status is null ||
                !int.TryParse(status.AsSpan(0, Math.Min(3, status.Length)), out var code) ||
                code is < 200 or >= 300)
            {
                throw new InvalidOperationException(
                    "The GCP FHIR transaction contained an unsuccessful entry.");
            }
        }
    }

    private static void VerifySelfLinkRetainedTenant(Bundle bundle, TenantId tenant)
    {
        var self = bundle.Link.FirstOrDefault(link =>
            string.Equals(link.Relation, "self", StringComparison.Ordinal))?.Url;
        if (string.IsNullOrWhiteSpace(self) ||
            !Uri.TryCreate(self, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException(
                "The GCP FHIR search response omitted its absolute self link.");
        }

        var expected = $"{FhirR4Constants.TenantSecuritySystem}|{tenant.Value}";
        var securityValues = ParseQuery(uri.Query)
            .Where(static pair => string.Equals(
                pair.Key,
                "_security",
                StringComparison.Ordinal))
            .Select(static pair => pair.Value)
            .ToArray();
        if (securityValues.Length != 1 ||
            !string.Equals(securityValues[0], expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The GCP FHIR response self link did not retain the tenant security filter.");
        }
    }

    private static IEnumerable<KeyValuePair<string, string>> ParseQuery(string query)
    {
        foreach (var item in query.TrimStart('?').Split(
                     '&',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = item.IndexOf('=');
            if (equals < 0)
            {
                yield return new KeyValuePair<string, string>(
                    Uri.UnescapeDataString(item),
                    string.Empty);
            }
            else
            {
                yield return new KeyValuePair<string, string>(
                    Uri.UnescapeDataString(item[..equals]),
                    Uri.UnescapeDataString(item[(equals + 1)..]));
            }
        }
    }

    private static string? GetNextCursor(Bundle bundle)
    {
        var next = bundle.Link.FirstOrDefault(link =>
            string.Equals(link.Relation, "next", StringComparison.Ordinal))?.Url;
        if (string.IsNullOrWhiteSpace(next) ||
            !Uri.TryCreate(next, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return ParseQuery(uri.Query)
            .FirstOrDefault(static pair =>
                string.Equals(pair.Key, "_page_token", StringComparison.Ordinal))
            .Value;
    }

    private static bool HasInternalTag(Resource resource, string code) =>
        resource.Meta?.Tag.Any(tag =>
            string.Equals(
                tag.System,
                GcpDomainResourceMapper.InternalSystem,
                StringComparison.Ordinal) &&
            string.Equals(tag.Code, code, StringComparison.Ordinal)) == true;
}
