using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hl7.Fhir.Model;
using UnifyEmpi.Domain;
using UnifyEmpi.Fhir.R4;
using Task = Hl7.Fhir.Model.Task;

namespace UnifyEmpi.Storage.Gcp;

internal static class GcpDomainResourceMapper
{
    internal const string InternalSystem = "https://unifyempi.dev/CodeSystem/internal-resource";
    internal const string BlockingSystemPrefix = "https://unifyempi.dev/CodeSystem/blocking/";
    internal const string SourceKeySystemPrefix = "https://unifyempi.dev/Id/source-key/";
    private const string EnterpriseExtension = "https://unifyempi.dev/StructureDefinition/enterprise-id";
    private const string SourceLocalIdExtension = "https://unifyempi.dev/StructureDefinition/source-local-id";
    private const string SourceTrustExtension = "https://unifyempi.dev/StructureDefinition/source-trust";
    private const string CreatedAtExtension = "https://unifyempi.dev/StructureDefinition/created-at";
    private const string SourcesExtension = "https://unifyempi.dev/StructureDefinition/source-links";
    private const string LogicalVersionExtension = "https://unifyempi.dev/StructureDefinition/logical-version";
    private const string ReviewEnvelopeExtension = "https://unifyempi.dev/StructureDefinition/review-envelope";
    private const string AuditEnvelopeExtension = "https://unifyempi.dev/StructureDefinition/audit-envelope";
    private const string ReceiptEnvelopeExtension = "https://unifyempi.dev/StructureDefinition/receipt-envelope";
    private const string SettingsEnvelopeExtension = "https://unifyempi.dev/StructureDefinition/tenant-settings-envelope";

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static Patient ToSourcePatient(SourcePatientRecord source, TenantId tenant)
    {
        var resource = FhirR4Mapper.ToSourcePatient(source, tenant);
        resource.Meta!.Tag.Add(new Coding(InternalSystem, "source-patient"));
        resource.Identifier.Add(new Identifier(
            $"{SourceKeySystemPrefix}{Uri.EscapeDataString(source.Key.SourceSystem.Value)}",
            source.Key.LocalId));
        resource.Extension.Add(new Extension(
            EnterpriseExtension,
            new FhirString(source.EnterpriseId.ToString())));
        resource.Extension.Add(new Extension(
            SourceLocalIdExtension,
            new FhirString(source.Key.LocalId)));
        resource.Extension.Add(new Extension(
            SourceTrustExtension,
            new Integer(source.SourceTrust)));
        AddLogicalVersion(resource, source.Version);
        return resource;
    }

    public static SourcePatientRecord ToSourcePatient(Resource resource, TenantId tenant)
    {
        var patient = resource as Patient ??
                      throw new InvalidOperationException("Expected a Patient resource.");
        FhirR4Mapper.AssertTenant(patient, tenant);
        RequireTag(patient, "source-patient");
        var sourceSystem = GetStringExtension(patient, FhirR4Constants.SourceSystemExtension);
        var localId = GetStringExtension(patient, SourceLocalIdExtension);
        return new SourcePatientRecord(
            new SourceRecordKey(new SourceSystemId(sourceSystem), localId),
            patient.Id ?? throw new InvalidOperationException("Stored Patient.id is missing."),
            ParseEnterpriseId(GetStringExtension(patient, EnterpriseExtension)),
            WithoutInternalIdentifiers(FhirR4Mapper.ToTrustedDomain(patient)),
            GetIntegerExtension(patient, SourceTrustExtension),
            patient.Meta!.LastUpdated ?? DateTimeOffset.MinValue,
            ParseVersion(patient));
    }

    public static Patient ToCanonicalPatient(CanonicalPatient canonical, TenantId tenant)
    {
        var resource = FhirR4Mapper.ToCanonicalPatient(canonical, tenant);
        resource.Meta!.Tag.Add(new Coding(InternalSystem, "canonical-patient"));
        foreach (var key in canonical.BlockingKeys)
        {
            resource.Meta.Tag.Add(new Coding($"{BlockingSystemPrefix}{key.Version}", key.Value));
        }

        resource.Extension.Add(new Extension(
            SourceTrustExtension,
            new Integer(canonical.SurvivorshipTrust)));
        resource.Extension.Add(new Extension(
            CreatedAtExtension,
            new FhirDateTime(canonical.CreatedAt)));
        resource.Extension.Add(new Extension(
            SourcesExtension,
            new FhirString(JsonSerializer.Serialize(
                canonical.Sources.Select(static source => new SourceLinkEnvelope(
                    source.SourceSystem.Value,
                    source.LocalId)),
                JsonOptions))));
        AddLogicalVersion(resource, canonical.Version);
        return resource;
    }

