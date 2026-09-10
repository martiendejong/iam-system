using System.Security.Claims;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PortalController : ControllerBase
{
    private readonly IAMDbContext _context;
    private readonly ISessionService _sessionService;
    private readonly ILogger<PortalController> _logger;

    public PortalController(
        IAMDbContext context,
        ISessionService sessionService,
        ILogger<PortalController> logger)
    {
        _context = context;
        _sessionService = sessionService;
        _logger = logger;
    }

    // ─── Profile ───────────────────────────────────────────────

    /// <summary>
    /// Get the current user's profile.
    /// </summary>
    [HttpGet("profile")]
    public async Task<IActionResult> GetProfile(CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "Invalid user" });

        var user = await _context.Users.FindAsync(new object[] { userId.Value }, ct);
        if (user == null)
            return NotFound(new { error = "User not found" });

        return Ok(new
        {
            id = user.Id,
            email = user.Email,
            firstName = user.FirstName,
            lastName = user.LastName,
            phoneNumber = user.PhoneNumber,
            avatarUrl = user.AvatarUrl,
            createdAt = user.CreatedAt
        });
    }

    /// <summary>
    /// Update the current user's profile (name, phone, avatar).
    /// </summary>
    [HttpPut("profile")]
    public async Task<IActionResult> UpdateProfile([FromBody] PortalUpdateProfileRequest request, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "Invalid user" });

        var user = await _context.Users.FindAsync(new object[] { userId.Value }, ct);
        if (user == null)
            return NotFound(new { error = "User not found" });

        if (!string.IsNullOrWhiteSpace(request.FirstName))
            user.FirstName = request.FirstName.Trim();

        if (!string.IsNullOrWhiteSpace(request.LastName))
            user.LastName = request.LastName.Trim();

        // Allow setting phone to null (clearing it)
        user.PhoneNumber = request.PhoneNumber?.Trim();
        user.AvatarUrl = request.AvatarUrl?.Trim();
        user.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);

        return Ok(new
        {
            id = user.Id,
            email = user.Email,
            firstName = user.FirstName,
            lastName = user.LastName,
            phoneNumber = user.PhoneNumber,
            avatarUrl = user.AvatarUrl,
            createdAt = user.CreatedAt
        });
    }

    // ─── Password ──────────────────────────────────────────────

    /// <summary>
    /// Change the current user's password. Requires the current password for verification.
    /// </summary>
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "Invalid user" });

        if (string.IsNullOrWhiteSpace(request.CurrentPassword) || string.IsNullOrWhiteSpace(request.NewPassword))
            return BadRequest(new { error = "Current password and new password are required" });

        if (request.NewPassword.Length < 8)
            return BadRequest(new { error = "New password must be at least 8 characters" });

        var user = await _context.Users.FindAsync(new object[] { userId.Value }, ct);
        if (user == null)
            return NotFound(new { error = "User not found" });

        // Verify current password
        if (!BCrypt.Net.BCrypt.Verify(request.CurrentPassword, user.PasswordHash))
            return BadRequest(new { error = "Current password is incorrect" });

        // Update password
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword, workFactor: 12);
        user.UpdatedAt = DateTime.UtcNow;

        // Log password change in audit log
        _context.AuditLogs.Add(new Core.Entities.AuditLog
        {
            UserId = userId.Value,
            Action = "PasswordChanged",
            Resource = "User",
            Details = """{"source":"portal"}""",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = Request.Headers["User-Agent"].ToString()
        });

        await _context.SaveChangesAsync(ct);

        return Ok(new { message = "Password changed successfully" });
    }

    // ─── Security Summary ──────────────────────────────────────

    /// <summary>
    /// Get a security overview: MFA status, passkey count, session count, last login, recovery codes remaining.
    /// </summary>
    [HttpGet("security-summary")]
    public async Task<IActionResult> GetSecuritySummary(CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "Invalid user" });

        var user = await _context.Users.FindAsync(new object[] { userId.Value }, ct);
        if (user == null)
            return NotFound(new { error = "User not found" });

        var passkeyCount = await _context.Credentials
            .CountAsync(c => c.UserId == userId.Value, ct);

        var sessions = await _sessionService.GetActiveSessionsAsync(userId.Value, ct);
        var sessionCount = sessions.Count;

        var recoveryCodesRemaining = await _context.RecoveryCodes
            .CountAsync(rc => rc.UserId == userId.Value && !rc.IsUsed, ct);

        return Ok(new
        {
            mfaEnabled = user.TwoFactorEnabled,
            mfaMethod = user.TwoFactorEnabled ? "totp" : (string?)null,
            passkeyCount,
            sessionCount,
            lastLoginAt = user.LastLoginAt,
            recoveryCodesRemaining
        });
    }

    // ─── Activity ──────────────────────────────────────────────

    /// <summary>
    /// Get a paginated list of the current user's own audit log entries.
    /// </summary>
    [HttpGet("activity")]
    public async Task<IActionResult> GetActivity(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? eventType = null,
        CancellationToken ct = default)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "Invalid user" });

        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 100) pageSize = 100;

        var query = _context.AuditLogs
            .Where(a => a.UserId == userId.Value)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(eventType))
            query = query.Where(a => a.Action == eventType);

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(a => a.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new
            {
                a.Id,
                a.Action,
                a.Resource,
                a.Details,
                a.IpAddress,
                a.UserAgent,
                a.CreatedAt
            })
            .ToListAsync(ct);

        return Ok(new { items, total, page, pageSize });
    }

    // ─── Helpers ───────────────────────────────────────────────

    private Guid? GetUserId()
    {
        var claim = User.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? User.FindFirst("sub")?.Value;

        if (Guid.TryParse(claim, out var userId))
            return userId;

        return null;
    }
}

// ─── Request DTOs ──────────────────────────────────────────

public record PortalUpdateProfileRequest(
    string? FirstName,
    string? LastName,
    string? PhoneNumber,
    string? AvatarUrl
);

public record ChangePasswordRequest(
    string CurrentPassword,
    string NewPassword
);
