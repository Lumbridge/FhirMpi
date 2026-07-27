using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using UnifyEmpi.Domain;

namespace UnifyEmpi.Api;

public static class MpiScopes
{
    public const string Match = "mpi.match";
    public const string Review = "mpi.review";
    public const string Admin = "mpi.admin";
    public const string Audit = "mpi.audit";
    public const string Operations = "mpi.operations";
    public const string ConfigurationRead = "mpi.config.read";
    public const string ConfigurationWrite = "mpi.config.write";

    public static bool CanReadPatient(ActorContext actor) =>
        actor.HasScope(Match) ||
        actor.HasScope(Admin) ||
        actor.Scopes.Any(static scope =>
            scope.StartsWith("system/Patient.", StringComparison.Ordinal) &&
            (scope.EndsWith(".read", StringComparison.Ordinal) ||
             scope.EndsWith(".rs", StringComparison.Ordinal) ||
             scope.EndsWith(".*", StringComparison.Ordinal)));

    public static bool CanWritePatient(ActorContext actor) =>
        actor.HasScope(Admin) ||
        actor.Scopes.Any(static scope =>
            scope.StartsWith("system/Patient.", StringComparison.Ordinal) &&
            (scope.EndsWith(".write", StringComparison.Ordinal) ||
             scope.EndsWith(".cud", StringComparison.Ordinal) ||
             scope.EndsWith(".*", StringComparison.Ordinal)));

    public static bool CanReview(ActorContext actor) =>
        actor.HasScope(Review) || actor.HasScope(Admin);

    public static bool CanAudit(ActorContext actor) =>
        actor.HasScope(Audit) || actor.HasScope(Admin);

    public static bool CanOperate(ActorContext actor) =>
        actor.HasScope(Operations) || actor.HasScope(Review) || actor.HasScope(Admin);

    public static bool CanReadConfiguration(ActorContext actor) =>
        actor.HasScope(ConfigurationRead) ||
        actor.HasScope(ConfigurationWrite) ||
        actor.HasScope(Operations) ||
        actor.HasScope(Admin);

    public static bool CanWriteConfiguration(ActorContext actor) =>
        actor.HasScope(ConfigurationWrite) || actor.HasScope(Admin);
}

public sealed class ActorContextFactory(IHttpContextAccessor accessor)
{
    public ActorContext Create()
    {
        var httpContext = accessor.HttpContext ??
                          throw new InvalidOperationException("No HTTP request is active.");
        if (httpContext.Request.Headers.ContainsKey("X-Tenant-Id") ||
            httpContext.Request.Headers.ContainsKey("X-Source-System") ||
            httpContext.Request.Headers.ContainsKey("tenant_id") ||
            httpContext.Request.Headers.ContainsKey("source_system"))
        {
            throw new RegistryAuthorisationException(
                "Tenant and source-system identity may only come from validated claims.");
        }

        var principal = httpContext.User;
        var tenant = principal.FindFirstValue("tenant_id");
        if (string.IsNullOrWhiteSpace(tenant))
        {
            throw new RegistryAuthorisationException(
                "The validated access token has no tenant_id claim.");
        }

        var actor = principal.FindFirstValue("sub") ??
                    principal.FindFirstValue(ClaimTypes.NameIdentifier) ??
                    "unknown";
        var source = principal.FindFirstValue("source_system");
        var scopeValues = principal.FindAll("scope")
            .SelectMany(static claim =>
                claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Concat(principal.FindAll("scp").SelectMany(static claim =>
                claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
            .ToHashSet(StringComparer.Ordinal);

        return new ActorContext(
            new TenantId(tenant),
            actor,
            string.IsNullOrWhiteSpace(source) ? null : new SourceSystemId(source),
            scopeValues,
            httpContext.TraceIdentifier);
    }
}

public sealed class DevelopmentAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<AuthenticationOptions> authenticationOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Development";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var configured = authenticationOptions.Value;
        var claims = new[]
        {
            new Claim("sub", "development"),
            new Claim("tenant_id", configured.DevelopmentTenant),
            new Claim("source_system", configured.DevelopmentSourceSystem),
            new Claim(
                "scope",
                "system/Patient.* system/Person.read mpi.match mpi.review mpi.audit mpi.operations mpi.config.read mpi.config.write mpi.admin")
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
