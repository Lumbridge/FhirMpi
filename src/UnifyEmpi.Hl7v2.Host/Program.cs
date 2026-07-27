using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using UnifyEmpi.Application;
using UnifyEmpi.Application.Configuration;
using UnifyEmpi.Domain;
using UnifyEmpi.Fhir.R4;
using UnifyEmpi.Hl7v2;
using UnifyEmpi.Hl7v2.Host;
using UnifyEmpi.Storage.Abstractions;
using UnifyEmpi.Storage.Gcp;
using UnifyEmpi.Storage.InMemory;

var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);
builder.Services.Configure<MllpHostOptions>(
    builder.Configuration.GetSection(MllpHostOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<FhirResourceCodec>();

var provider = builder.Configuration[$"{MllpRegistryProviderOptions.SectionName}:Type"]?.Trim();
switch (provider)
{
    case "InMemory":
    case "inmemory":
        builder.Services.AddSingleton<IIdentityRegistryStore, InMemoryIdentityRegistryStore>();
        break;
    case "GcpHealthcare":
    case "gcphealthcare":
        var gcpOptions = builder.Configuration
            .GetSection(GcpFhirStoreOptions.SectionName)
            .Get<GcpFhirStoreOptions>() ?? new GcpFhirStoreOptions();
        gcpOptions.Validate();
        builder.Services.AddSingleton(gcpOptions);
        builder.Services.AddSingleton<IGcpFhirClient>(services =>
            HealthcareApiFhirClient.Create(
                services.GetRequiredService<GcpFhirStoreOptions>(),
                services.GetRequiredService<FhirResourceCodec>()));
        builder.Services.AddSingleton<IIdentityRegistryStore, GcpIdentityRegistryStore>();
        break;
    default:
        throw new InvalidOperationException(
            "Exactly one registry provider must be configured as 'InMemory' or 'GcpHealthcare'.");
}

var tenantOptions = builder.Configuration
    .GetSection(MllpTenantRegistryOptions.SectionName)
    .Get<MllpTenantRegistryOptions>() ?? new MllpTenantRegistryOptions();
var tenantConfigurations = BuildTenantConfigurations(tenantOptions);
if (tenantConfigurations.Count == 0)
{
    if (!builder.Environment.IsDevelopment())
    {
        throw new InvalidOperationException(
            "At least one tenant must be configured outside Development.");
    }

    var listeners = builder.Configuration
        .GetSection(MllpHostOptions.SectionName)
        .Get<MllpHostOptions>()?.Listeners ?? [];
    foreach (var listener in listeners)
    {
        var development = DefaultTenantConfigurationFactory.CreateDevelopment(
            listener.TenantId,
            listener.SourceSystem);
        tenantConfigurations.TryAdd(development.TenantId, development);
    }
}

var configuredListeners = builder.Configuration
    .GetSection(MllpHostOptions.SectionName)
    .Get<MllpHostOptions>()?.Listeners ?? [];
foreach (var listener in configuredListeners)
{
    var tenantId = new TenantId(listener.TenantId);
    if (!tenantConfigurations.ContainsKey(tenantId))
    {
        throw new InvalidOperationException(
            $"MLLP listener '{listener.Name}' references unconfigured tenant '{tenantId}'.");
    }
}

builder.Services.AddSingleton<IReadOnlyDictionary<TenantId, TenantMatchingConfiguration>>(
    tenantConfigurations);
builder.Services.AddSingleton<ITenantConfigurationProvider, StoredTenantConfigurationProvider>();
builder.Services.AddSingleton<RegistryService>();
builder.Services.AddSingleton<Hl7v2AdtParser>();
builder.Services.AddSingleton<Hl7v2IngestionProcessor>();
builder.Services.AddHostedService<RegistryStartupValidationService>();
builder.Services.AddSingleton(services =>
{
    var options = services
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<MllpHostOptions>>()
        .Value;
    return new MllpConnectionProcessor(
        services.GetRequiredService<Hl7v2IngestionProcessor>(),
        options.MaximumMessageBytes);
});
builder.Services.AddHostedService<MllpListenerWorker>();
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("UnifyEmpi.Hl7v2"))
    .WithTracing(static tracing => tracing
        .AddSource("UnifyEmpi.Registry")
        .AddOtlpExporter())
    .WithMetrics(static metrics => metrics
        .AddMeter("UnifyEmpi.Registry", "UnifyEmpi.Hl7v2")
        .AddOtlpExporter());

await builder.Build().RunAsync();

static Dictionary<TenantId, TenantMatchingConfiguration> BuildTenantConfigurations(
    MllpTenantRegistryOptions options)
{
    var result = new Dictionary<TenantId, TenantMatchingConfiguration>();
    foreach (var item in options.Items)
    {
        var tenant = new TenantId(item.TenantId);
        var secrets = item.BlockingSecrets.Select(secret =>
        {
            if (string.IsNullOrWhiteSpace(secret.Version))
            {
                throw new InvalidOperationException("Every blocking-key secret needs a version.");
            }

            var bytes = Convert.FromBase64String(secret.SecretBase64);
            if (bytes.Length < 32)
            {
                throw new InvalidOperationException(
                    $"Tenant '{tenant}' blocking-key secrets must contain at least 256 bits.");
            }

            return new BlockingKeySecret(secret.Version, bytes, secret.Active);
        }).ToArray();
        if (secrets.Count(static secret => secret.IsActive) != 1)
        {
            throw new InvalidOperationException(
                $"Tenant '{tenant}' must have exactly one active blocking-key secret.");
        }

        var profile = MatchingProfile.UkDefault with
        {
            Version = item.MatchingProfileVersion
        };
        var trust = item.SourceTrust.ToDictionary(
            static pair => new SourceSystemId(pair.Key),
            static pair => pair.Value);
        var authoritativeSources = item.AuthoritativeSources
            .Select(static source => new SourceSystemId(source))
            .ToHashSet();
        if (authoritativeSources.Any(source => !trust.ContainsKey(source)))
        {
            throw new InvalidOperationException(
                $"Tenant '{tenant}' has an authoritative source without a SourceTrust entry.");
        }

        result.Add(
            tenant,
            new TenantMatchingConfiguration(
                tenant,
                profile,
                secrets,
                trust,
                authoritativeSources));
    }

    return result;
}
