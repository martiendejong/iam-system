using IAM.Core.Entities;
using IAM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PamController : ControllerBase
{
    private readonly IPrivilegedAccessService _pamService;
    private readonly ILogger<PamController> _logger;

    public PamController(
        IPrivilegedAccessService pamService,
        ILogger<PamController> logger)
    {
        _pamService = pamService;
        _logger = logger;
    }

    // ─── Session Management ──────────────────────────────────

    /// <summary>
    /// Request checkout of a privileged role
    /// </summary>
    [HttpPost("checkout")]
    public async Task<ActionResult<PrivilegedSession>> Checkout(
        [FromBody] PamCheckoutRequest request,
        CancellationToken cancellationToken = default)
    {
        var userIdClaim = User.FindFirst("sub")?.Value;
        if (!Guid.TryParse(userIdClaim, out var userId))
            return Unauthorized("User ID not found in token");

        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();

        try
        {
            var session = await _pamService.CheckoutAsync(
                userId,
                request.RoleId,
                request.TenantId,
                request.Justification,
                request.DurationMinutes,
                ipAddress,
                cancellationToken);

            return Ok(session);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Check in (release) a privileged role
    /// </summary>
    [HttpPost("checkin/{id}")]
    public async Task<ActionResult<PrivilegedSession>> Checkin(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var userIdClaim = User.FindFirst("sub")?.Value;
        if (!Guid.TryParse(userIdClaim, out var userId))
            return Unauthorized("User ID not found in token");

        try
        {
            var session = await _pamService.CheckinAsync(id, userId, cancellationToken);
            return Ok(session);
        }
        catch (ArgumentException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Approve a pending privileged session
    /// </summary>
    [HttpPost("sessions/{id}/approve")]
    [Authorize(Roles = "SecurityAdmin,SystemAdmin")]
    public async Task<ActionResult<PrivilegedSession>> ApproveSession(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var userIdClaim = User.FindFirst("sub")?.Value;
        if (!Guid.TryParse(userIdClaim, out var userId))
            return Unauthorized("User ID not found in token");

        try
        {
            var session = await _pamService.ApproveSessionAsync(id, userId, cancellationToken);
            return Ok(session);
        }
        catch (ArgumentException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Deny a pending privileged session
    /// </summary>
    [HttpPost("sessions/{id}/deny")]
    [Authorize(Roles = "SecurityAdmin,SystemAdmin")]
    public async Task<ActionResult<PrivilegedSession>> DenySession(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var userIdClaim = User.FindFirst("sub")?.Value;
        if (!Guid.TryParse(userIdClaim, out var userId))
            return Unauthorized("User ID not found in token");

        try
        {
            var session = await _pamService.DenySessionAsync(id, userId, cancellationToken);
            return Ok(session);
        }
        catch (ArgumentException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // ─── Break-Glass Emergency Access ────────────────────────

    /// <summary>
    /// Request break-glass emergency access (4-eyes principle)
    /// </summary>
    [HttpPost("break-glass")]
    public async Task<ActionResult<PrivilegedSession>> BreakGlass(
        [FromBody] PamBreakGlassRequest request,
        CancellationToken cancellationToken = default)
    {
        var userIdClaim = User.FindFirst("sub")?.Value;
        if (!Guid.TryParse(userIdClaim, out var userId))
            return Unauthorized("User ID not found in token");

        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();

        try
        {
            var session = await _pamService.BreakGlassAsync(
                userId,
                request.RoleId,
                request.TenantId,
                request.Justification,
                request.ApproverUserIds,
                request.DurationMinutes,
                ipAddress,
                cancellationToken);

            return Ok(session);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // ─── Session Queries ─────────────────────────────────────

    /// <summary>
    /// Get active privileged sessions
    /// </summary>
    [HttpGet("sessions")]
    [Authorize(Roles = "SecurityAdmin,SystemAdmin")]
    public async Task<ActionResult<List<PrivilegedSession>>> GetActiveSessions(
        [FromQuery] Guid? tenantId,
        [FromQuery] Guid? userId,
        CancellationToken cancellationToken = default)
    {
        var sessions = await _pamService.GetActiveSessionsAsync(tenantId, userId, cancellationToken);
        return Ok(sessions);
    }

    /// <summary>
    /// Get privileged session history
    /// </summary>
    [HttpGet("sessions/history")]
    [Authorize(Roles = "SecurityAdmin,SystemAdmin")]
    public async Task<ActionResult<List<PrivilegedSession>>> GetSessionHistory(
        [FromQuery] Guid? userId,
        [FromQuery] Guid? tenantId,
        [FromQuery] DateTime? startDate,
        [FromQuery] DateTime? endDate,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100,
        CancellationToken cancellationToken = default)
    {
        var history = await _pamService.GetSessionHistoryAsync(
            userId, tenantId, startDate, endDate, skip, take, cancellationToken);
        return Ok(history);
    }

    // ─── PAM Policy CRUD ─────────────────────────────────────

    /// <summary>
    /// Get all PAM policies
    /// </summary>
    [HttpGet("policies")]
    [Authorize(Roles = "SecurityAdmin,SystemAdmin")]
    public async Task<ActionResult<List<PamPolicy>>> GetPolicies(
        [FromQuery] Guid? tenantId,
        CancellationToken cancellationToken = default)
    {
        var policies = await _pamService.GetPoliciesAsync(tenantId, cancellationToken);
        return Ok(policies);
    }

    /// <summary>
    /// Get a PAM policy by ID
    /// </summary>
    [HttpGet("policies/{id}")]
    [Authorize(Roles = "SecurityAdmin,SystemAdmin")]
    public async Task<ActionResult<PamPolicy>> GetPolicy(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var policy = await _pamService.GetPolicyByIdAsync(id, cancellationToken);
        if (policy == null)
            return NotFound(new { error = $"PAM policy {id} not found" });

        return Ok(policy);
    }

    /// <summary>
    /// Create a PAM policy
    /// </summary>
    [HttpPost("policies")]
    [Authorize(Roles = "SecurityAdmin,SystemAdmin")]
    public async Task<ActionResult<PamPolicy>> CreatePolicy(
        [FromBody] PamPolicyRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var policy = new PamPolicy
            {
                TenantId = request.TenantId,
                RoleId = request.RoleId,
                MaxDurationMinutes = request.MaxDurationMinutes,
                RequireJustification = request.RequireJustification,
                RequireApproval = request.RequireApproval,
                ApproverRoleId = request.ApproverRoleId,
                BreakGlassEnabled = request.BreakGlassEnabled,
                BreakGlassApproversRequired = request.BreakGlassApproversRequired,
                IsActive = request.IsActive
            };

            var created = await _pamService.CreatePolicyAsync(policy, cancellationToken);
            return CreatedAtAction(nameof(GetPolicy), new { id = created.Id }, created);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Update a PAM policy
    /// </summary>
    [HttpPut("policies/{id}")]
    [Authorize(Roles = "SecurityAdmin,SystemAdmin")]
    public async Task<ActionResult<PamPolicy>> UpdatePolicy(
        Guid id,
        [FromBody] PamPolicyRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var policy = new PamPolicy
            {
                Id = id,
                MaxDurationMinutes = request.MaxDurationMinutes,
                RequireJustification = request.RequireJustification,
                RequireApproval = request.RequireApproval,
                ApproverRoleId = request.ApproverRoleId,
                BreakGlassEnabled = request.BreakGlassEnabled,
                BreakGlassApproversRequired = request.BreakGlassApproversRequired,
                IsActive = request.IsActive
            };

            var updated = await _pamService.UpdatePolicyAsync(policy, cancellationToken);
            return Ok(updated);
        }
        catch (ArgumentException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Delete a PAM policy
    /// </summary>
    [HttpDelete("policies/{id}")]
    [Authorize(Roles = "SecurityAdmin,SystemAdmin")]
    public async Task<ActionResult> DeletePolicy(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _pamService.DeletePolicyAsync(id, cancellationToken);
            return NoContent();
        }
        catch (ArgumentException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }
}

// ─── Request DTOs ────────────────────────────────────────

public class PamCheckoutRequest
{
    public Guid RoleId { get; set; }
    public Guid TenantId { get; set; }
    public string Justification { get; set; } = string.Empty;
    public int? DurationMinutes { get; set; }
}

public class PamBreakGlassRequest
{
    public Guid RoleId { get; set; }
    public Guid TenantId { get; set; }
    public string Justification { get; set; } = string.Empty;
    public List<Guid> ApproverUserIds { get; set; } = new();
    public int? DurationMinutes { get; set; }
}

public class PamPolicyRequest
{
    public Guid TenantId { get; set; }
    public Guid RoleId { get; set; }
    public int MaxDurationMinutes { get; set; } = 480;
    public bool RequireJustification { get; set; } = true;
    public bool RequireApproval { get; set; } = true;
    public Guid? ApproverRoleId { get; set; }
    public bool BreakGlassEnabled { get; set; } = false;
    public int BreakGlassApproversRequired { get; set; } = 2;
    public bool IsActive { get; set; } = true;
}