    public static CanonicalPatient ToCanonicalPatient(Resource resource, TenantId tenant)
    {
        var patient = resource as Patient ??
                      throw new InvalidOperationException("Expected a Patient resource.");
        FhirR4Mapper.AssertTenant(patient, tenant);
        RequireTag(patient, "canonical-patient");
        var sources = JsonSerializer.Deserialize<SourceLinkEnvelope[]>(
                          GetStringExtension(patient, SourcesExtension),
                          JsonOptions) ??
                      [];
        var replacedByReference = patient.Link.FirstOrDefault(link =>
            link.Type == Patient.LinkType.ReplacedBy)?.Other?.Reference;
        EnterpriseId? replacedBy = TryParseReferenceId(
            replacedByReference,
            "Patient",
            out var replacement)
            ? replacement
            : null;
        return new CanonicalPatient(
            ParseEnterpriseId(patient.Id),
            WithoutInternalIdentifiers(FhirR4Mapper.ToTrustedDomain(patient)),
            sources.Select(static source => new SourceRecordKey(
                new SourceSystemId(source.SourceSystem),
                source.LocalId)).ToArray(),
            patient.Meta!.Tag
                .Where(static tag =>
                    tag.System?.StartsWith(BlockingSystemPrefix, StringComparison.Ordinal) == true &&
                    !string.IsNullOrWhiteSpace(tag.Code))
                .Select(static tag => new BlockingKey(
                    tag.System![BlockingSystemPrefix.Length..],
                    tag.Code!))
                .ToArray(),
            GetIntegerExtension(patient, SourceTrustExtension),
            GetDateTimeExtension(patient, CreatedAtExtension),
            patient.Meta.LastUpdated ?? DateTimeOffset.MinValue,
            ParseVersion(patient),
            patient.Active ?? true,
            replacedBy);
    }

    public static Person ToPerson(EnterprisePerson person, TenantId tenant)
    {
        var resource = FhirR4Mapper.ToPerson(person, tenant);
        resource.Meta!.Tag.Add(new Coding(InternalSystem, "enterprise-person"));
        resource.Extension.Add(new Extension(
            CreatedAtExtension,
            new FhirDateTime(person.CreatedAt)));
        for (var index = 0; index < person.Links.Count; index++)
        {
            var domainLink = person.Links[index];
            var fhirLink = resource.Link[index];
            fhirLink.Extension.Add(new Extension(
                FhirR4Constants.SourceSystemExtension,
                new FhirString(domainLink.Source.SourceSystem.Value)));
            fhirLink.Extension.Add(new Extension(
                SourceLocalIdExtension,
                new FhirString(domainLink.Source.LocalId)));
            fhirLink.Extension.Add(new Extension(
                "https://unifyempi.dev/StructureDefinition/linked-at",
                new FhirDateTime(domainLink.LinkedAt)));
            fhirLink.Extension.Add(new Extension(
                "https://unifyempi.dev/StructureDefinition/link-reason",
                new FhirString(domainLink.Reason)));
        }

        AddLogicalVersion(resource, person.Version);
        return resource;
    }

