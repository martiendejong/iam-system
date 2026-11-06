using System.Globalization;
using System.Security.Claims;
using IAM.Core;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

namespace IAM.API.Auth;

/// <summary>
/// Task 5490: the IAM single sign-on session (the IAM.Session cookie that /connect/authorize reads).
/// The cookie carries the user's security stamp, the time of the interactive sign-in and an absolute
/// expiry. The sliding ExpireTimeSpan alone would keep a copied cookie alive for as long as it is used.
/// </summary>
public static class SsoSession
{
    public const string Scheme = "IAM.Session";
    public const string StampClaim = "iam_stamp";
    public const string AuthTimeClaim = "iam_auth_time";
    public const string AbsoluteExpiryClaim = "iam_abs_exp";

    /// <summary>Default absolute lifetime of a normal (non remember-me) session, in hours.</summary>
    public const int DefaultAbsoluteHours = 24;

    public static ClaimsIdentity BuildIdentity(User user, bool rememberMe, DateTimeOffset now, int absoluteHours)
    {
        var absolute = rememberMe ? TimeSpan.FromDays(AuthConstants.RememberMeMinimumDays) : TimeSpan.FromHours(absoluteHours);
        return new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Name, $"{user.FirstName} {user.LastName}".Trim()),
            new Claim(StampClaim, user.SecurityStamp ?? string.Empty),
            new Claim(AuthTimeClaim, now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
            new Claim(AbsoluteExpiryClaim, (now + absolute).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
        }, Scheme);
    }

    /// <summary>
    /// Why a cookie no longer counts, or null when it is still good. A cookie without the stamp or
    /// expiry claims (issued before this change) is rejected on purpose: the user signs in once more.
    /// </summary>
    public static string? Reject(ClaimsPrincipal principal, User user, DateTimeOffset now)
    {
        var stamp = principal.FindFirst(StampClaim)?.Value;
        if (string.IsNullOrEmpty(stamp) || string.IsNullOrEmpty(user.SecurityStamp)
            || !string.Equals(stamp, user.SecurityStamp, StringComparison.Ordinal))
            return "The IAM session was ended (sign-out, password change or deactivation).";

        if (!TryReadUnix(principal, AbsoluteExpiryClaim, out var expiry) || now.ToUnixTimeSeconds() >= expiry)
            return "The IAM session reached its maximum age.";

        return null;
    }

    /// <summary>True when the last interactive sign-in is older than maxAgeSeconds (OIDC max_age).</summary>
    public static bool IsOlderThan(ClaimsPrincipal principal, long maxAgeSeconds, DateTimeOffset now) =>
        !TryReadUnix(principal, AuthTimeClaim, out var authTime) || now.ToUnixTimeSeconds() - authTime > maxAgeSeconds;

    public static int AbsoluteHours(IConfiguration configuration)
    {
        var hours = configuration.GetValue("Sso:AbsoluteSessionHours", DefaultAbsoluteHours);
        return hours > 0 ? hours : DefaultAbsoluteHours;
    }

    /// <summary>
    /// Ends the single sign-on session for good: rotates the signed-in user's stamp (so a copied cookie
    /// stops working too, in every browser) and clears the cookie. Works with an absent or stale cookie.
    /// </summary>
    public static async Task EndAsync(HttpContext http)
    {
        var result = await http.AuthenticateAsync(Scheme);
        var userId = result.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (Guid.TryParse(userId, out var id))
        {
            var db = http.RequestServices.GetRequiredService<IAMDbContext>();
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id);
            if (user != null)
            {
                user.RotateSecurityStamp();
                await db.SaveChangesAsync();
            }
        }
        await http.SignOutAsync(Scheme);
    }

    private static bool TryReadUnix(ClaimsPrincipal principal, string type, out long value) =>
        long.TryParse(principal.FindFirst(type)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}
