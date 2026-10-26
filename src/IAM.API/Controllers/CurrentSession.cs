using System.Security.Claims;

namespace IAM.API.Controllers;

/// <summary>
/// Which session is making this request. One rule for the sessions screen and the portal password change (task 5157).
/// </summary>
internal static class CurrentSession
{
    /// <summary>
    /// The UserSession id: the X-Session-Id header, else the JWT refresh_token_id claim (set at login).
    /// Null when it cannot be told.
    /// </summary>
    public static Guid? Resolve(HttpRequest request, ClaimsPrincipal user)
    {
        if (request.Headers.TryGetValue("X-Session-Id", out var header)
            && Guid.TryParse(header.FirstOrDefault(), out var headerSessionId))
            return headerSessionId;

        return RefreshTokenId(user);
    }

    /// <summary>
    /// The refresh token this access token was issued with (the signed refresh_token_id claim). A request header
    /// cannot name it, so a caller cannot ask to keep a refresh token that is not its own. Null when absent.
    /// </summary>
    public static Guid? RefreshTokenId(ClaimsPrincipal user)
        => Guid.TryParse(user.FindFirst("refresh_token_id")?.Value, out var id) ? id : null;
}
