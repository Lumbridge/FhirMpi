namespace OpenMpi.Fhir.R4;

public static class FhirR4Constants
{
    public const string FhirVersion = "4.0.1";
    public const string BasePath = "/fhir/R4";
    public const string UkCorePatientProfile =
        "https://fhir.hl7.org.uk/StructureDefinition/UKCore-Patient";
    public const string TenantSecuritySystem =
        "https://openmpi.dev/CodeSystem/tenant";
    public const string SourceSystemExtension =
        "https://openmpi.dev/StructureDefinition/source-system";
    public const string IdentifierVerifiedExtension =
        "https://openmpi.dev/StructureDefinition/identifier-verified";
    public const string IdentifierAuthoritativeExtension =
        "https://openmpi.dev/StructureDefinition/identifier-authoritative";
    public const string MatchGradeExtension =
        "http://hl7.org/fhir/StructureDefinition/match-grade";
    public const string MatchEvidenceExtension =
        "https://openmpi.dev/StructureDefinition/match-evidence";
    public const string EnterpriseIdentifierSystem =
        "https://openmpi.dev/Id/enterprise-id";
}
