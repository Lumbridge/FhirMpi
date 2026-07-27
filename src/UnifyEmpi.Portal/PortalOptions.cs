using UnifyEmpi.Application.Configuration;

namespace UnifyEmpi.Portal;

public sealed class PortalAuthenticationOptions
{
    public const string SectionName = "PortalAuthentication";

    public bool Enabled { get; init; } = true;

    public string Authority { get; init; } = string.Empty;

    public string ClientId { get; init; } = string.Empty;

    public string ClientSecret { get; init; } = string.Empty;

    public bool RequireHttpsMetadata { get; init; } = true;

    public string TenantClaimType { get; init; } = "tenant_id";

    public string NameClaimType { get; init; } = "name";

    public IReadOnlyList<string> Scopes { get; init; } =
    [
        "openid",
        "profile",
        "mpi.review",
        "mpi.audit",
        "mpi.operations",
        "mpi.patient.write",
        "mpi.config.read",
        "mpi.config.write"
    ];

    public string DevelopmentTenant { get; init; } = "demo";
}

public sealed class PortalRegistryProviderOptions
{
    public const string SectionName = "RegistryProvider";

    public string Type { get; init; } = string.Empty;
}

public sealed class PortalTenantRegistryOptions
{
    public const string SectionName = "Tenants";

    public List<PortalTenantDefinition> Items { get; init; } = [];
}

public sealed class PortalTenantDefinition
{
    public string TenantId { get; init; } = string.Empty;

    public string MatchingProfileVersion { get; init; } = "uk-default-v1";

    public double PossibleThreshold { get; init; } = 0.62;

    public double ProbableThreshold { get; init; } = 0.82;

    public MatchingRuleOptions MatchingRules { get; init; } = new();

    public int RequiredLinkApprovals { get; init; } = 2;

    public Dictionary<string, int> SourceTrust { get; init; } =
        new(StringComparer.Ordinal);

    public List<string> AuthoritativeSources { get; init; } = [];

    public List<PortalBlockingSecretDefinition> BlockingSecrets { get; init; } = [];
}

public sealed class PortalBlockingSecretDefinition
{
    public string Version { get; init; } = string.Empty;

    public string SecretBase64 { get; init; } = string.Empty;

    public bool Active { get; init; }
}

public sealed class PortalOptions
{
    public const string SectionName = "Portal";

    public int OverviewLoadTimeoutSeconds { get; init; } = 20;

    public bool SeedSyntheticData { get; init; }

    public bool PublicDemo { get; init; }

    public int CircuitRetentionMinutes { get; init; } = 3;

    public string DataProtectionKeyPath { get; init; } = string.Empty;

    public string ManagedSourceSystem { get; init; } = "portal";
}
