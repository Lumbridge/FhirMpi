using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using UnifyEmpi.Application;
using UnifyEmpi.Application.Configuration;
using UnifyEmpi.Domain;
using UnifyEmpi.Fhir.R4;
using UnifyEmpi.Portal;
using UnifyEmpi.Portal.Components;
using UnifyEmpi.Storage.Abstractions;
using UnifyEmpi.Storage.Gcp;
using UnifyEmpi.Storage.InMemory;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<PortalAuthenticationOptions>(
    builder.Configuration.GetSection(PortalAuthenticationOptions.SectionName));
builder.Services.Configure<PortalOptions>(
    builder.Configuration.GetSection(PortalOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();

var authentication = builder.Configuration
    .GetSection(PortalAuthenticationOptions.SectionName)
    .Get<PortalAuthenticationOptions>() ?? new PortalAuthenticationOptions();
var portalOptions = builder.Configuration
    .GetSection(PortalOptions.SectionName)
    .Get<PortalOptions>() ?? new PortalOptions();
if (portalOptions.OverviewLoadTimeoutSeconds is < 5 or > 120)
{
    throw new InvalidOperationException(
        "Portal:OverviewLoadTimeoutSeconds must be between 5 and 120.");
}

if (portalOptions.PublicDemo && authentication.Enabled)
{
    throw new InvalidOperationException(
        "Portal public-demo mode requires external authentication to be disabled.");
}

if (portalOptions.SeedSyntheticData &&
    !builder.Environment.IsDevelopment() &&
    !portalOptions.PublicDemo)
{
    throw new InvalidOperationException(
        "Synthetic data can be seeded outside development only in explicit public-demo mode.");
}

if (!string.IsNullOrWhiteSpace(portalOptions.DataProtectionKeyPath))
{
    builder.Services.AddDataProtection()
        .SetApplicationName("UnifyEmpi.Portal")
        .PersistKeysToFileSystem(new DirectoryInfo(portalOptions.DataProtectionKeyPath));
}
else if (authentication.Enabled && !builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException(
        "Portal:DataProtectionKeyPath must reference shared durable storage in production.");
}

if (authentication.Enabled)
{
    if (string.IsNullOrWhiteSpace(authentication.Authority) ||
        string.IsNullOrWhiteSpace(authentication.ClientId))
    {
        throw new InvalidOperationException(
            "PortalAuthentication:Authority and ClientId are required when OIDC is enabled.");
    }

    builder.Services
        .AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
        })
        .AddCookie(options =>
        {
            options.Cookie.Name = "__Host-UnifyEmpi.Portal";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
            options.SlidingExpiration = true;
            options.AccessDeniedPath = "/access-denied";
        })
        .AddOpenIdConnect(options =>
        {
            options.Authority = authentication.Authority;
            options.ClientId = authentication.ClientId;
            options.ClientSecret = authentication.ClientSecret;
            options.RequireHttpsMetadata = authentication.RequireHttpsMetadata;
            options.ResponseType = OpenIdConnectResponseType.Code;
            options.UsePkce = true;
            options.SaveTokens = false;
            options.GetClaimsFromUserInfoEndpoint = true;
            options.MapInboundClaims = false;
            options.TokenValidationParameters.NameClaimType = authentication.NameClaimType;
            options.Scope.Clear();
            foreach (var scope in authentication.Scopes.Distinct(StringComparer.Ordinal))
            {
                options.Scope.Add(scope);
            }

            options.Events.OnTokenValidated = context =>
            {
                var principal = context.Principal ??
                                throw new InvalidOperationException(
                                    "OIDC validation produced no principal.");
                var tenants = principal.FindAll(authentication.TenantClaimType)
                    .Select(static claim => claim.Value)
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (tenants.Length != 1)
                {
                    context.Fail(
                        "The identity provider must issue exactly one tenant claim per portal session.");
                    return Task.CompletedTask;
                }

                if (!string.Equals(
                        authentication.TenantClaimType,
                        "tenant_id",
                        StringComparison.Ordinal) &&
                    principal.Identity is ClaimsIdentity identity)
                {
                    identity.AddClaim(new Claim("tenant_id", tenants[0]));
                }

                return Task.CompletedTask;
            };
        });
}
else
{
    builder.Services
        .AddAuthentication(DevelopmentPortalAuthenticationHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevelopmentPortalAuthenticationHandler>(
            DevelopmentPortalAuthenticationHandler.SchemeName,
            static _ => { });
}

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("PortalAccess", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("tenant_id");
        policy.RequireAssertion(context =>
            PortalPermissions.HasAny(context.User, [.. PortalPermissions.All]));
    })
    .AddPolicy("OperationsAccess", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("tenant_id");
        policy.RequireAssertion(context =>
            PortalPermissions.HasAny(
                context.User,
                PortalPermissions.Operations,
                PortalPermissions.Review));
    })
    .AddPolicy("ReviewAccess", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("tenant_id");
        policy.RequireAssertion(context =>
            PortalPermissions.HasAny(context.User, PortalPermissions.Review));
    })
    .AddPolicy("PatientWriteAccess", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("tenant_id");
        policy.RequireAssertion(context =>
            PortalPermissions.HasAny(context.User, PortalPermissions.PatientWrite));
        policy.RequireAssertion(context =>
            PortalPermissions.HasAny(context.User, PortalPermissions.Review));
    })
    .AddPolicy("AuditAccess", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("tenant_id");
        policy.RequireAssertion(context =>
            PortalPermissions.HasAny(context.User, PortalPermissions.Audit));
    })
    .AddPolicy("ConfigurationReadAccess", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("tenant_id");
        policy.RequireAssertion(context =>
            PortalPermissions.HasAny(
                context.User,
                PortalPermissions.ConfigurationRead,
                PortalPermissions.ConfigurationWrite,
                PortalPermissions.Operations));
    })
    .AddPolicy("ConfigurationWriteAccess", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("tenant_id");
        policy.RequireAssertion(context =>
            PortalPermissions.HasAny(context.User, PortalPermissions.ConfigurationWrite));
    });
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options =>
    {
        options.DisconnectedCircuitRetentionPeriod =
            TimeSpan.FromMinutes(Math.Clamp(portalOptions.CircuitRetentionMinutes, 1, 10));
        options.DetailedErrors = builder.Environment.IsDevelopment();
    });
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<PortalActorContextFactory>();
builder.Services.AddSingleton<FhirResourceCodec>();

