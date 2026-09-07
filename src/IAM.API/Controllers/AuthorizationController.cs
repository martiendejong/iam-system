using System.Collections.Immutable;
using System.Security.Claims;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace IAM.API.Controllers;

[ApiController]
[Route("connect")]
public class AuthorizationController : ControllerBase
{
    /// <summary>
    /// Same claim name machine auth already issues for tenant scoping — see
    /// ApiKeyAuthenticationMiddleware, ServiceAccountService, DeviceAuthenticationService.
    /// </summary>
    public const string TenantIdClaimType = "tenant_id";

    private readonly IAMDbContext _context;
    private readonly IOpenIddictApplicationManager _applicationManager;
    private readonly IOpenIddictAuthorizationManager _authorizationManager;
    private readonly IOpenIddictScopeManager _scopeManager;
    private readonly ILogger<AuthorizationController> _logger;
    private readonly string _loginBasePath;

    public AuthorizationController(
        IAMDbContext context,
        IOpenIddictApplicationManager applicationManager,
        IOpenIddictAuthorizationManager authorizationManager,
        IOpenIddictScopeManager scopeManager,
        ILogger<AuthorizationController> logger,
        IConfiguration configuration)
    {
        _context = context;
        _applicationManager = applicationManager;
        _authorizationManager = authorizationManager;
        _scopeManager = scopeManager;
        _logger = logger;

        // ARR strips the /auth/ prefix before forwarding to Kestrel, so redirect to /login
        // must include /auth prefix so the browser lands on the IAM admin-UI login page.
        var issuer = configuration["Jwt:Issuer"] ?? "";
        _loginBasePath = "";
        if (!string.IsNullOrEmpty(issuer) && Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri))
        {
            var path = issuerUri.AbsolutePath.TrimEnd('/');
            if (path.Length > 0 && path != "/")
                _loginBasePath = path;
        }
    }

    /// <summary>
    /// OAuth2 Authorization Endpoint - handles authorization requests
    /// </summary>
    [HttpGet("authorize")]
    [HttpPost("authorize")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Authorize()
    {
        var request = HttpContext.GetOpenIddictServerRequest() ??
                      throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        // Try to retrieve the user principal stored in the IAM session cookie
        var result = await HttpContext.AuthenticateAsync("IAM.Session");

        // If the user is not authenticated, redirect to React login page with return URL
        if (!result.Succeeded || result.Principal == null)
        {
            var returnUrl = Request.Path + QueryString.Create(
                Request.HasFormContentType ? Request.Form.ToList() : Request.Query.ToList());
            return Redirect($"/auth/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
        }

        // Retrieve user from database
        var userId = result.Principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return BadRequest(new { error = "invalid_request", error_description = "User ID not found in claims" });
        }

        var user = await _context.Users
            .Include(u => u.UserRoles)
            .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Id == Guid.Parse(userId));

        if (user == null || !user.IsActive)
        {
            return BadRequest(new { error = "invalid_request", error_description = "User not found or inactive" });
        }

        // Federated app-role gate: when an application has registered a role catalog
        // (Roles with Category "app:{clientId}" — see AppRolesController), only users
        // holding at least one of that app's roles may sign in to it. Applications
        // without a catalog keep the historic everyone-active behavior. FAIL-OPEN on
        // any error: a bug here must never lock every application out of the SSO.
        //
        // When the app does have a catalog, the matching UserRole's TenantId (see
        // UsersController.AssignRole / TenantsController.ChangeMemberRole — assignments
        // are already tenant-scopable) becomes the token's tenant_id claim, mirroring the
        // tenant_id claim machine auth already issues (ApiKeyAuthenticationMiddleware,
        // ServiceAccountService, DeviceAuthenticationService) but for human logins.
        Guid? tenantIdForToken = null;
        try
        {
            var clientIdLower = (request.ClientId ?? string.Empty).ToLowerInvariant();
            if (clientIdLower.Length > 0)
            {
                var category = $"app:{clientIdLower}";
                var appHasCatalog = await _context.Roles.AnyAsync(r => r.Category == category);
                if (appHasCatalog)
                {
                    var rolePrefix = clientIdLower + ":";
                    var appRoleAssignments = user.UserRoles
                        .Where(ur => ur.Role != null && ur.Role.Name.StartsWith(rolePrefix, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (appRoleAssignments.Count == 0)
                    {
                        _logger.LogWarning("App access denied: {Email} has no {ClientId} role", user.Email, clientIdLower);
                        return Content(
                            "<!doctype html><html><head><meta charset=\"utf-8\"><title>No access</title>" +
                            "<style>body{font-family:'Segoe UI',sans-serif;background:#f1f5f9;display:flex;align-items:center;justify-content:center;height:100vh;margin:0}" +
                            ".card{background:#fff;padding:2.5rem 3rem;border-radius:12px;box-shadow:0 4px 24px rgba(0,0,0,.08);text-align:center;max-width:420px}</style></head>" +
                            $"<body><div class=\"card\"><h2>No access to this application</h2><p>Your account ({System.Net.WebUtility.HtmlEncode(user.Email)}) is not authorized for <strong>{System.Net.WebUtility.HtmlEncode(clientIdLower)}</strong>.</p>" +
                            "<p>Ask your administrator to assign you a role for this application in the IAM system.</p></div></body></html>",
                            "text/html");
                    }
                    tenantIdForToken = ResolveAppRoleTenantId(appRoleAssignments);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "App-role gate failed for client {ClientId} - failing open", request.ClientId);
        }

        // Retrieve the application details from the database
        var application = await _applicationManager.FindByClientIdAsync(request.ClientId!) ??
            throw new InvalidOperationException("The application cannot be found.");

        // Create a new ClaimsIdentity for OpenIddict
        var identity = new ClaimsIdentity(
            authenticationType: TokenValidationParameters.DefaultAuthenticationType,
            nameType: Claims.Name,
            roleType: Claims.Role);

        // Add claims
        identity.SetClaim(Claims.Subject, user.Id.ToString())
                .SetClaim(Claims.Email, user.Email)
                .SetClaim(Claims.Name, $"{user.FirstName} {user.LastName}")
                .SetClaim(Claims.GivenName, user.FirstName)
                .SetClaim(Claims.FamilyName, user.LastName);

        // Add role claims
        var roles = user.UserRoles.Select(ur => ur.Role.Name).ToImmutableArray();
        identity.SetClaims(Claims.Role, roles);

        // Add tenant claim (only set when the app-role gate above resolved one — see
        // ResolveAppRoleTenantId; apps without a role catalog, or a global/unscoped app
        // role, keep issuing a token with no tenant_id, same as before this change)
        if (tenantIdForToken.HasValue)
        {
            identity.SetClaim(TenantIdClaimType, tenantIdForToken.Value.ToString());
        }

        // Set scopes
        identity.SetScopes(request.GetScopes());

        // Set destinations for claims (which token types they should appear in)
        identity.SetDestinations(GetDestinations);

        // Create authorization
        var authorization = await _authorizationManager.CreateAsync(
            identity: identity,
            subject: user.Id.ToString(),
            client: await _applicationManager.GetIdAsync(application)!,
            type: AuthorizationTypes.Permanent,
            scopes: identity.GetScopes());

        identity.SetAuthorizationId(await _authorizationManager.GetIdAsync(authorization));

        // Return the SignInResult to issue tokens
        return SignIn(new ClaimsPrincipal(identity), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// OAuth2 Token Endpoint - exchanges authorization code for access token
    /// </summary>
    [HttpPost("token")]
    [IgnoreAntiforgeryToken]
    [Produces("application/json")]
    public async Task<IActionResult> Exchange()
    {
        var request = HttpContext.GetOpenIddictServerRequest() ??
                      throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        ClaimsPrincipal claimsPrincipal;

        if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType())
        {
            // Retrieve the claims principal stored in the authorization code/refresh token
            claimsPrincipal = (await HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)).Principal!;
        }
        else if (request.IsClientCredentialsGrantType())
        {
            // For client credentials, create a claims principal from the application
            var identity = new ClaimsIdentity(TokenValidationParameters.DefaultAuthenticationType);

            // Add the client_id as subject
            identity.SetClaim(Claims.Subject, request.ClientId!);

            // Set the scopes
            identity.SetScopes(request.GetScopes());

            claimsPrincipal = new ClaimsPrincipal(identity);

            claimsPrincipal.SetScopes(request.GetScopes());
            claimsPrincipal.SetDestinations(GetDestinations);
        }
        else
        {
            return BadRequest(new
            {
                error = Errors.UnsupportedGrantType,
                error_description = "The specified grant type is not supported."
            });
        }

        // Return a SignInResult to issue tokens
        return SignIn(claimsPrincipal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// OIDC UserInfo Endpoint - returns user information
    /// </summary>
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)]
    [HttpGet("userinfo")]
    [HttpPost("userinfo")]
    [Produces("application/json")]
    public async Task<IActionResult> Userinfo()
    {
        var userId = User.FindFirst(Claims.Subject)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return BadRequest(new { error = "invalid_token", error_description = "User ID not found in token" });
        }

        var user = await _context.Users
            .Include(u => u.UserRoles)
            .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Id == Guid.Parse(userId));

        if (user == null)
        {
            return BadRequest(new { error = "invalid_token", error_description = "User not found" });
        }

        // Build claims based on requested scopes
        var claims = new Dictionary<string, object>
        {
            [Claims.Subject] = user.Id.ToString()
        };

        // Profile scope
        if (User.HasScope(Scopes.Profile))
        {
            claims[Claims.Name] = $"{user.FirstName} {user.LastName}";
            claims[Claims.GivenName] = user.FirstName;
            claims[Claims.FamilyName] = user.LastName;
        }

        // Email scope
        if (User.HasScope(Scopes.Email))
        {
            claims[Claims.Email] = user.Email;
            claims[Claims.EmailVerified] = user.EmailConfirmed;
        }

        // Roles scope
        if (User.HasScope("roles"))
        {
            claims[Claims.Role] = user.UserRoles.Select(ur => ur.Role.Name).ToArray();
        }

        // Custom tenants scope
        if (User.HasScope("tenants"))
        {
            var tenants = await _context.UserRoles
                .Where(ur => ur.UserId == user.Id && ur.TenantId != null)
                .Select(ur => new
                {
                    id = ur.TenantId,
                    name = ur.Tenant!.Name,
                    role = ur.Role.Name
                })
                .ToListAsync();

            claims["tenants"] = tenants;
        }

        return Ok(claims);
    }

    /// <summary>
    /// OAuth2 Logout Endpoint - handles logout requests
    /// </summary>
    [HttpGet("logout")]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        // Ask the OpenIddict server to sign out
        await HttpContext.SignOutAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

        // TODO: Also sign out from the cookie authentication scheme if using cookies
        // await HttpContext.SignOutAsync("Identity.Application");

        return Ok(new { message = "Logged out successfully" });
    }

    /// <summary>
    /// Determines which token types (access_token, id_token) each claim should appear in
    /// </summary>
    private static IEnumerable<string> GetDestinations(Claim claim)
    {
        // Note: by default, claims are NOT automatically included in tokens.
        // To allow OpenIddict to serialize them, you must attach them to a destination.

        switch (claim.Type)
        {
            // Include subject claim in both access and identity tokens
            case Claims.Subject:
                yield return Destinations.AccessToken;
                yield return Destinations.IdentityToken;
                yield break;

            // Include name claims in identity token
            case Claims.Name:
            case Claims.GivenName:
            case Claims.FamilyName:
                yield return Destinations.IdentityToken;
                yield break;

            // Include email claims in identity token
            case Claims.Email:
            case Claims.EmailVerified:
                yield return Destinations.IdentityToken;
                yield break;

            // Include role claims in both access and identity tokens
            case Claims.Role:
                yield return Destinations.AccessToken;
                yield return Destinations.IdentityToken;
                yield break;

            // Include tenant claim in both access and identity tokens, same as Role above
            case TenantIdClaimType:
                yield return Destinations.AccessToken;
                yield return Destinations.IdentityToken;
                yield break;

            // For other claims, only include in access token if scope is present
            default:
                yield return Destinations.AccessToken;
                yield break;
        }
    }

    /// <summary>
    /// Picks the tenant to scope a login token to, given the UserRole assignments that
    /// matched the signed-in app's role prefix (e.g. "taskmanager:"). A role can be
    /// assigned globally (TenantId == null, e.g. an internal/admin test account) or scoped
    /// to one tenant; when both a scoped and an unscoped assignment exist, or the user was
    /// granted the app role at more than one tenant, the first tenant-scoped one wins —
    /// "one tenant" per the task's own scope, not multi-tenant token claims.
    /// Public + static so it's unit-testable without standing up the full OIDC pipeline.
    /// </summary>
    public static Guid? ResolveAppRoleTenantId(IEnumerable<UserRole> appRoleAssignments)
    {
        foreach (var assignment in appRoleAssignments)
        {
            if (assignment.TenantId.HasValue)
                return assignment.TenantId;
        }
        return null;
    }
}
