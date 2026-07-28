using UnifyEmpi.Application.Configuration;

namespace UnifyEmpi.Api;

public sealed class AuthenticationOptions
{
    public const string SectionName = "Authentication";

    public bool Enabled { get; init; } = true;

    public string Authority { get; init; } = string.Empty;

    public string Audience { get; init; } = "unifyempi";

    public bool RequireHttpsMetadata { get; init; } = true;

    public string DevelopmentTenant { get; init; } = "demo";

    public string DevelopmentSourceSystem { get; init; } = "demo-source";
}

public sealed class RegistryProviderOptions
{
    public const string SectionName = "RegistryProvider";

    public string Type { get; init; } = string.Empty;
}

public sealed class TenantLimitOptions
{
    public const string SectionName = "TenantLimits";

    public int ConcurrentRequests { get; init; } = 64;

    public int QueueLimit { get; init; } = 128;
}

public sealed class TenantRegistryOptions
{
    public const string SectionName = "Tenants";

    public List<TenantDefinition> Items { get; init; } = [];
}

public sealed class TenantDefinition
{
    public string TenantId { get; init; } = string.Empty;

    public string MatchingProfileVersion { get; init; } = "uk-default-v1";

    public double PossibleThreshold { get; init; } = 0.62;

    public double ProbableThreshold { get; init; } = 0.82;

    public MatchingRuleOptions MatchingRules { get; init; } = new();

    public Dictionary<string, int> SourceTrust { get; init; } =
        new(StringComparer.Ordinal);

    public List<string> AuthoritativeSources { get; init; } = [];

    public List<BlockingSecretDefinition> BlockingSecrets { get; init; } = [];
}

public sealed class BlockingSecretDefinition
{
    public string Version { get; init; } = string.Empty;

    public string SecretBase64 { get; init; } = string.Empty;

    public bool Active { get; init; }
}

public sealed class FhirValidationOptions
{
    public const string SectionName = "FhirValidation";

    public string? UkCorePackageDirectory { get; init; }

    public int PoolSize { get; init; } = 4;
}

public sealed class RegistryMaintenanceOptions
{
    public const string SectionName = "Maintenance";

    public bool WorkerEnabled { get; init; } = true;

    public int PollIntervalSeconds { get; init; } = 5;

    public int LeaseSeconds { get; init; } = 60;

    public List<ReconciliationScheduleDefinition> ReconciliationSchedules { get; init; } = [];

    public List<ExternalFhirSourceDefinition> FhirSources { get; init; } = [];
}

public sealed class ReconciliationScheduleDefinition
{
    public string Key { get; init; } = string.Empty;

    public string TenantId { get; init; } = string.Empty;

    public string? SourceSystem { get; init; }

    public int IntervalMinutes { get; init; } = 1440;

    public int BatchSize { get; init; } = 25;

    public bool RunOnStartup { get; init; } = true;
}

public sealed class ExternalFhirSourceDefinition
{
    public string TenantId { get; init; } = string.Empty;

    public string SourceSystem { get; init; } = string.Empty;

    public string BaseUrl { get; init; } = string.Empty;

    public int RequestTimeoutSeconds { get; init; } = 60;

    public bool AllowInsecureHttp { get; init; }

    public string? LocalIdentifierSystem { get; init; }

    public Dictionary<string, string> PatientSearchParameters { get; init; } =
        new(StringComparer.Ordinal);

    public FhirSourceAuthenticationDefinition Authentication { get; init; } = new();
}

public sealed class FhirSourceAuthenticationDefinition
{
    public string Type { get; init; } = "None";

    public string? BearerToken { get; init; }

    public string? TokenEndpoint { get; init; }

    public string? ClientId { get; init; }

    public string? ClientSecret { get; init; }

    public string? Scope { get; init; }
}
