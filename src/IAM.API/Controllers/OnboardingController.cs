using System.Security.Claims;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IAM.API.Controllers;

/// <summary>
/// Guided first-run setup for a freshly self-registered customer: create their own
/// Organization tenant and invite a teammate, reusing the shared Invitation system
/// (InvitationsController/InvitationService, tasks 869cmvq8k/869cmvq9y) rather than
/// duplicating invite logic. A brand-new user holds no roles at all, so the existing
/// tenant-management endpoints (TenantsController.CreateTenant, InvitationsController.
/// SendInvitation) are both gated to SuperAdmin/BuildingOwner and unreachable until this
/// step grants them one.
/// </summary>
[ApiController]
[Route("api/onboarding")]
[Authorize]
public class OnboardingController : ControllerBase
{
    /// <summary>
    /// Global role name granted to the creator of a new Organization. Reuses the role
    /// name already recognized by TenantsController/InvitationsController's own
    /// [Authorize(Roles = "...BuildingOwner...")] gates rather than inventing a new one,
    /// so the new owner can immediately manage their tenant through those endpoints.
    /// </summary>
    public const string OrganizationOwnerRoleName = "BuildingOwner";

    private readonly IAMDbContext _context;
    private readonly IInvitationService _invitationService;
    private readonly ILogger<OnboardingController> _logger;

    public OnboardingController(
        IAMDbContext context,
        IInvitationService invitationService,
        ILogger<OnboardingController> logger)
    {
        _context = context;
        _invitationService = invitationService;
        _logger = logger;
    }

    /// <summary>
    /// Whether the current user already administers at least one Organization tenant -
    /// the guided UI uses this to decide whether to show the "set up your organization" step.
    /// </summary>
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var hasOrganization = await _context.UserRoles
            .Include(ur => ur.Tenant)
            .AnyAsync(ur => ur.UserId == userId.Value && ur.Tenant != null && ur.Tenant.Type == "Organization");

        return Ok(new { needsOrganizationSetup = !hasOrganization });
    }

    /// <summary>
    /// Guided step: create the caller's Organization and, optionally, invite the first
    /// teammate to it in the same call. The caller becomes that org's owner (BuildingOwner,
    /// scoped to the new tenant) so they can immediately manage it through the existing
    /// Tenants/Invitations admin endpoints - no separate approval step needed.
    /// </summary>
    [HttpPost("organization")]
    public async Task<IActionResult> CreateOrganization([FromBody] CreateOrganizationRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Organization name is required" });

        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId.Value);
        if (user == null) return Unauthorized();

        var tenant = new Tenant
        {
            Name = request.Name.Trim(),
            Type = "Organization",
            IsActive = true
        };
        _context.Tenants.Add(tenant);

        var ownerRole = await _context.Roles
            .FirstOrDefaultAsync(r => r.Name == OrganizationOwnerRoleName && r.TenantId == null);
        if (ownerRole == null)
        {
            ownerRole = new Role
            {
                Name = OrganizationOwnerRoleName,
                Description = "Owns and administers an Organization tenant",
                IsSystemRole = true,
                TenantId = null
            };
            _context.Roles.Add(ownerRole);
        }

        _context.UserRoles.Add(new UserRole
        {
            UserId = user.Id,
            RoleId = ownerRole.Id,
            TenantId = tenant.Id,
            GrantedBy = user.Id,
            GrantedAt = DateTime.UtcNow
        });

        await _context.SaveChangesAsync();

        object? invitation = null;
        var inviteEmail = request.InviteEmail?.Trim();
        if (!string.IsNullOrWhiteSpace(inviteEmail))
        {
            try
            {
                var invited = await _invitationService.SendInvitationAsync(
                    inviteEmail, tenant.Id, ownerRole.Id, user.Id);
                invitation = new { id = invited.Id, email = invited.Email, status = invited.Status };
            }
            catch (InvalidOperationException ex)
            {
                // Org creation already succeeded and must not be rolled back for an invite
                // failure (e.g. the teammate email is already a pending invite) - surface it
                // inline so the guided UI can show it without losing the new organization.
                _logger.LogWarning(ex, "Organization {TenantId} created but teammate invite to {Email} failed", tenant.Id, inviteEmail);
                invitation = new { error = ex.Message };
            }
        }

        _logger.LogInformation("User {UserId} created Organization {TenantId} ({Name}) during onboarding", user.Id, tenant.Id, tenant.Name);

        return StatusCode(StatusCodes.Status201Created, new
        {
            organizationId = tenant.Id,
            organizationName = tenant.Name,
            role = ownerRole.Name,
            invitation
        });
    }

    private Guid? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return userIdClaim != null ? Guid.Parse(userIdClaim) : null;
    }
}

public record CreateOrganizationRequest(string Name, string? InviteEmail = null);
