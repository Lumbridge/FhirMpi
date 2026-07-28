using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Hl7.Fhir.Model;
using UnifyEmpi.Application;
using UnifyEmpi.Domain;
using UnifyEmpi.Fhir.R4;
using UnifyEmpi.Storage.Abstractions;

namespace UnifyEmpi.Api;

public static class FhirEndpoints
{
    private static readonly string[] SmartGrantTypes = ["client_credentials"];
    private static readonly string[] SmartAuthenticationMethods =
        ["private_key_jwt", "client_secret_basic"];
    private static readonly string[] SmartScopes =
    [
        "system/Patient.read",
        "system/Patient.write",
        "system/Person.read",
        MpiScopes.Match,
        MpiScopes.Review,
        MpiScopes.Audit,
        MpiScopes.Operations,
        MpiScopes.ConfigurationRead,
        MpiScopes.ConfigurationWrite,
        MpiScopes.Admin
    ];
    private static readonly string[] SmartCapabilities =
        ["client-confidential-asymmetric", "permission-v2"];

    public static IEndpointRouteBuilder MapUnifyEmpiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/fhir/R4/metadata", GetMetadata);
        endpoints.MapGet("/.well-known/smart-configuration", GetSmartConfiguration);

        var fhir = endpoints.MapGroup("/fhir/R4").RequireAuthorization().RequireRateLimiting("tenant");
        fhir.MapPost("/Patient", CreatePatient);
        fhir.MapPut("/Patient/{id}", UpdatePatient);
        fhir.MapGet("/Patient/{id}", ReadPatient);
        fhir.MapGet("/Patient", SearchPatients);
        fhir.MapPost("/Patient/$match", MatchPatient);
        fhir.MapGet("/Person/{id}", ReadPerson);
        fhir.MapGet("/Person", SearchPersons);

        var reviews = endpoints.MapGroup("/api/v1/review-cases")
            .RequireAuthorization()
            .RequireRateLimiting("tenant");
        reviews.MapGet("/", SearchReviewCases);
        reviews.MapGet("/{id:guid}", ReadReviewCase);
        reviews.MapGet("/{id:guid}/detail", ReadReviewCaseDetail);
        reviews.MapPost("/{id:guid}/decisions", DecideReviewCase);
        reviews.MapPost("/manual-duplicate", CreateDuplicateReviewCase);
        reviews.MapPost("/split", CreateSplitReviewCase);