    public static EnterprisePerson ToPerson(Resource resource, TenantId tenant)
    {
        var person = resource as Person ??
                     throw new InvalidOperationException("Expected a Person resource.");
        FhirR4Mapper.AssertTenant(person, tenant);
        RequireTag(person, "enterprise-person");
        var links = person.Link
            .Where(static link =>
                link.Target?.Reference?.StartsWith("Patient/", StringComparison.Ordinal) == true)
            .Select(link => new PersonLink(
                new SourceRecordKey(
                    new SourceSystemId(GetStringExtension(
                        link,
                        FhirR4Constants.SourceSystemExtension)),
                    GetStringExtension(link, SourceLocalIdExtension)),
                link.Target.Reference!["Patient/".Length..],
                link.Assurance switch
                {
                    Person.IdentityAssuranceLevel.Level2 => LinkAssurance.Level2,
                    Person.IdentityAssuranceLevel.Level3 => LinkAssurance.Level3,
                    Person.IdentityAssuranceLevel.Level4 => LinkAssurance.Level4,
                    _ => LinkAssurance.Level1
                },
                GetDateTimeExtension(
                    link,
                    "https://unifyempi.dev/StructureDefinition/linked-at"),
                GetStringExtension(
                    link,
                    "https://unifyempi.dev/StructureDefinition/link-reason")))
            .ToArray();
        var replacementReference = person.Link.FirstOrDefault(link =>
            link.Target?.Reference?.StartsWith("Person/", StringComparison.Ordinal) == true)
            ?.Target.Reference;
        EnterpriseId? replacedBy = TryParseReferenceId(
            replacementReference,
            "Person",
            out var replacement)
            ? replacement
            : null;
        return new EnterprisePerson(
            ParseEnterpriseId(person.Id),
            links,
            GetDateTimeExtension(person, CreatedAtExtension),
            person.Meta!.LastUpdated ?? DateTimeOffset.MinValue,
            ParseVersion(person),
            person.Active ?? true,
            replacedBy);
    }

    public static Task ToReviewTask(ReviewCase review, TenantId tenant)
    {
        var task = new Task
        {
            Id = review.Id.ToString("D"),
            Meta = FhirR4Mapper.CreateMeta(tenant, review.Version, review.UpdatedAt),
            Status = review.Status switch
            {
                ReviewCaseStatus.Pending => Task.TaskStatus.Requested,
                ReviewCaseStatus.AwaitingSecondApproval => Task.TaskStatus.Accepted,
                ReviewCaseStatus.Linked => Task.TaskStatus.Completed,
                ReviewCaseStatus.Split => Task.TaskStatus.Completed,
                ReviewCaseStatus.Rejected => Task.TaskStatus.Rejected,
                ReviewCaseStatus.Superseded => Task.TaskStatus.Cancelled,
                _ => Task.TaskStatus.Requested
            },
            Intent = Task.TaskIntent.Order,
            Code = new CodeableConcept(InternalSystem, "identity-match-review"),
            For = new ResourceReference($"Patient/{review.SubjectEnterpriseId}"),
            AuthoredOn = review.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            LastModified = review.UpdatedAt.ToString("O", CultureInfo.InvariantCulture)
        };
        task.Meta!.Tag.Add(new Coding(InternalSystem, "review-case"));
        task.Extension.Add(new Extension(
            ReviewEnvelopeExtension,
            new FhirString(JsonSerializer.Serialize(review, JsonOptions))));
        AddLogicalVersion(task, review.Version);
        return task;
    }

    public static ReviewCase ToReviewCase(Resource resource, TenantId tenant)
    {
        var task = resource as Task ??
                   throw new InvalidOperationException("Expected a Task resource.");
        FhirR4Mapper.AssertTenant(task, tenant);
        RequireTag(task, "review-case");
        var review = JsonSerializer.Deserialize<ReviewCase>(
                         GetStringExtension(task, ReviewEnvelopeExtension),
                         JsonOptions) ??
                     throw new InvalidOperationException("The review-case envelope is invalid.");
        return review with
        {
            Version = ParseVersion(task),
            UpdatedAt = task.Meta!.LastUpdated ?? review.UpdatedAt
        };
    }

