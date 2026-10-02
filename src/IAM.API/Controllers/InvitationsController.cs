using System.Globalization;
using IAM.API.Authorization;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class InvitationsController : ControllerBase
{
    private readonly IInvitationService _invitationService;
    private readonly IAMDbContext _context;
    private readonly ILogger<InvitationsController> _logger;

    public InvitationsController(
        IInvitationService invitationService,
        IAMDbContext context,
        ILogger<InvitationsController> logger)
    {
        _invitationService = invitationService;
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Send an invitation to a user. Allowed for the admin roles, and (task 1496) for
    /// service accounts holding the exact "invitations:send" permission — TaskManager's
    /// Add-Team-Member flow sends invites server-to-server with its own scoped credential.
    /// </summary>
    [HttpPost]
    [Authorize]
    public async Task<IActionResult> SendInvitation([FromBody] SendInvitationRequest request, CancellationToken ct = default)
    {
        var isServiceAccount = ServiceAccountAuthorization.IsServiceAccount(User);
        var allowed = isServiceAccount
            ? ServiceAccountAuthorization.HasPermission(User, ServiceAccountAuthorization.InvitationsSendPermission)
            : User.IsInRole("SuperAdmin") || User.IsInRole("BuildingOwner") || User.IsInRole("BuildingManager");
        if (!allowed) return Forbid();

        // A human caller must manage the request's tenant (SuperAdmin or an active BuildingOwner/BuildingManager
        // row there). A service account keeps its permission-based gate and is treated as an owner-level grantor
        // without SuperAdmin: it can never hand out a platform-wide role.
        TenantGrantor grantor;
        if (isServiceAccount)
        {
            grantor = TenantGrantor.Owner;
        }
        else
        {
            var (humanGrantor, denied) = await RequireManagerAsync(request.TenantId, ct);
            if (denied != null) return denied;
            grantor = humanGrantor!;
        }

        Guid? userId;
        if (isServiceAccount)
        {
            // Invitation.InvitedByUserId is a hard FK to Users, and a service account is
            // not a user row. The caller must name the acting human via onBehalfOfEmail
            // (TaskManager sends the admin who clicked "Add Team Member"; that admin logs
            // in through IAM, so their email resolves to a real user here).
            var onBehalfOf = request.OnBehalfOfEmail?.Trim();
            if (string.IsNullOrWhiteSpace(onBehalfOf))
                return BadRequest(new { error = "onBehalfOfEmail is required when calling with a service-account token" });
            var actingUser = await _context.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == onBehalfOf.ToLower());
            if (actingUser == null)
                return BadRequest(new { error = "onBehalfOfEmail does not match any IAM user" });
            userId = actingUser.Id;
        }
        else
        {
            userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(request.Email))
            return BadRequest(new { error = "Email is required" });

        var targetRole = await _context.Roles.FirstOrDefaultAsync(r => r.Id == request.RoleId);
        if (targetRole == null)
            return BadRequest(new { error = "Role not found" });

        var problem = TenantRoleGrantRules.Check(targetRole, request.TenantId, grantor);
        if (problem != RoleGrantProblem.None)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = TenantRoleGrantRules.Describe(problem, targetRole.Name) });

        try
        {
            var invitation = await _invitationService.SendInvitationAsync(
                request.Email,
                request.TenantId,
                request.RoleId,
                userId.Value,
                request.ExpiryDays,
                ct);

            return Ok(new
            {
                id = invitation.Id,
                email = invitation.Email,
                tenantId = invitation.TenantId,
                roleId = invitation.RoleId,
                status = invitation.Status,
                expiresAt = invitation.ExpiresAt,
                createdAt = invitation.CreatedAt
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Send bulk invitations from CSV upload (columns: name, email, role)
    /// </summary>
    [HttpPost("bulk")]
    [Authorize(Roles = "SuperAdmin,BuildingOwner,BuildingManager")]
    public async Task<IActionResult> SendBulkInvitations([FromForm] BulkInviteRequest request, CancellationToken ct = default)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var (grantor, denied) = await RequireManagerAsync(request.TenantId, ct);
        if (denied != null) return denied;

        if (request.File == null || request.File.Length == 0)
            return BadRequest(new { error = "CSV file is required" });

        // Parse CSV
        var entries = new List<BulkInviteEntry>();
        using var reader = new StreamReader(request.File.OpenReadStream());

        // Read header line
        var headerLine = await reader.ReadLineAsync();
        if (headerLine == null)
            return BadRequest(new { error = "CSV file is empty" });

        var headers = headerLine.Split(',').Select(h => h.Trim().ToLowerInvariant()).ToArray();
        var nameIdx = Array.FindIndex(headers, h => h == "name");
        var emailIdx = Array.FindIndex(headers, h => h == "email");
        var roleIdx = Array.FindIndex(headers, h => h == "role");

        if (emailIdx < 0)
            return BadRequest(new { error = "CSV must contain an 'email' column" });

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) continue;

            var values = ParseCsvLine(line);

            entries.Add(new BulkInviteEntry
            {
                Name = nameIdx >= 0 && nameIdx < values.Length ? values[nameIdx].Trim() : string.Empty,
                Email = emailIdx < values.Length ? values[emailIdx].Trim() : string.Empty,
                RoleName = roleIdx >= 0 && roleIdx < values.Length ? values[roleIdx].Trim() : null
            });
        }

        if (entries.Count == 0)
            return BadRequest(new { error = "CSV file contains no data rows" });

        var result = await _invitationService.SendBulkInvitationsAsync(
            entries, request.TenantId, userId.Value, grantor!.IsSuperAdmin, ct, callerIsTenantOwner: grantor.IsTenantOwner);

        return Ok(new
        {
            totalProcessed = result.TotalProcessed,
            succeeded = result.Succeeded,
            failed = result.Failed,
            errors = result.Errors.Select(e => new
            {
                row = e.Row,
                email = e.Email,
                error = e.Error
            })
        });
    }

    /// <summary>
    /// List all invitations for a tenant
    /// </summary>
    [HttpGet]
    [Authorize(Roles = "SuperAdmin,BuildingOwner,BuildingManager")]
    public async Task<IActionResult> GetInvitations([FromQuery] Guid tenantId, CancellationToken ct = default)
    {
        var (_, denied) = await RequireManagerAsync(tenantId, ct);
        if (denied != null) return denied;

        var invitations = await _invitationService.GetInvitationsByTenantAsync(tenantId, ct);

        return Ok(invitations.Select(i => new
        {
            id = i.Id,
            email = i.Email,
            tenantId = i.TenantId,
            roleId = i.RoleId,
            roleName = i.Role?.Name,
            status = i.Status,
            invitedBy = i.InvitedByUser != null
                ? $"{i.InvitedByUser.FirstName} {i.InvitedByUser.LastName}".Trim()
                : null,
            expiresAt = i.ExpiresAt,
            acceptedAt = i.AcceptedAt,
            createdAt = i.CreatedAt
        }));
    }

    /// <summary>
    /// Accept an invitation by token (public endpoint - no auth required)
    /// </summary>
    [HttpPost("{token}/accept")]
    [AllowAnonymous]
    public async Task<IActionResult> AcceptInvitation(string token, [FromBody] AcceptInvitationRequest request)
    {
        var result = await _invitationService.AcceptInvitationAsync(
            token,
            request.Password,
            request.FirstName,
            request.LastName);

        if (!result.Success)
        {
            return BadRequest(new { error = result.Error });
        }

        return Ok(new
        {
            success = true,
            userId = result.User?.Id,
            email = result.User?.Email,
            welcomeMessage = result.WelcomeMessage,
            mfaSetupRequired = result.MfaSetupRequired
        });
    }

    /// <summary>
    /// Revoke a pending invitation
    /// </summary>
    [HttpDelete("{id}")]
    [Authorize(Roles = "SuperAdmin,BuildingOwner,BuildingManager")]
    public async Task<IActionResult> RevokeInvitation(Guid id, CancellationToken ct = default)
    {
        // Coarse gate first (a caller who manages no tenant gets 403 whether or not the id exists), then the
        // invitation's own tenant.
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();
        if (!await TenantManagementAuthority.ManagesAnyTenantAsync(_context, userId.Value, User.IsInRole("SuperAdmin"), ct))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Only a SuperAdmin or a tenant owner/manager can revoke invitations" });

        var invitationTenantId = await _context.Set<IAM.Core.Entities.Invitation>().AsNoTracking()
            .Where(i => i.Id == id)
            .Select(i => (Guid?)i.TenantId)
            .FirstOrDefaultAsync(ct);
        if (invitationTenantId == null)
            return NotFound(new { error = "Invitation not found or not pending" });

        var (_, denied) = await RequireManagerAsync(invitationTenantId.Value, ct);
        if (denied != null) return denied;

        var revoked = await _invitationService.RevokeInvitationAsync(id, ct);

        if (!revoked)
        {
            return NotFound(new { error = "Invitation not found or not pending" });
        }

        return Ok(new { message = "Invitation revoked successfully" });
    }

    /// <summary>
    /// Get pending invitations for a tenant
    /// </summary>
    [HttpGet("pending")]
    [Authorize(Roles = "SuperAdmin,BuildingOwner,BuildingManager")]
    public async Task<IActionResult> GetPendingInvitations([FromQuery] Guid tenantId, CancellationToken ct = default)
    {
        var (_, denied) = await RequireManagerAsync(tenantId, ct);
        if (denied != null) return denied;

        var invitations = await _invitationService.GetPendingInvitationsAsync(tenantId, ct);

        return Ok(invitations.Select(i => new
        {
            id = i.Id,
            email = i.Email,
            tenantId = i.TenantId,
            roleId = i.RoleId,
            roleName = i.Role?.Name,
            invitedBy = i.InvitedByUser != null
                ? $"{i.InvitedByUser.FirstName} {i.InvitedByUser.LastName}".Trim()
                : null,
            expiresAt = i.ExpiresAt,
            createdAt = i.CreatedAt
        }));
    }

    /// <summary>
    /// Get invitation details by token (public - for the accept page)
    /// </summary>
    [HttpGet("by-token/{token}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetInvitationByToken(string token)
    {
        var invitation = await _invitationService.GetInvitationByTokenAsync(token);

        if (invitation == null)
        {
            return NotFound(new { error = "Invitation not found" });
        }

        return Ok(new
        {
            email = invitation.Email,
            tenantName = invitation.Tenant?.Name,
            roleName = invitation.Role?.Name,
            status = invitation.Status,
            expiresAt = invitation.ExpiresAt,
            invitedBy = invitation.InvitedByUser != null
                ? $"{invitation.InvitedByUser.FirstName} {invitation.InvitedByUser.LastName}".Trim()
                : null
        });
    }

    /// <summary>
    /// The caller's authority over one tenant's invitations (task 4700): SuperAdmin, or an active BuildingOwner /
    /// BuildingManager UserRole scoped to exactly that tenant. Runs before any lookup or payload validation.
    /// Null grantor = the denial to send.
    /// </summary>
    private async Task<(TenantGrantor? Grantor, IActionResult? Denied)> RequireManagerAsync(Guid tenantId, CancellationToken ct)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
            return (null, Unauthorized());

        var grantor = await TenantManagementAuthority.ResolveAsync(_context, userId.Value, User.IsInRole("SuperAdmin"), tenantId, ct);
        if (grantor == null)
        {
            return (null, StatusCode(StatusCodes.Status403Forbidden,
                new { error = "Only a SuperAdmin or an owner/manager of this tenant can manage its invitations" }));
        }

        return (grantor, null);
    }

    private Guid? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return userIdClaim != null ? Guid.Parse(userIdClaim) : null;
    }

    private static string[] ParseCsvLine(string line)
    {
        var result = new List<string>();
        var inQuotes = false;
        var current = "";

        foreach (var ch in line)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (ch == ',' && !inQuotes)
            {
                result.Add(current);
                current = "";
            }
            else
            {
                current += ch;
            }
        }

        result.Add(current);
        return result.ToArray();
    }
}

public record SendInvitationRequest(
    string Email,
    Guid TenantId,
    Guid RoleId,
    int? ExpiryDays = null,
    /// <summary>Email of the acting human when the caller is a service account (task 1496);
    /// resolved to the InvitedBy user. Ignored for normal role-based callers.</summary>
    string? OnBehalfOfEmail = null
);

public record AcceptInvitationRequest(
    string? Password,
    string? FirstName,
    string? LastName
);

public record BulkInviteRequest
{
    public IFormFile? File { get; set; }
    public Guid TenantId { get; set; }
}
