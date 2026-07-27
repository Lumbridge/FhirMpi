using System.Collections.Concurrent;
using System.Text;
using UnifyEmpi.Domain;
using UnifyEmpi.Storage.Abstractions;

namespace UnifyEmpi.Storage.InMemory;

public sealed class InMemoryIdentityRegistryStore : IIdentityRegistryStore
{
    private readonly ConcurrentDictionary<TenantId, TenantState> _tenants = new();

    public ValueTask<SourcePatientRecord?> GetSourcePatientAsync(
        ActorContext context,
        SourceRecordKey key,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tenant = GetTenant(context);
        lock (tenant.Sync)
        {
            return ValueTask.FromResult(
                tenant.SourcePatients.TryGetValue(key, out var patient) ? patient : null);
        }
    }

    public ValueTask<SourcePatientRecord?> GetSourcePatientByResourceIdAsync(
        ActorContext context,
        string resourceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tenant = GetTenant(context);
        lock (tenant.Sync)
        {
            return ValueTask.FromResult(tenant.SourcePatients.Values.FirstOrDefault(patient =>
                string.Equals(patient.ResourceId, resourceId, StringComparison.Ordinal)));
        }
    }

    public ValueTask<CanonicalPatient?> GetCanonicalPatientAsync(
        ActorContext context,
        EnterpriseId enterpriseId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tenant = GetTenant(context);
        lock (tenant.Sync)
        {
            return ValueTask.FromResult(
                tenant.CanonicalPatients.TryGetValue(enterpriseId, out var patient) ? patient : null);
        }
    }

    public ValueTask<EnterprisePerson?> GetPersonAsync(
        ActorContext context,
        EnterpriseId enterpriseId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tenant = GetTenant(context);
        lock (tenant.Sync)
        {
            return ValueTask.FromResult(
                tenant.Persons.TryGetValue(enterpriseId, out var person) ? person : null);
        }
    }

    public ValueTask<CandidatePage> FindCandidatesAsync(
        ActorContext context,
        IReadOnlyCollection<BlockingKey> blockingKeys,
        int maximumCandidates,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tenant = GetTenant(context);
        lock (tenant.Sync)
        {
            var ids = new HashSet<EnterpriseId>();
            foreach (var key in blockingKeys)
            {
                if (tenant.CandidateIndex.TryGetValue(key, out var matchingIds))
                {
                    ids.UnionWith(matchingIds);
                }
            }

            var isTruncated = ids.Count > maximumCandidates;
            var results = ids
                .OrderBy(static id => id.Value)
                .Take(maximumCandidates)
                .Select(id => tenant.CanonicalPatients[id])
                .Where(static patient => patient.IsActive)
                .ToArray();
            return ValueTask.FromResult(new CandidatePage(results, isTruncated));
        }
    }

    public ValueTask<Page<CanonicalPatient>> SearchCanonicalPatientsAsync(
        ActorContext context,
        CanonicalPatientSearch search,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tenant = GetTenant(context);
        lock (tenant.Sync)
        {
            IEnumerable<CanonicalPatient> query = tenant.CanonicalPatients.Values
                .Where(static patient => patient.IsActive);

            if (!string.IsNullOrWhiteSpace(search.IdentifierValue))
            {
                query = query.Where(patient => patient.Profile.Identifiers.Any(identifier =>
                    (search.IdentifierSystem is null ||
                     string.Equals(identifier.System, search.IdentifierSystem, StringComparison.Ordinal)) &&
                    string.Equals(identifier.Value, search.IdentifierValue, StringComparison.Ordinal)));
            }

            if (!string.IsNullOrWhiteSpace(search.FamilyName))
            {
                query = query.Where(patient => patient.Profile.Names.Any(name =>
                    string.Equals(name.Family, search.FamilyName, StringComparison.OrdinalIgnoreCase)));
            }

            if (search.BirthDate.HasValue)
            {
                query = query.Where(patient => patient.Profile.BirthDate == search.BirthDate);
            }

            return ValueTask.FromResult(CreatePage(
                query.OrderBy(static item => item.EnterpriseId.Value).ToArray(),
                search.Count,
                search.Cursor));
        }
    }

    public ValueTask<Page<EnterprisePerson>> SearchPersonsAsync(
        ActorContext context,
        PersonSearch search,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tenant = GetTenant(context);
        lock (tenant.Sync)
        {
            IEnumerable<EnterprisePerson> query = tenant.Persons.Values
                .Where(static person => person.IsActive);
            if (search.EnterpriseId is { } enterpriseId)
            {
                query = query.Where(person => person.EnterpriseId == enterpriseId);
            }

            return ValueTask.FromResult(CreatePage(
                query.OrderBy(static item => item.EnterpriseId.Value).ToArray(),
                search.Count,
                search.Cursor));
        }
    }