    public static AuditEvent ToAuditEvent(AuditRecord audit, TenantId tenant)
    {
        var resource = new AuditEvent
        {
            Id = audit.Id.ToString("D"),
            Meta = FhirR4Mapper.CreateMeta(tenant, 1, audit.RecordedAt),
            Type = new Coding(InternalSystem, audit.Action),
            Recorded = audit.RecordedAt,
            Outcome = audit.Outcome == "success"
                ? AuditEvent.AuditEventOutcome.N0
                : AuditEvent.AuditEventOutcome.N8,
            OutcomeDesc = audit.Reason,
            Agent =
            [
                new AuditEvent.AgentComponent
                {
                    Who = new ResourceReference { Identifier = new Identifier("urn:ietf:rfc:3986", audit.Actor) },
                    Requestor = true
                }
            ],
            Source = new AuditEvent.SourceComponent
            {
                Observer = new ResourceReference
                {
                    Identifier = new Identifier(
                        "urn:ietf:rfc:3986",
                        "urn:unifyempi:registry")
                }
            },
            Entity =
            [
                new AuditEvent.EntityComponent
                {
                    What = audit.EnterpriseId is null
                        ? null
                        : new ResourceReference($"Patient/{audit.EnterpriseId}"),
                    Name = audit.CorrelationId
                }
            ]
        };
        resource.Meta!.Tag.Add(new Coding(InternalSystem, "audit"));
        resource.Extension.Add(new Extension(
            AuditEnvelopeExtension,
            new FhirString(JsonSerializer.Serialize(audit, JsonOptions))));
        AddLogicalVersion(resource, 1);
        return resource;
    }

    public static AuditRecord ToAuditRecord(Resource resource, TenantId tenant)
    {
        var audit = resource as AuditEvent ??
                    throw new InvalidOperationException("Expected an AuditEvent resource.");
        FhirR4Mapper.AssertTenant(audit, tenant);
        RequireTag(audit, "audit");
        var envelope = audit.Extension.FirstOrDefault(extension =>
            string.Equals(extension.Url, AuditEnvelopeExtension, StringComparison.Ordinal))?.Value;
        if (envelope is FhirString { Value: { } value })
        {
            return JsonSerializer.Deserialize<AuditRecord>(value, JsonOptions) ??
                   throw new InvalidOperationException("The audit envelope is invalid.");
        }

        var enterpriseReference = audit.Entity
            .Select(static entity => entity.What?.Reference)
            .FirstOrDefault(reference =>
                reference?.StartsWith("Patient/", StringComparison.Ordinal) == true);
        EnterpriseId? enterpriseId = TryParseReferenceId(
            enterpriseReference,
            "Patient",
            out var parsedEnterpriseId)
            ? parsedEnterpriseId
            : null;
        return new AuditRecord(
            Guid.TryParse(audit.Id, out var id) ? id : Guid.Empty,
            audit.Type?.Code ?? "unknown",
            audit.Agent.FirstOrDefault()?.Who?.Identifier?.Value ?? "unknown",
            audit.Outcome == AuditEvent.AuditEventOutcome.N0 ? "success" : "failure",
            audit.OutcomeDesc ?? string.Empty,
            enterpriseId,
            null,
            audit.Recorded ?? DateTimeOffset.MinValue,
            audit.Entity.FirstOrDefault()?.Name ?? string.Empty);
    }

    public static Basic ToReceipt(IngestionReceipt receipt, TenantId tenant)
    {
        var resource = new Basic
        {
            Id = ReceiptResourceId(tenant, receipt.IdempotencyKey),
            Meta = FhirR4Mapper.CreateMeta(tenant, 1, receipt.RecordedAt),
            Code = new CodeableConcept(InternalSystem, "idempotency-receipt"),
            Created = receipt.RecordedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        };
        resource.Meta!.Tag.Add(new Coding(InternalSystem, "idempotency-receipt"));
        resource.Extension.Add(new Extension(
            ReceiptEnvelopeExtension,
            new FhirString(JsonSerializer.Serialize(receipt, JsonOptions))));
        AddLogicalVersion(resource, 1);
        return resource;
    }

    public static IngestionReceipt ToReceipt(Resource resource, TenantId tenant)
    {
        var basic = resource as Basic ??
                    throw new InvalidOperationException("Expected a Basic resource.");
        FhirR4Mapper.AssertTenant(basic, tenant);
        RequireTag(basic, "idempotency-receipt");
        return JsonSerializer.Deserialize<IngestionReceipt>(
                   GetStringExtension(basic, ReceiptEnvelopeExtension),
                   JsonOptions) ??
               throw new InvalidOperationException("The receipt envelope is invalid.");
    }

