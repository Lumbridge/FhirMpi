namespace UnifyEmpi.Fhir.R4;

public static class FhirR4Constants
{
    public const string FhirVersion = "4.0.1";
    public const string BasePath = "/fhir/R4";
    public const string UkCorePatientProfile =
        "https://fhir.hl7.org.uk/StructureDefinition/UKCore-Patient";
    public const string TenantSecuritySystem =
        "https://unifyempi.dev/CodeSystem/tenant";
    public const string SourceSystemExtension =
        "https://unifyempi.dev/StructureDefinition/source-system";
    public const string IdentifierVerifiedExtension =
        "https://unifyempi.dev/StructureDefinition/identifier-verified";
    public const string IdentifierAuthoritativeExtension =
        "https://unifyempi.dev/StructureDefinition/identifier-authoritative";
    public const string MatchGradeExtension =
        "http://hl7.org/fhir/StructureDefinition/match-grade";
    public const string MatchEvidenceExtension =
        "https://unifyempi.dev/StructureDefinition/match-evidence";
    public const string EnterpriseIdentifierSystem =
        "https://unifyempi.dev/Id/enterprise-id";
}