    public ValueTask<ReviewCase?> GetReviewCaseAsync(
        ActorContext context,
        Guid reviewCaseId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tenant = GetTenant(context);
        lock (tenant.Sync)
        {
            return ValueTask.FromResult(
                tenant.ReviewCases.TryGetValue(reviewCaseId, out var review) ? review : null);
        }
    }

    public ValueTask<Page<ReviewCase>> SearchReviewCasesAsync(
        ActorContext context,
        ReviewCaseSearch search,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tenant = GetTenant(context);
        lock (tenant.Sync)
        {
            var query = tenant.ReviewCases.Values.AsEnumerable();
            if (search.Status.HasValue)
            {
                query = query.Where(review => review.Status == search.Status.Value);
            }

            if (search.Kind.HasValue)
            {
                query = query.Where(review => review.Kind == search.Kind.Value);
            }

            return ValueTask.FromResult(CreatePage(
                query.OrderByDescending(static item => item.CreatedAt)
                    .ThenBy(static item => item.Id)
                    .ToArray(),
                search.Count,
                search.Cursor));
        }
    }

    public ValueTask<Page<AuditRecord>> SearchAuditRecordsAsync(
        ActorContext context,
        AuditRecordSearch search,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tenant = GetTenant(context);
        lock (tenant.Sync)
        {
            IEnumerable<AuditRecord> query = tenant.AuditRecords;
            if (!string.IsNullOrWhiteSpace(search.Action))
            {
                query = query.Where(record =>
                    string.Equals(record.Action, search.Action, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(search.Actor))
            {
                query = query.Where(record =>
                    record.Actor.Contains(search.Actor, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(search.Outcome))
            {
                query = query.Where(record =>
                    string.Equals(record.Outcome, search.Outcome, StringComparison.OrdinalIgnoreCase));
            }

            if (search.EnterpriseId.HasValue)
            {
                query = query.Where(record => record.EnterpriseId == search.EnterpriseId);
            }

            if (search.From.HasValue)
            {
                query = query.Where(record => record.RecordedAt >= search.From.Value);
            }

            if (search.To.HasValue)
            {
                query = query.Where(record => record.RecordedAt <= search.To.Value);
            }

            return ValueTask.FromResult(CreatePage(
                query.OrderByDescending(static record => record.RecordedAt)
                    .ThenBy(static record => record.Id)
                    .ToArray(),
                search.Count,
                search.Cursor));
        }
    }

    public ValueTask<TenantSettings?> GetTenantSettingsAsync(
        ActorContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tenant = GetTenant(context);
        lock (tenant.Sync)
        {
            return ValueTask.FromResult(tenant.Settings);
        }
    }

    public ValueTask<IngestionReceipt?> GetReceiptAsync(
        ActorContext context,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tenant = GetTenant(context);
        lock (tenant.Sync)
        {
            return ValueTask.FromResult(
                tenant.Receipts.TryGetValue(idempotencyKey, out var receipt) ? receipt : null);
        }
    }

    public ValueTask<RegistryCommitResult> CommitAsync(
        ActorContext context,
        RegistryMutation mutation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tenant = GetTenant(context);
        lock (tenant.Sync)
        {
            if (mutation.Receipt is not null &&
                tenant.Receipts.TryGetValue(mutation.Receipt.IdempotencyKey, out var existingReceipt))
            {
                if (!string.Equals(
                        existingReceipt.PayloadDigest,
                        mutation.Receipt.PayloadDigest,
                        StringComparison.Ordinal))
                {
                    throw new IdempotencyConflictException(mutation.Receipt.IdempotencyKey);
                }

                return ValueTask.FromResult(new RegistryCommitResult(false, true));
            }

            ValidateExpectedVersions(tenant, mutation.ExpectedVersions);
            ValidateNewEntities(tenant, mutation);

            foreach (var source in mutation.SourcePatients)
            {
                tenant.SourcePatients[source.Key] = source;
            }

            foreach (var person in mutation.Persons)
            {
                tenant.Persons[person.EnterpriseId] = person;
            }

            foreach (var patient in mutation.CanonicalPatients)
            {
                RemoveFromCandidateIndex(tenant, patient.EnterpriseId);
                tenant.CanonicalPatients[patient.EnterpriseId] = patient;
                if (patient.IsActive)
                {
                    AddToCandidateIndex(tenant, patient);
                }
            }

            foreach (var review in mutation.ReviewCases)
            {
                tenant.ReviewCases[review.Id] = review;
            }

            tenant.AuditRecords.AddRange(mutation.AuditRecords);
            if (mutation.TenantSettings is not null)
            {
                tenant.Settings = mutation.TenantSettings;
            }

            if (mutation.Receipt is not null)
            {
                tenant.Receipts[mutation.Receipt.IdempotencyKey] = mutation.Receipt;
            }

            return ValueTask.FromResult(new RegistryCommitResult(true, false));
        }
    }

    public ValueTask<RegistryStoreHealth> CheckHealthAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new RegistryStoreHealth(
            true,
            "in-memory",
            new RegistryStoreCapabilities(true, true, true, true, 500),
            "Ephemeral development and test provider."));
    }

    private TenantState GetTenant(ActorContext context) =>
        _tenants.GetOrAdd(context.TenantId, static _ => new TenantState());

    private static void ValidateExpectedVersions(
        TenantState tenant,
        IReadOnlyList<ExpectedVersion> versions)
    {
        foreach (var expected in versions)
        {
            var actual = expected.Kind switch
            {
                RegistryEntityKind.SourcePatient => tenant.SourcePatients.Values
                    .FirstOrDefault(item => item.Key.ToString() == expected.Id)?.Version,
                RegistryEntityKind.CanonicalPatient => tenant.CanonicalPatients.Values
                    .FirstOrDefault(item => item.EnterpriseId.ToString() == expected.Id)?.Version,
                RegistryEntityKind.Person => tenant.Persons.Values
                    .FirstOrDefault(item => item.EnterpriseId.ToString() == expected.Id)?.Version,
                RegistryEntityKind.ReviewCase => tenant.ReviewCases.Values
                    .FirstOrDefault(item => item.Id.ToString() == expected.Id)?.Version,
                RegistryEntityKind.TenantSettings => tenant.Settings?.Version,
                _ => null
            };

            if (actual != expected.Version)
            {
                throw new RegistryConcurrencyException(
                    $"{expected.Kind} '{expected.Id}' expected version {expected.Version}, actual {actual?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "missing"}.");
            }
        }
    }

    private static void ValidateNewEntities(TenantState tenant, RegistryMutation mutation)
    {
        foreach (var source in mutation.SourcePatients.Where(static item => item.Version == 1))
        {
            if (tenant.SourcePatients.ContainsKey(source.Key))
            {
                throw new RegistryConcurrencyException($"Source patient '{source.Key}' already exists.");
            }
        }

        foreach (var patient in mutation.CanonicalPatients.Where(static item => item.Version == 1))
        {
            if (tenant.CanonicalPatients.ContainsKey(patient.EnterpriseId))
            {
                throw new RegistryConcurrencyException($"Canonical patient '{patient.EnterpriseId}' already exists.");
            }
        }

        foreach (var person in mutation.Persons.Where(static item => item.Version == 1))
        {
            if (tenant.Persons.ContainsKey(person.EnterpriseId))
            {
                throw new RegistryConcurrencyException($"Person '{person.EnterpriseId}' already exists.");
            }
        }

        if (mutation.TenantSettings is { Version: 1 } && tenant.Settings is not null)
        {
            throw new RegistryConcurrencyException(
                $"Tenant settings for '{mutation.TenantSettings.TenantId}' already exist.");
        }
    }

    private static void AddToCandidateIndex(TenantState tenant, CanonicalPatient patient)
    {
        foreach (var key in patient.BlockingKeys)
        {
            if (!tenant.CandidateIndex.TryGetValue(key, out var ids))
            {
                ids = [];
                tenant.CandidateIndex[key] = ids;
            }

            ids.Add(patient.EnterpriseId);
        }
    }

    private static void RemoveFromCandidateIndex(TenantState tenant, EnterpriseId enterpriseId)
    {
        foreach (var ids in tenant.CandidateIndex.Values)
        {
            ids.Remove(enterpriseId);
        }
    }

    private static Page<T> CreatePage<T>(
        IReadOnlyList<T> allItems,
        int requestedCount,
        string? cursor)
    {
        var count = Math.Clamp(requestedCount, 1, 100);
        var offset = DecodeCursor(cursor);
        if (offset > allItems.Count)
        {
            offset = allItems.Count;
        }

        var items = allItems.Skip(offset).Take(count).ToArray();
        var nextOffset = offset + items.Length;
        var nextCursor = nextOffset < allItems.Count ? EncodeCursor(nextOffset) : null;
        return new Page<T>(items, nextCursor);
    }

    private static string EncodeCursor(int offset) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(offset.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    private static int DecodeCursor(string? cursor)
    {
        if (cursor is null)
        {
            return 0;
        }

        try
        {
            var value = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            return int.TryParse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                out var offset)
                ? Math.Max(0, offset)
                : 0;
        }
        catch (FormatException)
        {
            return 0;
        }
    }

    private sealed class TenantState
    {
        public object Sync { get; } = new();

        public Dictionary<SourceRecordKey, SourcePatientRecord> SourcePatients { get; } = [];

        public Dictionary<EnterpriseId, CanonicalPatient> CanonicalPatients { get; } = [];

        public Dictionary<EnterpriseId, EnterprisePerson> Persons { get; } = [];

        public Dictionary<BlockingKey, HashSet<EnterpriseId>> CandidateIndex { get; } = [];

        public Dictionary<Guid, ReviewCase> ReviewCases { get; } = [];

        public Dictionary<string, IngestionReceipt> Receipts { get; } =
            new(StringComparer.Ordinal);

        public List<AuditRecord> AuditRecords { get; } = [];

        public TenantSettings? Settings { get; set; }
    }
}
