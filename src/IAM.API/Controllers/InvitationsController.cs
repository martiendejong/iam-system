using System.Globalization;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
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
    /// Send an invitation to a user
    /// </summary>
    [HttpPost]
    [Authorize(Roles = "SuperAdmin,BuildingOwner,BuildingManager")]
    public async Task<IActionResult> SendInvitation([FromBody] SendInvitationRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        if (string.IsNullOrWhiteSpace(request.Email))
            return BadRequest(new { error = "Email is required" });

        try
        {
            var invitation = await _invitationService.SendInvitationAsync(
                request.Email,
                request.TenantId,
                request.RoleId,
                userId.Value,
                request.ExpiryDays);

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
    public async Task<IActionResult> SendBulkInvitations([FromForm] BulkInviteRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

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

        var result = await _invitationService.SendBulkInvitationsAsync(entries, request.TenantId, userId.Value);

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
    public async Task<IActionResult> GetInvitations([FromQuery] Guid tenantId)
    {
        var invitations = await _invitationService.GetInvitationsByTenantAsync(tenantId);

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
    public async Task<IActionResult> RevokeInvitation(Guid id)
    {
        var revoked = await _invitationService.RevokeInvitationAsync(id);

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
    public async Task<IActionResult> GetPendingInvitations([FromQuery] Guid tenantId)
    {
        var invitations = await _invitationService.GetPendingInvitationsAsync(tenantId);

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
    int? ExpiryDays = null
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
