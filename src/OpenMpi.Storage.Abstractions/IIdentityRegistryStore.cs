using OpenMpi.Domain;

namespace OpenMpi.Storage.Abstractions;

public interface IIdentityRegistryStore
{
    ValueTask<SourcePatientRecord?> GetSourcePatientAsync(
        ActorContext context,
        SourceRecordKey key,
        CancellationToken cancellationToken);

    ValueTask<SourcePatientRecord?> GetSourcePatientByResourceIdAsync(
        ActorContext context,
        string resourceId,
        CancellationToken cancellationToken);

    ValueTask<CanonicalPatient?> GetCanonicalPatientAsync(
        ActorContext context,
        EnterpriseId enterpriseId,
        CancellationToken cancellationToken);

    ValueTask<EnterprisePerson?> GetPersonAsync(
        ActorContext context,
        EnterpriseId enterpriseId,
        CancellationToken cancellationToken);

    ValueTask<CandidatePage> FindCandidatesAsync(
        ActorContext context,
        IReadOnlyCollection<BlockingKey> blockingKeys,
        int maximumCandidates,
        CancellationToken cancellationToken);

    ValueTask<Page<CanonicalPatient>> SearchCanonicalPatientsAsync(
        ActorContext context,
        CanonicalPatientSearch search,
        CancellationToken cancellationToken);

    ValueTask<Page<EnterprisePerson>> SearchPersonsAsync(
        ActorContext context,
        PersonSearch search,
        CancellationToken cancellationToken);

    ValueTask<ReviewCase?> GetReviewCaseAsync(
        ActorContext context,
        Guid reviewCaseId,
        CancellationToken cancellationToken);

    ValueTask<Page<ReviewCase>> SearchReviewCasesAsync(
        ActorContext context,
        ReviewCaseSearch search,
        CancellationToken cancellationToken);

    ValueTask<Page<AuditRecord>> SearchAuditRecordsAsync(
        ActorContext context,
        AuditRecordSearch search,
        CancellationToken cancellationToken);

    ValueTask<TenantSettings?> GetTenantSettingsAsync(
        ActorContext context,
        CancellationToken cancellationToken);

    ValueTask<IngestionReceipt?> GetReceiptAsync(
        ActorContext context,
        string idempotencyKey,
        CancellationToken cancellationToken);

    ValueTask<RegistryCommitResult> CommitAsync(
        ActorContext context,
        RegistryMutation mutation,
        CancellationToken cancellationToken);

    ValueTask<RegistryStoreHealth> CheckHealthAsync(CancellationToken cancellationToken);
}
