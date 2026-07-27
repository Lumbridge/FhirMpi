using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using UnifyEmpi.Api;
using UnifyEmpi.Application;
using UnifyEmpi.Application.Configuration;
using UnifyEmpi.Fhir.R4;
using UnifyEmpi.Storage.Abstractions;
using UnifyEmpi.Storage.Gcp;
using UnifyEmpi.Storage.InMemory;
using AppAuthenticationOptions = UnifyEmpi.Api.AuthenticationOptions;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<AppAuthenticationOptions>(
    builder.Configuration.GetSection(AppAuthenticationOptions.SectionName));
builder.Services.Configure<RegistryProviderOptions>(
    builder.Configuration.GetSection(RegistryProviderOptions.SectionName));
builder.Services.Configure<TenantLimitOptions>(
    builder.Configuration.GetSection(TenantLimitOptions.SectionName));
builder.Services.Configure<TenantRegistryOptions>(
    builder.Configuration.GetSection(TenantRegistryOptions.SectionName));
builder.Services.Configure<FhirValidationOptions>(
    builder.Configuration.GetSection(FhirValidationOptions.SectionName));

var authentication = builder.Configuration
    .GetSection(AppAuthenticationOptions.SectionName)
    .Get<AppAuthenticationOptions>() ?? new AppAuthenticationOptions();
if (authentication.Enabled)
{
    if (string.IsNullOrWhiteSpace(authentication.Authority))
    {
        throw new InvalidOperationException(
            "Authentication:Authority is required when JWT authentication is enabled.");
    }

    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = authentication.Authority;
            options.Audience = authentication.Audience;
            options.RequireHttpsMetadata = authentication.RequireHttpsMetadata;
            options.MapInboundClaims = false;
        });
}
else
{
    builder.Services
        .AddAuthentication(DevelopmentAuthenticationHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(
            DevelopmentAuthenticationHandler.SchemeName,
            static _ => { });
}

builder.Services.AddAuthorization();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<ActorContextFactory>();
builder.Services.AddSingleton<FhirResourceCodec>();
var validation = builder.Configuration
    .GetSection(FhirValidationOptions.SectionName)
    .Get<FhirValidationOptions>() ?? new FhirValidationOptions();
if (!string.IsNullOrWhiteSpace(validation.UkCorePackageDirectory))
{
    builder.Services.AddSingleton<IPatientProfileValidator>(
        FirelyPatientValidatorPool.Create(
            validation.UkCorePackageDirectory,
            validation.PoolSize));
}
else if (!authentication.Enabled)
{
    builder.Services.AddSingleton<IPatientProfileValidator, UkCorePatientValidator>();
}
else
{
    throw new InvalidOperationException(
        "FhirValidation:UkCorePackageDirectory is required outside development mode.");
}
builder.Services.AddSingleton(TimeProvider.System);

var provider = builder.Configuration[$"{RegistryProviderOptions.SectionName}:Type"]?.Trim();
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

var configuredTenants = builder.Configuration
    .GetSection(TenantRegistryOptions.SectionName)
    .Get<TenantRegistryOptions>() ?? new TenantRegistryOptions();
var tenantConfigurations = BuildTenantConfigurations(configuredTenants);
if (tenantConfigurations.Count == 0)
{
    if (authentication.Enabled)
    {
        throw new InvalidOperationException(
            "At least one tenant must be configured when external authentication is enabled.");
    }

    var development = DefaultTenantConfigurationFactory.CreateDevelopment(
        authentication.DevelopmentTenant,
        authentication.DevelopmentSourceSystem);
    tenantConfigurations.Add(development.TenantId, development);
}

builder.Services.AddSingleton<IReadOnlyDictionary<UnifyEmpi.Domain.TenantId, UnifyEmpi.Domain.TenantMatchingConfiguration>>(
    tenantConfigurations);
builder.Services.AddSingleton<ITenantConfigurationProvider, StoredTenantConfigurationProvider>();
builder.Services.AddSingleton<RegistryService>();
builder.Services.AddHostedService<RegistryStartupValidationService>();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("tenant", httpContext =>
    {
        var limits = httpContext.RequestServices.GetRequiredService<IOptions<TenantLimitOptions>>().Value;
        var tenant = httpContext.User.FindFirst("tenant_id")?.Value ?? "unauthenticated";
        return RateLimitPartition.GetConcurrencyLimiter(
            tenant,
            _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = Math.Max(1, limits.ConcurrentRequests),
                QueueLimit = Math.Max(0, limits.QueueLimit),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            });
    });
});

builder.Services.AddHealthChecks()
    .AddCheck<RegistryHealthCheck>("registry", tags: ["ready"]);
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("UnifyEmpi.Api"))
    .WithTracing(tracing => tracing
        .AddSource("UnifyEmpi.Registry")
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddMeter("UnifyEmpi.Registry")
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter());

var app = builder.Build();
app.UseMiddleware<FhirExceptionMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapUnifyEmpiEndpoints();
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = static _ => false
});
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = static check => check.Tags.Contains("ready")
});
app.Run();

static Dictionary<UnifyEmpi.Domain.TenantId, UnifyEmpi.Domain.TenantMatchingConfiguration>
    BuildTenantConfigurations(TenantRegistryOptions options)
{
    var result =
        new Dictionary<UnifyEmpi.Domain.TenantId, UnifyEmpi.Domain.TenantMatchingConfiguration>();
    foreach (var item in options.Items)
    {
        var tenantId = new UnifyEmpi.Domain.TenantId(item.TenantId);
        var secrets = item.BlockingSecrets.Select(secret =>
        {
            if (string.IsNullOrWhiteSpace(secret.Version))
            {
                throw new InvalidOperationException("Every blocking-key secret needs a version.");
            }

            byte[] value;
            try
            {
                value = Convert.FromBase64String(secret.SecretBase64);
            }
            catch (FormatException exception)
            {
                throw new InvalidOperationException(
                    $"Tenant '{tenantId}' has a blocking-key secret that is not base64.",
                    exception);
            }

            if (value.Length < 32)
            {
                throw new InvalidOperationException(
                    $"Tenant '{tenantId}' blocking-key secrets must contain at least 256 bits.");
            }

            return new UnifyEmpi.Domain.BlockingKeySecret(
                secret.Version,
                value,
                secret.Active);
        }).ToArray();
        if (secrets.Count(static secret => secret.IsActive) != 1)
        {
            throw new InvalidOperationException(
                $"Tenant '{tenantId}' must have exactly one active blocking-key secret.");
        }

        var profile = MatchingProfileFactory.Create(
            item.MatchingProfileVersion,
            item.PossibleThreshold,
            item.ProbableThreshold,
            item.MatchingRules);
        var trust = item.SourceTrust.ToDictionary(
            static pair => new UnifyEmpi.Domain.SourceSystemId(pair.Key),
            static pair => pair.Value);
        var authoritativeSources = item.AuthoritativeSources
            .Select(static source => new UnifyEmpi.Domain.SourceSystemId(source))
            .ToHashSet();
        if (authoritativeSources.Any(source => !trust.ContainsKey(source)))
        {
            throw new InvalidOperationException(
                $"Tenant '{tenantId}' has an authoritative source without a SourceTrust entry.");
        }

        result.Add(
            tenantId,
            new UnifyEmpi.Domain.TenantMatchingConfiguration(
                tenantId,
                profile,
                secrets,
                trust,
                authoritativeSources));
    }

    return result;
}

public partial class Program;
