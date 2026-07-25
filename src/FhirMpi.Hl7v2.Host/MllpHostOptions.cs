namespace FhirMpi.Hl7v2.Host;

public sealed class MllpHostOptions
{
    public const string SectionName = "Mllp";

    public int MaximumMessageBytes { get; init; } = 2 * 1024 * 1024;

    public int IdleTimeoutSeconds { get; init; } = 60;

    public int MaximumConcurrentConnectionsPerListener { get; init; } = 100;

    public List<MllpListenerOptions> Listeners { get; init; } = [];
}

public sealed class MllpListenerOptions
{
    public string Name { get; init; } = string.Empty;

    public string Address { get; init; } = "0.0.0.0";

    public int Port { get; init; } = 2575;

    public string TenantId { get; init; } = string.Empty;

    public string SourceSystem { get; init; } = string.Empty;

    public string ActorId { get; init; } = "mllp";

    public bool AllowPlaintext { get; init; }

    public string? CertificatePath { get; init; }

    public string? CertificatePassword { get; init; }

    public List<string> AllowedClientCertificateThumbprints { get; init; } = [];
}

public sealed class MllpRegistryProviderOptions
{
    public const string SectionName = "RegistryProvider";

    public string Type { get; init; } = string.Empty;
}

public sealed class MllpTenantRegistryOptions
{
    public const string SectionName = "Tenants";

    public List<MllpTenantDefinition> Items { get; init; } = [];
}

public sealed class MllpTenantDefinition
{
    public string TenantId { get; init; } = string.Empty;

    public string MatchingProfileVersion { get; init; } = "uk-default-v1";

    public Dictionary<string, int> SourceTrust { get; init; } =
        new(StringComparer.Ordinal);

    public List<string> AuthoritativeSources { get; init; } = [];

    public List<MllpBlockingSecretDefinition> BlockingSecrets { get; init; } = [];
}

public sealed class MllpBlockingSecretDefinition
{
    public string Version { get; init; } = string.Empty;

    public string SecretBase64 { get; init; } = string.Empty;

    public bool Active { get; init; }
}
