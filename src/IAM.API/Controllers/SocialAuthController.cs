using System.Security.Claims;
using IAM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/auth/social")]
public class SocialAuthController : ControllerBase
{
    private readonly ISocialAuthService _socialAuthService;

    public SocialAuthController(ISocialAuthService socialAuthService)
    {
        _socialAuthService = socialAuthService;
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
