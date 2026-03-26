using System.Security.Claims;
using IAM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SessionsController : ControllerBase
{
    private readonly ISessionService _sessionService;
    private readonly ILogger<SessionsController> _logger;

    public SessionsController(ISessionService sessionService, ILogger<SessionsController> logger)
    {
        _sessionService = sessionService;
        _logger = logger;
    }

    /// <summary>
    /// Get current user's active sessions.
    /// The caller's current session is marked with isCurrent = true,
    /// identified by the X-Session-Id header or the JWT's refresh_token_id claim.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetActiveSessions(CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "User ID not found in token" });

        var currentSessionId = GetCurrentSessionId();

        var sessions = await _sessionService.GetActiveSessionsAsync(userId.Value, ct);

        var response = sessions.Select(s => new
        {
            id = s.Id,
            ipAddress = s.IpAddress,
            userAgent = s.UserAgent,
            deviceInfo = s.DeviceInfo,
            location = s.Location,
            createdAt = s.CreatedAt,
            lastActivityAt = s.LastActivityAt,
            expiresAt = s.ExpiresAt,
            isCurrent = currentSessionId.HasValue && s.Id == currentSessionId.Value
        });

        return Ok(response);
    }

    /// <summary>
    /// Revoke a specific session by ID. Users can only revoke their own sessions.
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> RevokeSession(Guid id, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "User ID not found in token" });

        var success = await _sessionService.RevokeSessionAsync(id, userId.Value, "user_action", ct);

        if (!success)
            return NotFound(new { error = "Session not found or already revoked" });

        return Ok(new { message = "Session revoked successfully" });
    }

    /// <summary>
    /// Revoke all sessions except the current one.
    /// Current session is identified by X-Session-Id header or JWT refresh_token_id claim.
    /// </summary>
    [HttpDelete("revoke-others")]
    public async Task<IActionResult> RevokeOtherSessions(CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "User ID not found in token" });

        var currentSessionId = GetCurrentSessionId();
        if (!currentSessionId.HasValue)
            return BadRequest(new { error = "Cannot identify current session. Provide X-Session-Id header." });

        var count = await _sessionService.RevokeAllOtherSessionsAsync(userId.Value, currentSessionId.Value, ct);

        return Ok(new { message = $"Revoked {count} other session(s)", revokedCount = count });
    }

    /// <summary>
    /// Revoke all sessions for a user (admin action).
    /// Admins can revoke any user's sessions by providing a userId in the body.
    /// Non-admin users can only revoke their own sessions.
    /// </summary>
    [HttpDelete("revoke-all")]
    public async Task<IActionResult> RevokeAllSessions([FromBody] RevokeAllRequest? request, CancellationToken ct)
    {
        var callerUserId = GetUserId();
        if (callerUserId == null)
            return Unauthorized(new { error = "User ID not found in token" });

        var isAdmin = User.IsInRole("SuperAdmin") || User.IsInRole("SystemAdmin");
        Guid targetUserId;
        string reason;

        if (request?.UserId != null && request.UserId != callerUserId)
        {
            // Revoking another user's sessions requires admin role
            if (!isAdmin)
                return Forbid();

            targetUserId = request.UserId.Value;
            reason = "admin_action";
        }
        else
        {
            // Revoking own sessions
            targetUserId = callerUserId.Value;
            reason = "user_action";
        }

        var count = await _sessionService.RevokeAllUserSessionsAsync(targetUserId, reason, ct);

        return Ok(new { message = $"Revoked {count} session(s)", revokedCount = count });
    }

    /// <summary>
    /// Get session statistics (admin only).
    /// </summary>
    [HttpGet("statistics")]
    [Authorize(Roles = "SuperAdmin,SystemAdmin")]
    public async Task<IActionResult> GetStatistics([FromQuery] Guid? tenantId, CancellationToken ct)
    {
        var stats = await _sessionService.GetStatisticsAsync(tenantId, ct);

        return Ok(new
        {
            totalActiveSessions = stats.TotalActiveSessions,
            uniqueUsers = stats.UniqueUsers,
            sessionsByDevice = stats.SessionsByDevice,
            sessionsCreatedToday = stats.SessionsCreatedToday,
            sessionsRevokedToday = stats.SessionsRevokedToday
        });
    }

    /// <summary>
    /// Trigger cleanup of expired sessions (admin only).
    /// </summary>
    [HttpPost("cleanup")]
    [Authorize(Roles = "SuperAdmin,SystemAdmin")]
    public async Task<IActionResult> CleanupExpiredSessions(CancellationToken ct)
    {
        var count = await _sessionService.CleanupExpiredSessionsAsync(ct);

        return Ok(new { message = $"Cleaned up {count} expired session(s)", cleanedUpCount = count });
    }

    // --- Private helpers ---

    /// <summary>
    /// Extract the user ID from the JWT claims (supports both NameIdentifier and "sub" claim types).
    /// </summary>
    private Guid? GetUserId()
    {
        var claim = User.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? User.FindFirst("sub")?.Value;

        if (Guid.TryParse(claim, out var userId))
            return userId;

        return null;
    }

    /// <summary>
    /// Identify the current session from:
    /// 1. X-Session-Id header (preferred, set by client)
    /// 2. JWT refresh_token_id claim (fallback, set during login)
    /// </summary>
    private Guid? GetCurrentSessionId()
    {
        // Option 1: Explicit session ID header
        if (Request.Headers.TryGetValue("X-Session-Id", out var sessionIdHeader))
        {
            if (Guid.TryParse(sessionIdHeader.FirstOrDefault(), out var headerSessionId))
                return headerSessionId;
        }

        // Option 2: JWT refresh_token_id claim (the AuthService binds access tokens to refresh token IDs)
        var refreshTokenIdClaim = User.FindFirst("refresh_token_id")?.Value;
        if (Guid.TryParse(refreshTokenIdClaim, out var refreshTokenId))
            return refreshTokenId;

        return null;
    }
}

public record RevokeAllRequest(Guid? UserId);
