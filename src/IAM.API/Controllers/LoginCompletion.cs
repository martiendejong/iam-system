using IAM.API.Auth;
using System.Security.Claims;
using IAM.Core;
using IAM.Core.Services;
using IAM.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

/// <summary>
/// The one place that turns a successful sign-in (<see cref="AuthResult"/>) into the browser state: the refresh-token
/// cookie, the IAM.Session SSO cookie bound to that refresh token, and the JSON answer. Password, 2FA, step-up and the
/// approve-on-another-device sign-in all end here, so they behave the same.
/// </summary>
internal static class LoginCompletion
{
    public static async Task<IActionResult> CompleteAsync(HttpContext http, AuthResult result, bool rememberMe = false)
    {
        // Set refresh token in HttpOnly cookie - Expires mirrors the refresh token's own
        // lifetime (the organization's Token Configuration when one exists, otherwise
        // today's default) so the cookie never outlives, or expires before, the token it carries.
        // "Remember me" extends this to at least AuthConstants.RememberMeMinimumDays.
        var refreshDays = rememberMe
            ? Math.Max(result.RefreshTokenLifetimeDays, AuthConstants.RememberMeMinimumDays)
            : result.RefreshTokenLifetimeDays;
        http.Response.Cookies.Append("refreshToken", result.RefreshToken!, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Expires = DateTimeOffset.UtcNow.AddDays(refreshDays)
        });

        // Establish OIDC session cookie so the authorize endpoint can identify the user
        // without requiring a Bearer token in the browser request. Unchecked "Remember me"
        // keeps today's behavior: a non-persistent cookie whose ticket still expires after
        // Program.cs's sliding ExpireTimeSpan. Checked: a persistent cookie that survives
        // closing the browser and is valid for AuthConstants.RememberMeMinimumDays regardless
        // of activity.
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, result.User!.Id.ToString()),
            new(ClaimTypes.Email, result.User.Email),
            new(ClaimTypes.Name, $"{result.User.FirstName} {result.User.LastName}".Trim()),
        };
        // Task 5568: bind the cookie to the sign-in (refresh token) it belongs to, so revoking that sign-in ends the cookie.
        if (result.RefreshTokenId.HasValue)
            claims.Add(new(AccessTokenSessionValidator.RefreshTokenIdClaim, result.RefreshTokenId.Value.ToString()));
        var identity = new ClaimsIdentity(claims, "IAM.Session");
        var authProperties = rememberMe
            ? new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(AuthConstants.RememberMeMinimumDays)
            }
            : new AuthenticationProperties();
        await http.SignInAsync("IAM.Session", new ClaimsPrincipal(identity), authProperties);

        return new OkObjectResult(new
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
}