        var operations = endpoints.MapGroup("/api/v1")
            .RequireAuthorization()
            .RequireRateLimiting("tenant");
        operations.MapGet("/operations/summary", GetOperationalSummary);
        operations.MapGet("/registry/patients/{id:guid}", GetPatientIdentity);
        operations.MapGet("/registry/patients/{id:guid}/duplicates", FindDuplicates);
        operations.MapGet("/audit-events", SearchAuditEvents);
        operations.MapGet("/tenant/settings", GetTenantSettings);
        operations.MapPut("/tenant/settings", UpdateTenantSettings);
        operations.MapPost("/matching/evaluation", EvaluateGroundTruth);
        operations.MapPost("/matching/calibration/fellegi-sunter", CalibrateFellegiSunter);
        operations.MapPost("/maintenance/reindex", StartReindex);
        operations.MapPost("/maintenance/reconciliation", StartPopulationReconciliation);
        operations.MapGet("/maintenance/jobs", SearchMaintenanceJobs);
        operations.MapGet("/maintenance/jobs/{id:guid}", GetMaintenanceJob);
        operations.MapPost("/maintenance/jobs/{id:guid}/cancel", CancelMaintenanceJob);
        return endpoints;
    }

    private static FhirResult GetMetadata(HttpContext context)
    {
        var statement = new CapabilityStatement
        {
            Id = "unifyempi-r4",
            Url = $"{context.Request.Scheme}://{context.Request.Host}/fhir/R4/metadata",
            Version = "1.0.0",
            Name = "UnifyEmpiR4CapabilityStatement",
            Status = PublicationStatus.Active,
            Experimental = false,
            Date = "2026-07-25",
            Publisher = "UnifyEMPI",
            Kind = CapabilityStatementKind.Instance,
            FhirVersion = FHIRVersion.N4_0_1,
            Format = ["application/fhir+json", "application/fhir+xml"]
        };
        statement.Rest.Add(new CapabilityStatement.RestComponent
        {
            Mode = CapabilityStatement.RestfulCapabilityMode.Server,
            Security = new CapabilityStatement.SecurityComponent
            {
                Cors = true,
                Service =
                [
                    new CodeableConcept(
                        "http://terminology.hl7.org/CodeSystem/restful-security-service",
                        "SMART-on-FHIR")
                ]
            },
            Resource =
            [
                ResourceCapability(
                    ResourceType.Patient,
                    [CapabilityStatement.TypeRestfulInteraction.Read,
                        CapabilityStatement.TypeRestfulInteraction.SearchType,
                        CapabilityStatement.TypeRestfulInteraction.Create,
                        CapabilityStatement.TypeRestfulInteraction.Update],
                    ["identifier", "family", "birthdate"]),
                ResourceCapability(
                    ResourceType.Person,
                    [CapabilityStatement.TypeRestfulInteraction.Read,
                        CapabilityStatement.TypeRestfulInteraction.SearchType],
                    ["identifier"])
            ],
            Operation =
            [
                new CapabilityStatement.OperationComponent
                {
                    Name = "match",
                    Definition = "http://hl7.org/fhir/OperationDefinition/Patient-match"
                }
            ]
        });
        return new FhirResult(statement);
    }

    private static IResult GetSmartConfiguration(HttpContext context)
    {
        var authority = context.RequestServices
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<AuthenticationOptions>>()
            .Value.Authority.TrimEnd('/');
        return Results.Json(new
        {
            token_endpoint = $"{authority}/connect/token",
            grant_types_supported = SmartGrantTypes,
            token_endpoint_auth_methods_supported = SmartAuthenticationMethods,
            scopes_supported = SmartScopes,
            capabilities = SmartCapabilities
        });
    }

    private static async Task<IResult> CreatePatient(
        HttpContext http,
        RegistryService registry,
        ActorContextFactory actors,
        FhirResourceCodec codec,
        IPatientProfileValidator validator,
        CancellationToken cancellationToken)
    {
        var actor = actors.Create();
        Require(MpiScopes.CanWritePatient(actor) && actor.SourceSystem is not null);
        var patient = await FhirRequest.ReadAsync<Patient>(http.Request, codec, cancellationToken);
        RejectIdentityOverrides(patient);
        var validation = await validator.ValidateWriteAsync(patient, cancellationToken);
        if (!validation.IsSuccessful)
        {
            return new FhirResult(validation.ToOperationOutcome(), StatusCodes.Status422UnprocessableEntity);
        }

        var localId = string.IsNullOrWhiteSpace(patient.Id)
            ? Guid.CreateVersion7().ToString("D")
            : patient.Id;
        var result = await registry.UpsertPatientAsync(
            actor,
            new UpsertPatientCommand(
                new SourceRecordKey(actor.SourceSystem!.Value, localId),
                FhirR4Mapper.ToDomain(patient),
                http.Request.Headers["Idempotency-Key"].FirstOrDefault(),
                Digest(patient, codec),
                ExpectedVersion: 0),
            cancellationToken);
        var resource = FhirR4Mapper.ToSourcePatient(result.SourcePatient, actor.TenantId);
        var location = $"/fhir/R4/Patient/{resource.Id}/_history/{result.SourcePatient.Version}";
        return new FhirResult(
            resource,
            StatusCodes.Status201Created,
            location,
            FhirRequest.Etag(result.SourcePatient.Version));
    }

    private static async Task<IResult> UpdatePatient(
        string id,
        HttpContext http,
        RegistryService registry,
        ActorContextFactory actors,
        FhirResourceCodec codec,
        IPatientProfileValidator validator,
        CancellationToken cancellationToken)
    {
        var actor = actors.Create();
        Require(MpiScopes.CanWritePatient(actor) && actor.SourceSystem is not null);
        var sourceSystem = actor.SourceSystem ??
                           throw new RegistryAuthorisationException(
                               "The source_system claim is required.");
        var existing = await registry.GetSourcePatientByResourceIdAsync(actor, id, cancellationToken)
            ?? throw new RegistryNotFoundException("Patient", id);
        if (existing.Key.SourceSystem != sourceSystem)
        {
            throw new RegistryAuthorisationException("The source system does not own this patient.");
        }

        var expected = FhirRequest.ParseWeakEtag(http.Request.Headers.IfMatch.FirstOrDefault());
        if (!expected.HasValue)
        {
            throw new RegistryConcurrencyException("If-Match is required for Patient updates.");
        }

        if (expected.Value != existing.Version)
        {
            throw new RegistryConcurrencyException("The Patient ETag does not match.");
        }

        var patient = await FhirRequest.ReadAsync<Patient>(http.Request, codec, cancellationToken);
        RejectIdentityOverrides(patient);
        if (!string.IsNullOrWhiteSpace(patient.Id) &&
            !string.Equals(patient.Id, id, StringComparison.Ordinal))
        {
            throw new FormatException("Patient.id must match the route identifier.");
        }

        var validation = await validator.ValidateWriteAsync(patient, cancellationToken);
        if (!validation.IsSuccessful)
        {
            return new FhirResult(validation.ToOperationOutcome(), StatusCodes.Status422UnprocessableEntity);
        }

        var result = await registry.UpsertPatientAsync(
            actor,
            new UpsertPatientCommand(
                existing.Key,
                FhirR4Mapper.ToDomain(patient),
                http.Request.Headers["Idempotency-Key"].FirstOrDefault(),
                Digest(patient, codec),
                ExpectedVersion: expected.Value),
            cancellationToken);
        var resource = FhirR4Mapper.ToSourcePatient(result.SourcePatient, actor.TenantId);
        return new FhirResult(
            resource,
            StatusCodes.Status200OK,
            etag: FhirRequest.Etag(result.SourcePatient.Version));
    }

    private static async Task<IResult> ReadPatient(
        string id,
        RegistryService registry,
        ActorContextFactory actors,
        CancellationToken cancellationToken)
    {
        var actor = actors.Create();
        Require(MpiScopes.CanReadPatient(actor));
        if (actor.SourceSystem is not null)
        {
            var source = await registry.GetSourcePatientByResourceIdAsync(actor, id, cancellationToken)
                ?? throw new RegistryNotFoundException("Patient", id);
            if (source.Key.SourceSystem != actor.SourceSystem.Value)
            {
                throw new RegistryAuthorisationException("The source system does not own this patient.");
            }

            return new FhirResult(
                FhirR4Mapper.ToSourcePatient(source, actor.TenantId),
                etag: FhirRequest.Etag(source.Version));
        }

        var enterpriseId = ParseEnterpriseId(id);
        var canonical = await registry.GetCanonicalPatientAsync(actor, enterpriseId, cancellationToken)
            ?? throw new RegistryNotFoundException("Patient", id);
        return new FhirResult(
            FhirR4Mapper.ToCanonicalPatient(canonical, actor.TenantId),
            etag: FhirRequest.Etag(canonical.Version));
    }

    private static async Task<IResult> SearchPatients(
        HttpContext http,
        RegistryService registry,
        ActorContextFactory actors,
        CancellationToken cancellationToken)
    {
        var actor = actors.Create();
        Require(MpiScopes.CanReadPatient(actor) && actor.SourceSystem is null);
        RejectReservedSearchParameters(http.Request.Query);
        var (identifierSystem, identifierValue) = ParseToken(
            http.Request.Query["identifier"].FirstOrDefault());
        var search = new CanonicalPatientSearch(
            identifierSystem,
            identifierValue,
            http.Request.Query["family"].FirstOrDefault(),
            ParseOptionalDate(http.Request.Query["birthdate"].FirstOrDefault()),
            ParseCount(http.Request.Query["_count"].FirstOrDefault(), 20, 100),
            http.Request.Query["_cursor"].FirstOrDefault());
        var page = await registry.SearchCanonicalPatientsAsync(actor, search, cancellationToken);
        var self = BuildRequestUri(http.Request);
        var next = page.NextCursor is null ? null : WithCursor(self, page.NextCursor);
        return new FhirResult(
            FhirR4Mapper.ToPatientSearchBundle(page.Items, actor.TenantId, self, next));
    }

    private static async Task<IResult> MatchPatient(
        HttpContext http,
        RegistryService registry,
        ActorContextFactory actors,
        FhirResourceCodec codec,
        IPatientProfileValidator validator,
        CancellationToken cancellationToken)
    {
        var actor = actors.Create();
        Require(actor.HasScope(MpiScopes.Match) || actor.HasScope(MpiScopes.Admin));
        var parameters = await FhirRequest.ReadAsync<Parameters>(
            http.Request,
            codec,
            cancellationToken);
        var patientParameters = parameters.Parameter
            .Where(static parameter => parameter.Name == "resource")
            .ToArray();
        if (patientParameters.Length != 1 ||
            patientParameters[0].Resource is not Patient patient)
        {
            throw new FormatException(
                "$match requires exactly one Parameters.parameter named 'resource' containing a Patient.");
        }

        var onlyCertain = ReadOptionalBoolean(parameters, "onlyCertainMatches") ?? false;
        var count = ReadOptionalInteger(parameters, "count") ?? 10;
        if (count is < 1 or > 50)
        {
            throw new FormatException("$match count must be between 1 and 50.");
        }

        var validation = await validator.ValidateMatchInputAsync(patient, cancellationToken);
        if (!validation.IsSuccessful)
        {
            return new FhirResult(validation.ToOperationOutcome(), StatusCodes.Status422UnprocessableEntity);
        }

        var response = await registry.MatchAsync(
            actor,
            new MatchRequest(FhirR4Mapper.ToDomain(patient), onlyCertain, count),
            cancellationToken);
        return new FhirResult(
            FhirR4Mapper.ToMatchBundle(response, actor.TenantId, BuildRequestUri(http.Request)));
    }

    private static async Task<IResult> ReadPerson(
        string id,
        RegistryService registry,
        ActorContextFactory actors,
        CancellationToken cancellationToken)
    {
        var actor = actors.Create();
        Require(MpiScopes.CanReview(actor));
        var person = await registry.GetPersonAsync(
            actor,
            ParseEnterpriseId(id),
            cancellationToken) ?? throw new RegistryNotFoundException("Person", id);
        return new FhirResult(
            FhirR4Mapper.ToPerson(person, actor.TenantId),
            etag: FhirRequest.Etag(person.Version));
    }

    private static async Task<IResult> SearchPersons(
        HttpContext http,
        RegistryService registry,
        ActorContextFactory actors,
        CancellationToken cancellationToken)
    {
        var actor = actors.Create();
        Require(MpiScopes.CanReview(actor));
        RejectReservedSearchParameters(http.Request.Query);
        var (_, value) = ParseToken(http.Request.Query["identifier"].FirstOrDefault());
        EnterpriseId? enterpriseId = string.IsNullOrWhiteSpace(value)
            ? null
            : ParseEnterpriseId(value);
        var page = await registry.SearchPersonsAsync(
            actor,
            new PersonSearch(
                enterpriseId,
                ParseCount(http.Request.Query["_count"].FirstOrDefault(), 20, 100),
                http.Request.Query["_cursor"].FirstOrDefault()),
            cancellationToken);
        var bundle = new Bundle
        {
            Type = Bundle.BundleType.Searchset,
            Total = page.Items.Count,
            Timestamp = DateTimeOffset.UtcNow
        };
        foreach (var person in page.Items)
        {
            var resource = FhirR4Mapper.ToPerson(person, actor.TenantId);
            bundle.Entry.Add(new Bundle.EntryComponent
            {
                FullUrl = $"Person/{resource.Id}",
                Resource = resource,
                Search = new Bundle.SearchComponent { Mode = Bundle.SearchEntryMode.Match }
            });
        }

        return new FhirResult(bundle);
    }

    private static async Task<IResult> SearchReviewCases(
        HttpContext http,
        RegistryService registry,
        ActorContextFactory actors,
        CancellationToken cancellationToken)
    {
        var actor = actors.Create();
        Require(MpiScopes.CanReview(actor));
        var statusText = http.Request.Query["status"].FirstOrDefault();
        var status = string.IsNullOrWhiteSpace(statusText)
            ? ReviewCaseStatus.Pending
            : Enum.TryParse<ReviewCaseStatus>(statusText, true, out var parsed)
                ? parsed
                : throw new FormatException("Review status is not recognised.");
        var kindText = http.Request.Query["kind"].FirstOrDefault();
        ReviewCaseKind? kind = string.IsNullOrWhiteSpace(kindText)
            ? null
            : Enum.TryParse<ReviewCaseKind>(kindText, true, out var parsedKind)
                ? parsedKind
                : throw new FormatException("Review kind is not recognised.");
        var page = await registry.SearchReviewCasesAsync(
            actor,
            new ReviewCaseSearch(
                Status: status,
                Kind: kind,
                Count: ParseCount(http.Request.Query["count"].FirstOrDefault(), 50, 100),
                Cursor: http.Request.Query["cursor"].FirstOrDefault()),
            cancellationToken);
        return Results.Ok(new { items = page.Items, nextCursor = page.NextCursor });
    }

    private static async Task<IResult> ReadReviewCase(
        Guid id,
        RegistryService registry,
        ActorContextFactory actors,
        CancellationToken cancellationToken)
    {
        var actor = actors.Create();
        Require(MpiScopes.CanReview(actor));
        var review = await registry.GetReviewCaseAsync(actor, id, cancellationToken)
            ?? throw new RegistryNotFoundException("ReviewCase", id.ToString());
        return Results.Ok(review);
    }

    private static async Task<IResult> ReadReviewCaseDetail(
        Guid id,
        RegistryService registry,
        ActorContextFactory actors,
        CancellationToken cancellationToken)
    {
        var actor = actors.Create();
        Require(MpiScopes.CanReview(actor));
        return Results.Ok(await registry.GetReviewCaseDetailAsync(
            actor,
            id,
            cancellationToken));
    }

    private static async Task<IResult> DecideReviewCase(
        Guid id,
        ReviewDecisionRequest request,
        RegistryService registry,
        ActorContextFactory actors,
        CancellationToken cancellationToken)
    {
        var actor = actors.Create();
        Require(MpiScopes.CanReview(actor));
        if (!Enum.TryParse<ReviewDecision>(request.Decision, true, out var decision))
        {
            throw new FormatException(
                "Decision must be 'link', 'reject', 'split', or 'supersede'.");
        }

        var review = await registry.DecideReviewCaseAsync(
            actor,
            new ReviewDecisionCommand(id, decision, request.Reason, request.ExpectedVersion),
            cancellationToken);
        return Results.Ok(review);
    }

    private static async Task<IResult> CreateDuplicateReviewCase(
        ManualDuplicateReviewRequest request,
        RegistryService registry,
        ActorContextFactory actors,
        CancellationToken cancellationToken)
    {
        var actor = actors.Create();
        Require(MpiScopes.CanReview(actor));
        var review = await registry.CreateDuplicateReviewCaseAsync(
            actor,
            new CreateDuplicateReviewCommand(
                new EnterpriseId(request.SubjectEnterpriseId),
                new EnterpriseId(request.CandidateEnterpriseId),
                request.Reason,
                request.SubjectVersion,
                request.CandidateVersion),
            cancellationToken);
        return Results.Created($"/api/v1/review-cases/{review.Id:D}", review);
    }

    private static async Task<IResult> CreateSplitReviewCase(
        SplitReviewRequest request,
        RegistryService registry,
        ActorContextFactory actors,
        CancellationToken cancellationToken)
    {
        var actor = actors.Create();
        Require(MpiScopes.CanReview(actor));
        var sources = request.SourcesToMove.Select(source =>
            new SourceRecordKey(
                new SourceSystemId(source.SourceSystem),
                source.LocalId)).ToArray();
        var review = await registry.CreateSplitReviewCaseAsync(
            actor,
            new CreateSplitReviewCommand(
                new EnterpriseId(request.EnterpriseId),
                sources,
                request.Reason,
                request.ExpectedVersion),
            cancellationToken);
        return Results.Created($"/api/v1/review-cases/{review.Id:D}", review);
    }

    private static async Task<IResult> GetOperationalSummary(
        RegistryService registry,
        ActorContextFactory actors,
        CancellationToken cancellationToken)
    {
        var actor = actors.Create();
        Require(MpiScopes.CanOperate(actor));
        return Results.Ok(await registry.GetOperationalSummaryAsync(
            actor,
            cancellationToken));
    }

    private static async Task<IResult> GetPatientIdentity(
        Guid id,
        RegistryService registry,
        ActorContextFactory actors,
        CancellationToken cancellationToken)
    {
        var actor = actors.Create();
        Require(MpiScopes.CanReview(actor));
        return Results.Ok(await registry.GetPatientIdentityViewAsync(
            actor,
            new EnterpriseId(id),
            cancellationToken));
    }

    private static async Task<IResult> FindDuplicates(
        Guid id,
        HttpContext http,
        RegistryService registry,
        ActorContextFactory actors,
        CancellationToken cancellationToken)
    {
        var actor = actors.Create();
        Require(MpiScopes.CanReview(actor));
        var count = ParseCount(http.Request.Query["count"].FirstOrDefault(), 10, 50);
        return Results.Ok(await registry.FindDuplicateCandidatesAsync(
            actor,
            new EnterpriseId(id),
            count,
            cancellationToken));
    }

    private static async Task<IResult> SearchAuditEvents(
        HttpContext http,
        RegistryService registry,
        ActorContextFactory actors,
        CancellationToken cancellationToken)
    {
        var actor = actors.Create();
        Require(MpiScopes.CanAudit(actor));
        EnterpriseId? enterpriseId = Guid.TryParse(
            http.Request.Query["enterpriseId"].FirstOrDefault(),
            out var parsedEnterpriseId)
            ? new EnterpriseId(parsedEnterpriseId)
            : null;
        var search = new AuditRecordSearch(
            http.Request.Query["action"].FirstOrDefault(),
            http.Request.Query["actor"].FirstOrDefault(),
            http.Request.Query["outcome"].FirstOrDefault(),
            enterpriseId,
            ParseOptionalInstant(http.Request.Query["from"].FirstOrDefault()),
            ParseOptionalInstant(http.Request.Query["to"].FirstOrDefault()),
            ParseCount(http.Request.Query["count"].FirstOrDefault(), 50, 100),
            http.Request.Query["cursor"].FirstOrDefault());
        var page = await registry.SearchAuditRecordsAsync(actor, search, cancellationToken);
        return Results.Ok(new { items = page.Items, nextCursor = page.NextCursor });
    }

    private static async Task<IResult> GetTenantSettings(
        RegistryService registry,
        ActorContextFactory actors,
        CancellationToken cancellationToken)
    {
        var actor = actors.Create();
        Require(MpiScopes.CanReadConfiguration(actor));
        return Results.Ok(await registry.GetTenantSettingsAsync(actor, cancellationToken));
    }

    private static async Task<IResult> UpdateTenantSettings(
        TenantSettingsRequest request,
        RegistryService registry,
        ActorContextFactory actors,
        CancellationToken cancellationToken)
    {
        var actor = actors.Create();
        Require(MpiScopes.CanWriteConfiguration(actor));
        var settings = await registry.UpdateTenantSettingsAsync(
            actor,
            new UpdateTenantSettingsCommand(
                request.MatchingProfileVersion,
                request.PossibleThreshold,
                request.ProbableThreshold,
                request.RequiredLinkApprovals,
                request.Sources.Select(source => new SourceSystemSettings(
                    new SourceSystemId(source.SourceSystem),
                    source.Trust,
                    source.IsAuthoritative)).ToArray(),
                request.Reason,
                request.ExpectedVersion),
            cancellationToken);
        return Results.Ok(settings);
    }

    private static async Task<IResult> EvaluateGroundTruth(
        GroundTruthEvaluationRequest request,
        MatchingAssuranceService assurance,
        ActorContextFactory actors,
        CancellationToken cancellationToken)
    {
        var actor = actors.Create();
        Require(actor.HasScope(MpiScopes.Admin));
        var report = await assurance.EvaluateAsync(
            actor,
            new EvaluateGroundTruthCommand(
                request.DatasetId,
                request.Pairs.Select(ToGroundTruthPair).ToArray(),
                request.Thresholds,
                request.MaximumErrorExamples),
            cancellationToken);
        return Results.Ok(report);
    }

    private static async Task<IResult> CalibrateFellegiSunter(
        FellegiSunterCalibrationRequest request,
        MatchingAssuranceService assurance,
        ActorContextFactory actors,
        CancellationToken cancellationToken)
    {
        var actor = actors.Create();
        Require(actor.HasScope(MpiScopes.Admin));
        var report = await assurance.CalibrateAsync(
            actor,
            new CalibrateFellegiSunterCommand(
                request.DatasetId,
                request.ModelVersion,
                request.Pairs.Select(ToGroundTruthPair).ToArray(),
                request.PriorMatchProbability,
                request.Smoothing,
                request.ValidationFraction,
                request.TargetPrecision),
            cancellationToken);
        return Results.Ok(report);
    }

    private static async Task<IResult> StartReindex(
        StartReindexRequest request,
        RegistryMaintenanceService maintenance,
        ActorContextFactory actors,
        CancellationToken cancellationToken)
    {
        var actor = actors.Create();
        Require(actor.HasScope(MpiScopes.Admin));
        var job = await maintenance.StartReindexAsync(
            actor,
            new StartReindexCommand(request.Reason, request.BatchSize),
            cancellationToken);
        return Results.Accepted($"/api/v1/maintenance/jobs/{job.Id:D}", job);
    }

    private static async Task<IResult> StartPopulationReconciliation(
        StartPopulationReconciliationRequest request,
        RegistryMaintenanceService maintenance,
        ActorContextFactory actors,
        CancellationToken cancellationToken)
    {
        var actor = actors.Create();
        Require(actor.HasScope(MpiScopes.Admin));
        SourceSystemId? source = string.IsNullOrWhiteSpace(request.ExternalSourceSystem)
            ? null
            : new SourceSystemId(request.ExternalSourceSystem);
        var job = await maintenance.StartPopulationReconciliationAsync(
            actor,
            new StartPopulationReconciliationCommand(
                request.Reason,
                request.BatchSize,
                source,
                request.ChangedSince),
            cancellationToken);
        return Results.Accepted($"/api/v1/maintenance/jobs/{job.Id:D}", job);
    }

    private static async Task<IResult> SearchMaintenanceJobs(
        HttpContext http,
        RegistryMaintenanceService maintenance,
        ActorContextFactory actors,
        CancellationToken cancellationToken)
    {
        var actor = actors.Create();
        Require(MpiScopes.CanOperate(actor));
        var kindText = http.Request.Query["kind"].FirstOrDefault();
        var statusText = http.Request.Query["status"].FirstOrDefault();
        RegistryMaintenanceJobKind? kind = string.IsNullOrWhiteSpace(kindText)
            ? null
            : Enum.TryParse<RegistryMaintenanceJobKind>(kindText, true, out var parsedKind)
                ? parsedKind
                : throw new FormatException("Unknown maintenance job kind.");
        RegistryMaintenanceJobStatus? status = string.IsNullOrWhiteSpace(statusText)
            ? null
            : Enum.TryParse<RegistryMaintenanceJobStatus>(statusText, true, out var parsedStatus)
                ? parsedStatus
                : throw new FormatException("Unknown maintenance job status.");
        var sourceText = http.Request.Query["sourceSystem"].FirstOrDefault();
        var page = await maintenance.SearchJobsAsync(
            actor,
            new MaintenanceJobSearch(
                kind,
                status,
                string.IsNullOrWhiteSpace(sourceText)
                    ? null
                    : new SourceSystemId(sourceText),
                http.Request.Query["scheduleKey"].FirstOrDefault(),
                ParseCount(http.Request.Query["count"].FirstOrDefault(), 50, 100),
                http.Request.Query["cursor"].FirstOrDefault()),
            cancellationToken);
        return Results.Ok(new { items = page.Items, nextCursor = page.NextCursor });
    }

    private static async Task<IResult> GetMaintenanceJob(
        Guid id,
        RegistryMaintenanceService maintenance,
        ActorContextFactory actors,
        CancellationToken cancellationToken)
    {
        var actor = actors.Create();
        Require(MpiScopes.CanOperate(actor));
        var job = await maintenance.GetJobAsync(actor, id, cancellationToken) ??
                  throw new RegistryNotFoundException("MaintenanceJob", id.ToString("D"));
        return Results.Ok(job);
    }

    private static async Task<IResult> CancelMaintenanceJob(
        Guid id,
        RegistryMaintenanceService maintenance,
        ActorContextFactory actors,
        CancellationToken cancellationToken)
    {
        var actor = actors.Create();
        Require(actor.HasScope(MpiScopes.Admin));
        return Results.Ok(await maintenance.CancelJobAsync(actor, id, cancellationToken));
    }

    private static CapabilityStatement.ResourceComponent ResourceCapability(
        ResourceType resourceType,
        IReadOnlyList<CapabilityStatement.TypeRestfulInteraction> interactions,
        IReadOnlyList<string> searchParameters) =>
        new()
        {
            Type = resourceType.ToString(),
            Interaction = interactions.Select(static code =>
                new CapabilityStatement.ResourceInteractionComponent { Code = code }).ToList(),
            SearchParam = searchParameters.Select(name =>
                new CapabilityStatement.SearchParamComponent
                {
                    Name = name,
                    Type = SearchParamType.String
                }).ToList()
        };

    private static void RejectIdentityOverrides(Patient patient)
    {
        if (patient.Extension.Any(extension => string.Equals(
                extension.Url,
                FhirR4Constants.SourceSystemExtension,
                StringComparison.Ordinal)) ||
            patient.Meta?.Security.Any(coding => string.Equals(
                coding.System,
                FhirR4Constants.TenantSecuritySystem,
                StringComparison.Ordinal)) == true)
        {
            throw new RegistryAuthorisationException(
                "Tenant and source-system identity cannot be supplied in a FHIR resource.");
        }
    }

    private static void RejectReservedSearchParameters(IQueryCollection query)
    {
        if (query.ContainsKey("_security") || query.ContainsKey("_tag"))
        {
            throw new RegistryAuthorisationException(
                "_security and _tag are server-managed search parameters.");
        }
    }

    private static bool? ReadOptionalBoolean(Parameters parameters, string name)
    {
        var values = parameters.Parameter.Where(parameter => parameter.Name == name).ToArray();
        if (values.Length > 1 || values.FirstOrDefault()?.Value is not FhirBoolean value)
        {
            if (values.Length == 0)
            {
                return null;
            }

            throw new FormatException($"$match parameter '{name}' must occur once and be boolean.");
        }

        return value.Value;
    }

    private static int? ReadOptionalInteger(Parameters parameters, string name)
    {
        var values = parameters.Parameter.Where(parameter => parameter.Name == name).ToArray();
        if (values.Length > 1 || values.FirstOrDefault()?.Value is not Integer value)
        {
            if (values.Length == 0)
            {
                return null;
            }

            throw new FormatException($"$match parameter '{name}' must occur once and be integer.");
        }

        return value.Value;
    }

    private static (string? System, string? Value) ParseToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (null, null);
        }

        var separator = value.IndexOf('|', StringComparison.Ordinal);
        return separator < 0
            ? (null, value)
            : (value[..separator], value[(separator + 1)..]);
    }

    private static DateOnly? ParseOptionalDate(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : DateOnly.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date)
                ? date
                : throw new FormatException("birthdate must use yyyy-MM-dd.");

    private static DateTimeOffset? ParseOptionalInstant(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var instant)
                ? instant
                : throw new FormatException("Timestamps must use ISO 8601.");

    private static int ParseCount(string? value, int defaultValue, int maximum) =>
        string.IsNullOrWhiteSpace(value)
            ? defaultValue
            : int.TryParse(value, CultureInfo.InvariantCulture, out var count) &&
              count is >= 1 &&
              count <= maximum
                ? count
                : throw new FormatException($"Count must be between 1 and {maximum}.");

    private static EnterpriseId ParseEnterpriseId(string value) =>
        Guid.TryParse(value, out var id)
            ? new EnterpriseId(id)
            : throw new FormatException("The enterprise identifier is not a UUID.");

    private static GroundTruthPair ToGroundTruthPair(GroundTruthPairRequest request) =>
        new(
            new SourceRecordKey(
                new SourceSystemId(request.Left.SourceSystem),
                request.Left.LocalId),
            new SourceRecordKey(
                new SourceSystemId(request.Right.SourceSystem),
                request.Right.LocalId),
            request.IsMatch);

    private static Uri BuildRequestUri(HttpRequest request) =>
        new($"{request.Scheme}://{request.Host}{request.PathBase}{request.Path}{request.QueryString}");

    private static Uri WithCursor(Uri uri, string cursor)
    {
        var separator = string.IsNullOrEmpty(uri.Query) ? "?" : "&";
        return new Uri($"{uri}{separator}_cursor={Uri.EscapeDataString(cursor)}");
    }

    private static string Digest(Patient patient, FhirResourceCodec codec)
    {
        var json = codec.Serialise(patient, FhirWireFormat.Json);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private static void Require(bool condition)
    {
        if (!condition)
        {
            throw new RegistryAuthorisationException("The required scope is missing.");
        }
    }
}

