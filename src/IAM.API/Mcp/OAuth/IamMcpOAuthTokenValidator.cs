using System.Security.Claims;
using IAM.Infrastructure.Data;
using Jengo.Mcp;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace IAM.API.Mcp.OAuth;

/// <summary>
/// Accepts an IAM-issued OAuth access token at <c>/mcp</c>. IAM is its own authorization server, so there is no second
/// token layer: the token is validated by OpenIddict's own validation handler (signature, issuer, expiry) and then has
/// to pass the checks that make it an MCP token and nothing else:
/// <list type="number">
/// <item>the <c>mcp</c> scope and the <c>iam_mcp</c> audience (an ordinary IAM login token has neither);</item>
/// <item>the client behind it is a dynamically registered MCP client that still exists;</item>
/// <item>the user is an existing, active, not locked-out IAM user;</item>
/// <item>the OpenIddict authorization the token belongs to is still valid (revoking it ends access).</item>
/// </list>
/// The resulting principal is marked with <see cref="McpOAuth.OAuthPrincipalClaim"/>; only the read-only OAuth tool set
/// answers it (the API-key tool set of docs/mcp-server.md never does).
/// </summary>
public sealed class IamMcpOAuthTokenValidator : IMcpBearerTokenValidator
{
    private readonly IConfiguration _configuration;
    private readonly IAMDbContext _db;
    private readonly IOpenIddictApplicationManager _applications;
    private readonly IOpenIddictAuthorizationManager _authorizations;
    private readonly ILogger<IamMcpOAuthTokenValidator> _logger;

    public IamMcpOAuthTokenValidator(
        IConfiguration configuration,
        IAMDbContext db,
        IOpenIddictApplicationManager applications,
        IOpenIddictAuthorizationManager authorizations,
        ILogger<IamMcpOAuthTokenValidator> logger)
    {
        _configuration = configuration;
        _db = db;
        _applications = applications;
        _authorizations = authorizations;
        _logger = logger;
    }

    public async ValueTask<ClaimsPrincipal?> ValidateAsync(string bearerToken, HttpContext httpContext, CancellationToken cancellationToken)
    {
        if (!McpOAuth.IsEnabled(_configuration)) return null;

        // Only JWT-shaped tokens are IAM access tokens; API keys and garbage never reach OpenIddict.
        if (bearerToken.Count(c => c == '.') != 2) return null;

        var result = await httpContext.AuthenticateAsync(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        if (!result.Succeeded || result.Principal is not { } token) return null;

        if (!token.HasScope(McpOAuth.Scope) || !token.HasAudience(McpOAuth.Audience))
        {
            _logger.LogDebug("IAM token refused at /mcp: missing mcp scope or iam_mcp audience");
            return null;
        }

        if (!Guid.TryParse(token.GetClaim(Claims.Subject), out var userId)) return null;

        var clientId = token.GetClaim(Claims.ClientId) ?? token.GetPresenters().FirstOrDefault();
        if (string.IsNullOrEmpty(clientId)) return null;
        var application = await _applications.FindByClientIdAsync(clientId, cancellationToken);
        if (application is null || !await McpOAuth.IsDynamicClientAsync(_applications, application)) return null;

        var user = await _db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.IsActive, u.IsLockedOut, u.LockoutEnd, u.Email })
            .FirstOrDefaultAsync(cancellationToken);
        if (user is null || !user.IsActive || (user.IsLockedOut && user.LockoutEnd > DateTime.UtcNow)) return null;

        var authorizationId = token.GetAuthorizationId();
        if (!string.IsNullOrEmpty(authorizationId))
        {
            var authorization = await _authorizations.FindByIdAsync(authorizationId, cancellationToken);
            if (authorization is null || await _authorizations.GetStatusAsync(authorization, cancellationToken) != Statuses.Valid) return null;
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new("sub", userId.ToString()),
            new(McpOAuth.OAuthPrincipalClaim, "true"),
            new(McpOAuth.ClientIdClaim, clientId),
        };
        return McpPrincipal.Create("IamMcpOAuth", user.Email, new[] { McpOAuth.Scope }, claims);
    }
}