var provider = builder.Configuration[$"{PortalRegistryProviderOptions.SectionName}:Type"]?.Trim();
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
            "Exactly one portal registry provider must be configured as 'InMemory' or 'GcpHealthcare'.");
}

var tenantOptions = builder.Configuration
    .GetSection(PortalTenantRegistryOptions.SectionName)
    .Get<PortalTenantRegistryOptions>() ?? new PortalTenantRegistryOptions();
var tenantConfigurations = BuildTenantConfigurations(tenantOptions);
if (tenantConfigurations.Count == 0)
{
    if (authentication.Enabled)
    {
        throw new InvalidOperationException(
            "At least one tenant must be configured when portal authentication is enabled.");
    }

    var development = DefaultTenantConfigurationFactory.CreateDevelopment(
        authentication.DevelopmentTenant);
    tenantConfigurations.Add(development.TenantId, development);
}

builder.Services.AddSingleton<IReadOnlyDictionary<TenantId, TenantMatchingConfiguration>>(
    tenantConfigurations);
builder.Services.AddSingleton<ITenantConfigurationProvider, StoredTenantConfigurationProvider>();
builder.Services.AddSingleton<RegistryService>();
builder.Services.AddHostedService<PortalRegistryStartupService>();
builder.Services.AddHostedService<DevelopmentDataSeeder>();
builder.Services.AddHealthChecks()
    .AddCheck<PortalRegistryHealthCheck>("registry", tags: ["ready"]);
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("UnifyEmpi.Portal"))
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
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseMiddleware<PortalSecurityHeadersMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = static _ => false
}).AllowAnonymous();
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = static check => check.Tags.Contains("ready")
}).AllowAnonymous();

if (authentication.Enabled)
{
    app.MapGet("/auth/login", (string? returnUrl) =>
        Results.Challenge(
            new AuthenticationProperties
            {
                RedirectUri = IsLocalReturnUrl(returnUrl) ? returnUrl : "/"
            },
            [OpenIdConnectDefaults.AuthenticationScheme]))
        .AllowAnonymous();
    app.MapPost(
            "/auth/logout",
            async (
                HttpContext context,
                Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery) =>
            {
                await antiforgery.ValidateRequestAsync(context);
                return Results.SignOut(
                    new AuthenticationProperties { RedirectUri = "/" },
                    [
                        CookieAuthenticationDefaults.AuthenticationScheme,
                        OpenIdConnectDefaults.AuthenticationScheme
                    ]);
            })
        .RequireAuthorization();
}
else
{
    app.MapGet("/auth/login", () => Results.Redirect("/")).AllowAnonymous();
    app.MapPost(
            "/auth/logout",
            async (
                HttpContext context,
                Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery) =>
            {
                await antiforgery.ValidateRequestAsync(context);
                return Results.Redirect("/");
            })
        .RequireAuthorization();
}

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .RequireAuthorization("PortalAccess");
app.Run();

static bool IsLocalReturnUrl(string? returnUrl) =>
    !string.IsNullOrWhiteSpace(returnUrl) &&
    returnUrl.StartsWith('/') &&
    !returnUrl.StartsWith("//", StringComparison.Ordinal) &&
    !returnUrl.StartsWith("/\\", StringComparison.Ordinal);

static Dictionary<TenantId, TenantMatchingConfiguration> BuildTenantConfigurations(
    PortalTenantRegistryOptions options)
{
    var result = new Dictionary<TenantId, TenantMatchingConfiguration>();
    foreach (var item in options.Items)
    {
        var tenantId = new TenantId(item.TenantId);
        var secrets = item.BlockingSecrets.Select(secret =>
        {
            if (string.IsNullOrWhiteSpace(secret.Version))
            {
                throw new InvalidOperationException(
                    $"Tenant '{tenantId}' has a blocking-key secret without a version.");
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

            return new BlockingKeySecret(secret.Version, value, secret.Active);
        }).ToArray();
        if (secrets.Count(static secret => secret.IsActive) != 1)
        {
            throw new InvalidOperationException(
                $"Tenant '{tenantId}' must have exactly one active blocking-key secret.");
        }

        var trust = item.SourceTrust.ToDictionary(
            static pair => new SourceSystemId(pair.Key),
            static pair => pair.Value);
        var authoritative = item.AuthoritativeSources
            .Select(static source => new SourceSystemId(source))
            .ToHashSet();
        if (authoritative.Any(source => !trust.ContainsKey(source)))
        {
            throw new InvalidOperationException(
                $"Tenant '{tenantId}' has an authoritative source without a trust setting.");
        }

        result.Add(
            tenantId,
            new TenantMatchingConfiguration(
                tenantId,
                MatchingProfileFactory.Create(
                    item.MatchingProfileVersion,
                    item.PossibleThreshold,
                    item.ProbableThreshold,
                    item.MatchingRules),
                secrets,
                trust,
                authoritative,
                Math.Clamp(item.RequiredLinkApprovals, 1, 2)));
    }

    return result;
}

public partial class Program;