public sealed record ReviewDecisionRequest(
    string Decision,
    string Reason,
    long ExpectedVersion);

public sealed record ManualDuplicateReviewRequest(
    Guid SubjectEnterpriseId,
    Guid CandidateEnterpriseId,
    string Reason,
    long SubjectVersion,
    long CandidateVersion);

public sealed record SourceRecordRequest(
    string SourceSystem,
    string LocalId);

public sealed record SplitReviewRequest(
    Guid EnterpriseId,
    IReadOnlyList<SourceRecordRequest> SourcesToMove,
    string Reason,
    long ExpectedVersion);

public sealed record SourceSettingsRequest(
    string SourceSystem,
    int Trust,
    bool IsAuthoritative);

public sealed record TenantSettingsRequest(
    string MatchingProfileVersion,
    double PossibleThreshold,
    double ProbableThreshold,
    int RequiredLinkApprovals,
    IReadOnlyList<SourceSettingsRequest> Sources,
    string Reason,
    long ExpectedVersion);

public sealed record StartReindexRequest(
    string Reason,
    int BatchSize = 25);

public sealed record StartPopulationReconciliationRequest(
    string Reason,
    int BatchSize = 25,
    string? ExternalSourceSystem = null,
    DateTimeOffset? ChangedSince = null);

public sealed record GroundTruthPairRequest(
    SourceRecordRequest Left,
    SourceRecordRequest Right,
    bool IsMatch);

public sealed record GroundTruthEvaluationRequest(
    string DatasetId,
    IReadOnlyList<GroundTruthPairRequest> Pairs,
    IReadOnlyList<double>? Thresholds = null,
    int MaximumErrorExamples = 25);

public sealed record FellegiSunterCalibrationRequest(
    string DatasetId,
    string ModelVersion,
    IReadOnlyList<GroundTruthPairRequest> Pairs,
    double PriorMatchProbability,
    double Smoothing = 1,
    double ValidationFraction = 0.2,
    double TargetPrecision = 0.99);