    public static Basic ToTenantSettings(TenantSettings settings, TenantId tenant)
    {
        if (settings.TenantId != tenant)
        {
            throw new InvalidOperationException("Tenant settings cannot cross a tenant boundary.");
        }

        var resource = new Basic
        {
            Id = TenantSettingsResourceId(tenant),
            Meta = FhirR4Mapper.CreateMeta(tenant, settings.Version, settings.UpdatedAt),
            Code = new CodeableConcept(InternalSystem, "tenant-settings"),
            Created = settings.UpdatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        };
        resource.Meta!.Tag.Add(new Coding(InternalSystem, "tenant-settings"));
        resource.Extension.Add(new Extension(
            SettingsEnvelopeExtension,
            new FhirString(JsonSerializer.Serialize(
                new TenantSettingsEnvelope(
                    settings.TenantId.Value,
                    settings.MatchingProfileVersion,
                    settings.PossibleThreshold,
                    settings.ProbableThreshold,
                    settings.RequiredLinkApprovals,
                    settings.Sources.Select(static source => new SourceSettingsEnvelope(
                        source.SourceSystem.Value,
                        source.Trust,
                        source.IsAuthoritative)).ToArray(),
                    settings.UpdatedAt,
                    settings.UpdatedBy),
                JsonOptions))));
        AddLogicalVersion(resource, settings.Version);
        return resource;
    }

    public static TenantSettings ToTenantSettings(Resource resource, TenantId tenant)
    {
        var basic = resource as Basic ??
                    throw new InvalidOperationException("Expected a Basic resource.");
        FhirR4Mapper.AssertTenant(basic, tenant);
        RequireTag(basic, "tenant-settings");
        var envelope = JsonSerializer.Deserialize<TenantSettingsEnvelope>(
                           GetStringExtension(basic, SettingsEnvelopeExtension),
                           JsonOptions) ??
                       throw new InvalidOperationException("The tenant-settings envelope is invalid.");
        if (!string.Equals(envelope.TenantId, tenant.Value, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The stored tenant settings belong to another tenant.");
        }

        return new TenantSettings(
            tenant,
            envelope.MatchingProfileVersion,
            envelope.PossibleThreshold,
            envelope.ProbableThreshold,
            envelope.RequiredLinkApprovals,
            envelope.Sources.Select(static source => new SourceSystemSettings(
                new SourceSystemId(source.SourceSystem),
                source.Trust,
                source.IsAuthoritative)).ToArray(),
            envelope.UpdatedAt,
            envelope.UpdatedBy,
            ParseVersion(basic));
    }

    public static string ReceiptResourceId(TenantId tenant, string key)
    {
        var digest = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{tenant.Value}\0{key}"));
        return $"receipt-{Convert.ToHexString(digest[..16]).ToLowerInvariant()}";
    }

    public static string TenantSettingsResourceId(TenantId tenant)
    {
        var digest = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(tenant.Value));
        return $"settings-{Convert.ToHexString(digest[..16]).ToLowerInvariant()}";
    }

    public static void AssertTenant(Resource resource, TenantId tenant) =>
        FhirR4Mapper.AssertTenant(resource, tenant);

    private static IdentityProfile WithoutInternalIdentifiers(IdentityProfile profile) =>
        profile with
        {
            Identifiers = profile.Identifiers.Where(identifier =>
                !string.Equals(
                    identifier.System,
                    FhirR4Constants.EnterpriseIdentifierSystem,
                    StringComparison.Ordinal) &&
                !identifier.System.StartsWith(SourceKeySystemPrefix, StringComparison.Ordinal))
                .ToArray()
        };

    private static EnterpriseId ParseEnterpriseId(string? value) =>
        Guid.TryParse(value, out var parsed)
            ? new EnterpriseId(parsed)
            : throw new InvalidOperationException("The stored enterprise ID is invalid.");

    internal static long ParseVersion(Resource resource) =>
        resource is DomainResource domainResource &&
        long.TryParse(
            GetStringExtension(domainResource, LogicalVersionExtension),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var version)
            ? version
            : throw new InvalidOperationException("The stored resource version is invalid.");

