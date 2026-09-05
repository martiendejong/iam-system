using System.Security.Claims;
using System.Text.Json;
using IAM.Core.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/auth/social")]
public class SocialAuthController : ControllerBase
{
    private readonly ISocialAuthService _socialAuthService;
    private readonly IDataProtector _stateProtector;
    private readonly string _basePath;      // browser-facing prefix (ARR strips /auth)
    private readonly string _callbackUri;   // absolute redirect_uri geregistreerd bij de idp

    public SocialAuthController(
        ISocialAuthService socialAuthService,
        IDataProtectionProvider dataProtectionProvider,
        IConfiguration configuration)
    {
        _socialAuthService = socialAuthService;
        _stateProtector = dataProtectionProvider.CreateProtector("IAM.SocialAuth.State");

        // Zelfde patroon als AuthorizationController: het pad-deel van Jwt:Issuer is
        // de browser-facing prefix die ARR wegstript (bv. /auth).
        var issuer = configuration["Jwt:Issuer"] ?? "";
        _basePath = "";
        if (Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri))
        {
            var path = issuerUri.AbsolutePath.TrimEnd('/');
            if (path.Length > 0 && path != "/") _basePath = path;
        }
        _callbackUri = configuration["SocialAuth:BrowserCallbackUri"]
            ?? $"{issuer.TrimEnd('/')}/api/auth/social/callback";
    }

    // ── Server-gedreven browserflow (taak #1482: Entra ID → IAM.Session-cookie) ──
    // De bestaande SPA-flow (POST {providerId}/callback) levert alleen een eigen JWT
    // op; de workspace-portal en alle OpenIddict-clients hangen echter aan de
    // IAM.Session-cookie. Deze flow sluit federatieve logins daarop aan:
    //   /start?returnUrl=… → idp → /callback?code&state → SignIn(IAM.Session) → returnUrl.

    /// <summary>
    /// Public list of active identity providers for the login page (no secrets).
    /// </summary>
    [HttpGet("providers")]
    [AllowAnonymous]
    public async Task<IActionResult> GetPublicProviders([FromQuery] Guid? tenantId = null)
    {
        var providers = await _socialAuthService.GetIdentityProvidersAsync(tenantId);
        return Ok(providers
            .Where(p => p.IsActive)
            .Select(p => new { id = p.Id, name = p.Name, displayName = p.DisplayName, type = p.Type.ToString() }));
    }

    /// <summary>
    /// Start a federated browser login (e.g. "Sign in with Microsoft"): redirects to
    /// the external provider; the callback establishes the IAM.Session cookie.
    /// </summary>
    [HttpGet("{providerId:guid}/start")]
    [AllowAnonymous]
    public async Task<IActionResult> StartBrowserLogin(Guid providerId, [FromQuery] string? returnUrl = null)
    {
        var payload = JsonSerializer.Serialize(new StatePayload
        {
            ProviderId = providerId,
            ReturnUrl = SanitizeReturnUrl(returnUrl),
            ExpiresAtUnix = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds()
        });
        var state = _stateProtector.Protect(payload);

        try
        {
            var url = await _socialAuthService.GetAuthorizationUrlAsync(providerId, _callbackUri, state);
            return Redirect(url);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Browser callback from the external provider: validates state, exchanges the
    /// code, signs the user into the IAM.Session cookie and returns to returnUrl
    /// (typically the suspended /connect/authorize request of an OIDC client).
    /// </summary>
    [HttpGet("callback")]
    [AllowAnonymous]
    public async Task<IActionResult> BrowserCallback(
        [FromQuery] string? code = null,
        [FromQuery] string? state = null,
        [FromQuery] string? error = null)
    {
        StatePayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<StatePayload>(_stateProtector.Unprotect(state ?? ""))
                ?? throw new InvalidOperationException("empty state");
        }
        catch
        {
            return Redirect($"{_basePath}/login?error={Uri.EscapeDataString("Invalid or tampered login state")}");
        }

        var loginUrl = $"{_basePath}/login?returnUrl={Uri.EscapeDataString(payload.ReturnUrl)}";
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > payload.ExpiresAtUnix)
            return Redirect($"{loginUrl}&error={Uri.EscapeDataString("Login attempt expired, please try again")}");
        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
            return Redirect($"{loginUrl}&error={Uri.EscapeDataString(error ?? "Login was cancelled")}");

        var result = await _socialAuthService.HandleCallbackAsync(payload.ProviderId, code, state!, _callbackUri);
        if (!result.Success)
            return Redirect($"{loginUrl}&error={Uri.EscapeDataString(result.Error ?? "External login failed")}");

        // Zelfde sessiecookie-claims als AuthController.CompleteLoginAsync, zodat
        // /connect/authorize de gebruiker herkent en gewoon zijn OIDC-flow afmaakt.
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, result.User!.Id.ToString()),
            new(ClaimTypes.Email, result.User.Email),
            new(ClaimTypes.Name, $"{result.User.FirstName} {result.User.LastName}".Trim()),
        };
        var identity = new ClaimsIdentity(claims, "IAM.Session");
        await HttpContext.SignInAsync("IAM.Session", new ClaimsPrincipal(identity));

        return Redirect(ToBrowserPath(payload.ReturnUrl));
    }

    /// <summary>Alleen lokale paden toestaan (geen open redirect), default dashboard.</summary>
    private static string SanitizeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl)) return "/dashboard";
        if (!returnUrl.StartsWith('/') || returnUrl.StartsWith("//") || returnUrl.StartsWith("/\\"))
            return "/dashboard";
        return returnUrl;
    }

    /// <summary>Prefix het browser-facing basispad (ARR stript /auth server-side).</summary>
    private string ToBrowserPath(string localPath)
    {
        if (_basePath.Length > 0 && !localPath.StartsWith(_basePath + "/") && localPath != _basePath)
            return _basePath + localPath;
        return localPath;
    }

    private sealed class StatePayload
    {
        public Guid ProviderId { get; set; }
        public string ReturnUrl { get; set; } = "/dashboard";
        public long ExpiresAtUnix { get; set; }
    }

    /// <summary>
    /// Get the authorization URL for an external identity provider
    /// </summary>
    [HttpGet("{providerId:guid}/authorize")]
    public async Task<IActionResult> GetAuthorizationUrl(
        Guid providerId,
        [FromQuery] string redirectUri,
        [FromQuery] Guid? tenantId = null)
    {
        try
        {
            var state = Guid.NewGuid().ToString("N");
            var url = await _socialAuthService.GetAuthorizationUrlAsync(providerId, redirectUri, state);

            return Ok(new
            {
                authorizationUrl = url,
                state
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Handle the OAuth callback from an external identity provider
    /// </summary>
    [HttpPost("{providerId:guid}/callback")]
    public async Task<IActionResult> HandleCallback(
        Guid providerId,
        [FromBody] SocialCallbackRequest request)
    {
        var result = await _socialAuthService.HandleCallbackAsync(providerId, request.Code, request.State);

        if (!result.Success)
        {
            return BadRequest(new { error = result.Error });
        }

        // Set refresh token in HttpOnly cookie (same pattern as AuthController) - Expires
        // mirrors the refresh token's own resolved lifetime, not a hardcoded default.
        Response.Cookies.Append("refreshToken", result.RefreshToken!, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Expires = DateTimeOffset.UtcNow.AddDays(result.RefreshTokenLifetimeDays)
        });

        return Ok(new
        {
            accessToken = result.AccessToken,
            user = new
            {
                id = result.User!.Id,
                email = result.User.Email,
                firstName = result.User.FirstName,
                lastName = result.User.LastName
            }
        });
    }

    /// <summary>
    /// Link an external account to the current authenticated user
    /// </summary>
    [HttpPost("{providerId:guid}/link")]
    [Authorize]
    public async Task<IActionResult> LinkAccount(
        Guid providerId,
        [FromBody] SocialLinkRequest request)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized(new { error = "Invalid user identity" });
        }

        try
        {
            var externalLogin = await _socialAuthService.LinkAccountAsync(userId, providerId, request.Code);

            return Ok(new
            {
                id = externalLogin.Id,
                provider = externalLogin.Provider,
                providerUserId = externalLogin.ProviderUserId,
                email = externalLogin.Email,
                displayName = externalLogin.DisplayName,
                linkedAt = externalLogin.LinkedAt
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Unlink an external account from the current authenticated user
    /// </summary>
    [HttpDelete("{provider}/unlink")]
    [Authorize]
    public async Task<IActionResult> UnlinkAccount(string provider)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized(new { error = "Invalid user identity" });
        }

        var success = await _socialAuthService.UnlinkAccountAsync(userId, provider);

        if (!success)
        {
            return NotFound(new { error = "External login not found" });
        }

        return Ok(new { message = "External account unlinked successfully" });
    }

    /// <summary>
    /// Get all linked external accounts for the current authenticated user
    /// </summary>
    [HttpGet("linked-accounts")]
    [Authorize]
    public async Task<IActionResult> GetLinkedAccounts()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized(new { error = "Invalid user identity" });
        }

        var accounts = await _socialAuthService.GetLinkedAccountsAsync(userId);

        return Ok(accounts.Select(a => new
        {
            id = a.Id,
            provider = a.Provider,
            providerUserId = a.ProviderUserId,
            email = a.Email,
            displayName = a.DisplayName,
            linkedAt = a.LinkedAt,
            lastUsedAt = a.LastUsedAt
        }));
    }
}

public record SocialCallbackRequest(string Code, string State);
public record SocialLinkRequest(string Code);
