using System.Diagnostics;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;
using UnifyEmpi.Domain;

namespace UnifyEmpi.Portal;

public static class PortalPermissions
{
    public const string Review = "mpi.review";
    public const string Audit = "mpi.audit";
    public const string Operations = "mpi.operations";
    public const string PatientWrite = "mpi.patient.write";
    public const string ConfigurationRead = "mpi.config.read";
    public const string ConfigurationWrite = "mpi.config.write";
    public const string Admin = "mpi.admin";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal)
        {
            Review,
            Audit,
            Operations,
            PatientWrite,
            ConfigurationRead,
            ConfigurationWrite,
            Admin
        };

    public static bool HasAny(ClaimsPrincipal principal, params string[] required)
    {
        var granted = principal.FindAll("scope")
            .Concat(principal.FindAll("scp"))
            .SelectMany(static claim =>
                claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Concat(principal.FindAll("mpi_permission").Select(static claim => claim.Value))
            .ToHashSet(StringComparer.Ordinal);
        return granted.Contains(Admin) || required.Any(granted.Contains);
    }
}

public sealed class PortalActorContextFactory(
    AuthenticationStateProvider authenticationStateProvider,
    IOptions<PortalOptions> portalOptions)
{
    public SourceSystemId ManagedSourceSystem =>
        new(portalOptions.Value.ManagedSourceSystem);

    public async ValueTask<ActorContext> CreateAsync()
    {
        var principal = (await authenticationStateProvider.GetAuthenticationStateAsync()).User;
        var tenant = principal.FindFirstValue("tenant_id");
        if (string.IsNullOrWhiteSpace(tenant))
        {
            throw new RegistryAuthorisationException(
                "The authenticated portal session has no tenant_id claim.");
        }

        var actor = principal.FindFirstValue("sub") ??
                    principal.FindFirstValue(ClaimTypes.NameIdentifier) ??
                    "unknown";
        var scopes = principal.FindAll("scope")
            .Concat(principal.FindAll("scp"))
            .SelectMany(static claim =>
                claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Concat(principal.FindAll("mpi_permission").Select(static claim => claim.Value))
            .ToHashSet(StringComparer.Ordinal);
        return new ActorContext(
            new TenantId(tenant),
            actor,
            null,
            scopes,
            Activity.Current?.TraceId.ToString() ?? Guid.CreateVersion7().ToString("N"));
    }

    public async ValueTask<ActorContext> CreateManagedSourceWriterAsync()
    {
        var actor = await CreateAsync();
        if (!actor.HasScope(PortalPermissions.PatientWrite) &&
            !actor.HasScope(PortalPermissions.Admin))
        {
            throw new RegistryAuthorisationException(
                "Patient create and update permission is required.");
        }

        return actor with
        {
            SourceSystem = ManagedSourceSystem,
            Scopes = actor.Scopes
                .Append("system/Patient.*")
                .ToHashSet(StringComparer.Ordinal)
        };
    }
}

public sealed class DevelopmentPortalAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<PortalAuthenticationOptions> authenticationOptions,
    IOptions<PortalOptions> portalOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "PortalDevelopment";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var configured = authenticationOptions.Value;
        var isPublicDemo = portalOptions.Value.PublicDemo;
        var claims = new[]
        {
            new Claim("sub", isPublicDemo ? "public-demo-reviewer" : "development-reviewer"),
            new Claim("name", isPublicDemo ? "Public demo reviewer" : "Development reviewer"),
            new Claim("tenant_id", configured.DevelopmentTenant),
            new Claim("scope", string.Join(' ', PortalPermissions.All))
        };
        var identity = new ClaimsIdentity(claims, SchemeName, "name", "role");
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

public sealed class PortalSecurityHeadersMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.ContentSecurityPolicy =
                "default-src 'self'; base-uri 'self'; object-src 'none'; frame-ancestors 'none'; " +
                "form-action 'self'; img-src 'self' data:; font-src 'self'; " +
                "style-src 'self'; script-src 'self'; connect-src 'self' ws: wss:";
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers.Append(
                "Permissions-Policy",
                "camera=(), microphone=(), geolocation=(), payment=(), usb=()");
            if (!context.Request.Path.StartsWithSegments("/_framework") &&
                !context.Request.Path.StartsWithSegments("/assets"))
            {
                headers.CacheControl = "no-store, max-age=0";
                headers.Pragma = "no-cache";
            }

            return Task.CompletedTask;
        });
        await next(context);
    }
}
