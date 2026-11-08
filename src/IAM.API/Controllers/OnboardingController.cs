using System.Security.Claims;
using System.Collections.Concurrent;
using IAM.Core.Configuration;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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
    /// Global role name granted to the creator of a new Organization. MUST NOT be a name
    /// recognized by any existing [Authorize(Roles = "...")] gate (e.g. "BuildingOwner",
    /// "BuildingManager") — those gates check the role name only, with no tenant
    /// comparison against the resource being acted on (TenantsController.CreateTenant/
    /// UpdateTenant/GetMembers/ChangeMemberRole/RemoveMember, RolesController.CreateRole,
    /// InvitationsController.SendInvitation/GetInvitations/RevokeInvitation,
    /// PoliciesController — see UsersController.cs:257's own "TODO: Add tenant-based
    /// authorization" acknowledging the gap). Auto-granting "BuildingOwner" here would let
    /// any self-registering customer immediately list/create/update/delete every tenant on
    /// the platform and manage every tenant's invitations, not just their own new one — a
    /// live privilege-escalation bug caught in review on this task (1741). Self-service
    /// management of an Organization beyond this onboarding call (inviting more teammates
    /// later, updating org settings) needs those endpoints retrofitted with real per-tenant
    /// checks first; until then this role intentionally has no reach outside this
    /// controller's own direct service calls.
    /// </summary>
    public const string OrganizationOwnerRoleName = "OrganizationOwner";

    private readonly IAMDbContext _context;
    private readonly IInvitationService _invitationService;
    private readonly ILogger<OnboardingController> _logger;
    private readonly OnboardingOptions _options;

    // Serializes onboarding per user so two parallel calls cannot both pass the
    // "already owns an organization" check. Process-local: IAM runs as a single instance.
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> UserLocks = new();

    public OnboardingController(
        IAMDbContext context,
        IInvitationService invitationService,
        ILogger<OnboardingController> logger,
        IOptions<OnboardingOptions> options)
    {
        _context = context;
        _invitationService = invitationService;
        _logger = logger;
        _options = options.Value;
    }

    /// <summary>
    /// Returns an error message when the name is empty, too long or contains control characters,
    /// line breaks or markup characters (the name ends up in invitation e-mail subjects).
    /// </summary>
    internal static string? ValidateOrganizationName(string? name, int maxLength)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return "Organization name is required";
        if (trimmed.Length > maxLength) return $"Organization name must be at most {maxLength} characters";
        foreach (var c in trimmed)
        {
            if (char.IsControl(c) || c == '<' || c == '>' || c == '&' || c == '"' || c == '`'
                || c == (char)0x2028 || c == (char)0x2029)
                return "Organization name contains characters that are not allowed";
        }
        return null;
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
    /// teammate to it in the same call. The caller becomes that org's owner
    /// (OrganizationOwnerRoleName, scoped to the new tenant) - no separate approval step
    /// needed. The invite (if requested) is sent here via a direct InvitationService call,
    /// not through InvitationsController's HTTP endpoint, so it works regardless of which
    /// admin endpoints the new role name happens to be recognized by.
    /// </summary>
    [HttpPost("organization")]
    public async Task<IActionResult> CreateOrganization([FromBody] CreateOrganizationRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var nameError = ValidateOrganizationName(request?.Name, _options.MaxOrganizationNameLength);
        if (nameError != null)
            return BadRequest(new { error = nameError });

        var userLock = UserLocks.GetOrAdd(userId.Value, _ => new SemaphoreSlim(1, 1));
        await userLock.WaitAsync();
        try
        {
            return await CreateOrganizationLocked(userId.Value, request!);
        }
        finally
        {
            userLock.Release();
        }
    }

    private async Task<IActionResult> CreateOrganizationLocked(Guid currentUserId, CreateOrganizationRequest request)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == currentUserId);
        if (user == null) return Unauthorized();

        var alreadyOwns = await _context.UserRoles
            .AnyAsync(ur => ur.UserId == user.Id
                && ur.Role.Name == OrganizationOwnerRoleName
                && ur.Tenant != null && ur.Tenant.Type == "Organization");
        if (alreadyOwns)
            return Conflict(new { error = "You already own an organization. Onboarding creates at most one organization per user." });

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
                Description = "Owns a self-service Organization tenant created via onboarding",
                IsSystemRole = true,
                Permissions = @"[""Organization.ManageOwn"",""User.InviteOwn""]",
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
        string? inviteSkippedReason = null;
        if (!string.IsNullOrWhiteSpace(inviteEmail))
        {
            inviteSkippedReason = await CheckInviteLimitsAsync(user.Id, inviteEmail);
        }

        if (inviteSkippedReason != null)
        {
            _logger.LogWarning("Organization {TenantId} created but teammate invite to {Email} skipped: {Reason}", tenant.Id, inviteEmail, inviteSkippedReason);
            invitation = new { error = inviteSkippedReason, emailSent = false };
        }
        else if (!string.IsNullOrWhiteSpace(inviteEmail))
        {
            try
            {
                var invited = await _invitationService.SendInvitationAsync(
                    inviteEmail, tenant.Id, ownerRole.Id, user.Id);
                invitation = new
                {
                    id = invited.Invitation.Id,
                    email = invited.Invitation.Email,
                    status = invited.Invitation.Status,
                    emailSent = invited.EmailSent
                };
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

    /// <summary>
    /// Server-side invitation limits, checked before any e-mail is sent. Returns a message when a
    /// limit is hit, otherwise null. Counts existing Invitation rows (no extra infrastructure).
    /// </summary>
    private async Task<string?> CheckInviteLimitsAsync(Guid userId, string email)
    {
        var now = DateTime.UtcNow;

        var userSince = now.AddMinutes(-_options.UserWindowMinutes);
        var sentByUser = await _context.Invitations
            .CountAsync(i => i.InvitedByUserId == userId && i.CreatedAt >= userSince);
        if (sentByUser >= _options.MaxInvitesPerUserPerWindow)
            return $"Invitation limit reached: at most {_options.MaxInvitesPerUserPerWindow} invitations per {_options.UserWindowMinutes} minutes. Try again later.";

        var normalized = email.ToLowerInvariant();
        var recipientSince = now.AddMinutes(-_options.RecipientWindowMinutes);
        var sentToRecipient = await _context.Invitations
            .CountAsync(i => i.Email.ToLower() == normalized && i.CreatedAt >= recipientSince);
        if (sentToRecipient >= _options.MaxInvitesPerRecipientPerWindow)
            return "This address was invited recently. Try again later.";

        return null;
    }

    private Guid? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return userIdClaim != null ? Guid.Parse(userIdClaim) : null;
    }
}

public record CreateOrganizationRequest(string Name, string? InviteEmail = null);