    private static void AddLogicalVersion(DomainResource resource, long version) =>
        resource.Extension.Add(new Extension(
            LogicalVersionExtension,
            new FhirString(version.ToString(CultureInfo.InvariantCulture))));

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new EnterpriseIdJsonConverter());
        options.Converters.Add(new SourceSystemIdJsonConverter());
        options.Converters.Add(new TenantIdJsonConverter());
        return options;
    }

    private static string GetStringExtension(DomainResource resource, string url) =>
        GetStringExtension(resource.Extension, url);

    private static string GetStringExtension(BackboneElement element, string url) =>
        GetStringExtension(element.Extension, url);

    private static string GetStringExtension(IEnumerable<Extension> extensions, string url) =>
        extensions.FirstOrDefault(extension =>
            string.Equals(extension.Url, url, StringComparison.Ordinal))?.Value switch
        {
            FhirString { Value: not null } value => value.Value,
            _ => throw new InvalidOperationException($"Required internal extension '{url}' is missing.")
        };

    private static int GetIntegerExtension(DomainResource resource, string url) =>
        resource.Extension.FirstOrDefault(extension =>
            string.Equals(extension.Url, url, StringComparison.Ordinal))?.Value switch
        {
            Integer { Value: not null } value => value.Value.Value,
            _ => throw new InvalidOperationException($"Required internal extension '{url}' is missing.")
        };

    private static DateTimeOffset GetDateTimeExtension(DomainResource resource, string url) =>
        GetDateTimeExtension(resource.Extension, url);

    private static DateTimeOffset GetDateTimeExtension(BackboneElement element, string url) =>
        GetDateTimeExtension(element.Extension, url);

    private static DateTimeOffset GetDateTimeExtension(
        IEnumerable<Extension> extensions,
        string url) =>
        extensions.FirstOrDefault(extension =>
            string.Equals(extension.Url, url, StringComparison.Ordinal))?.Value switch
        {
            FhirDateTime value when DateTimeOffset.TryParse(
                value.Value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed) => parsed,
            _ => throw new InvalidOperationException($"Required internal extension '{url}' is missing.")
        };

    private static void RequireTag(Resource resource, string code)
    {
        if (resource.Meta?.Tag.Any(tag =>
                string.Equals(tag.System, InternalSystem, StringComparison.Ordinal) &&
                string.Equals(tag.Code, code, StringComparison.Ordinal)) != true)
        {
            throw new InvalidOperationException(
                $"Stored resource is missing the '{code}' registry tag.");
        }
    }

    private static bool TryParseReferenceId(
        string? reference,
        string resourceType,
        out EnterpriseId enterpriseId)
    {
        var prefix = $"{resourceType}/";
        if (reference?.StartsWith(prefix, StringComparison.Ordinal) == true &&
            Guid.TryParse(reference[prefix.Length..], out var parsed))
        {
            enterpriseId = new EnterpriseId(parsed);
            return true;
        }

        enterpriseId = default;
        return false;
    }

    private sealed record SourceLinkEnvelope(string SourceSystem, string LocalId);

    private sealed record SourceSettingsEnvelope(
        string SourceSystem,
        int Trust,
        bool IsAuthoritative);

    private sealed record TenantSettingsEnvelope(
        string TenantId,
        string MatchingProfileVersion,
        double PossibleThreshold,
        double ProbableThreshold,
        int RequiredLinkApprovals,
        IReadOnlyList<SourceSettingsEnvelope> Sources,
        DateTimeOffset UpdatedAt,
        string UpdatedBy);

    private sealed class EnterpriseIdJsonConverter : JsonConverter<EnterpriseId>
    {
        public override EnterpriseId Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String &&
            Guid.TryParse(reader.GetString(), out var value)
                ? new EnterpriseId(value)
                : throw new JsonException("The enterprise ID is invalid.");

        public override void Write(
            Utf8JsonWriter writer,
            EnterpriseId value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }

    private sealed class SourceSystemIdJsonConverter : JsonConverter<SourceSystemId>
    {
        public override SourceSystemId Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String
                ? new SourceSystemId(
                    reader.GetString() ??
                    throw new JsonException("The source-system ID is missing."))
                : throw new JsonException("The source-system ID is invalid.");

        public override void Write(
            Utf8JsonWriter writer,
            SourceSystemId value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }

    private sealed class TenantIdJsonConverter : JsonConverter<TenantId>
    {
        public override TenantId Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String
                ? new TenantId(
                    reader.GetString() ??
                    throw new JsonException("The tenant ID is missing."))
                : throw new JsonException("The tenant ID is invalid.");

        public override void Write(
            Utf8JsonWriter writer,
            TenantId value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
