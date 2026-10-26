using IAM.API.Authorization;
using IAM.Core.Entities;
using IAM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.Text.Json;
using Hazina.Security.ApiKeys;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class GroupsController : ControllerBase
{
    private readonly IGroupService _groupService;
    private readonly ITenantAccessResolver _access;

    public GroupsController(IGroupService groupService, ITenantAccessResolver access)
    {
        _groupService = groupService;
        _access = access;
    }

    // Task 5161. Reading a group exposes member names, e-mail addresses, roles and the permission roles attached to
    // it, so the read routes follow the tenant rule of the device endpoints: members of the group's tenant and
    // SuperAdmin can read it; an active member of the group itself can read that one group (SCIM and AddMember do
    // not require a tenant role). Everyone else, including device/service tokens and tokens scoped to another
    // tenant, gets the same 403 whether or not the group exists. The tenant always comes from the stored group.

    private ObjectResult ForbiddenRead() =>
        StatusCode(StatusCodes.Status403Forbidden, new { error = "You do not have access to the groups of this tenant." });

    /// <summary>True when the caller may read this stored group: tenant read access, or an active membership of the group itself.</summary>
    private bool CanReadGroup(TenantAccess access, Group group)
    {
        if (access.CanRead(group.TenantId))
        {
            return true;
        }

        // The group-member exception needs a real user token: device and service-account tokens are refused outright
        // and an API key is a credential of its own, not its owner's memberships.
        if (access.IsRefused || User.IsApiKey())
        {
            return false;
        }

        // A tenant-scoped token carries authority only inside its own tenant.
        var tenantClaim = User.FindFirst("tenant_id")?.Value;
        if (!string.IsNullOrWhiteSpace(tenantClaim)
            && !(Guid.TryParse(tenantClaim, out var claimedTenant) && claimedTenant == group.TenantId))
        {
            return false;
        }

        var subject = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (!Guid.TryParse(subject, out var userId))
        {
            return false;
        }

        var now = DateTime.UtcNow;
        return group.Members.Any(m => m.UserId == userId && m.IsActive && (m.ExpiresAt == null || m.ExpiresAt > now));
    }

    /// <summary>
    /// Loads a group for a read route. Returns null plus the response to send when the caller may not read it;
    /// an unknown id answers a non-admin exactly like a foreign group (403), only a caller who can read every
    /// tenant learns that the id does not exist.
    /// </summary>
    private async Task<(Group? Group, IActionResult? Denied)> LoadReadableGroupAsync(Guid id, CancellationToken ct)
    {
        var access = await _access.ResolveAsync(User, ct);
        if (access.IsRefused)
        {
            return (null, ForbiddenRead());
        }

        var group = await _groupService.GetGroupAsync(id, ct);
        if (group == null)
        {
            return (null, access.AllTenants ? NotFound(new { error = "Group not found" }) : ForbiddenRead());
        }

        return CanReadGroup(access, group) ? (group, null) : (null, ForbiddenRead());
    }

    /// <summary>
    /// The caller on whose behalf group mutations run (task 4056). Global admin = SuperAdmin.
    /// Password-login tokens carry no tenant_id; when a tenant_id claim IS present the service
    /// only honours group authority inside that tenant.
    /// </summary>
    private GroupActor? TryGetActor()
    {
        var subject = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (!Guid.TryParse(subject, out var userId))
        {
            return null;
        }

        Guid? tenantId = null;
        var tenantClaim = User.FindFirst("tenant_id")?.Value;
        if (!string.IsNullOrWhiteSpace(tenantClaim))
        {
            if (!Guid.TryParse(tenantClaim, out var parsedTenantId))
            {
                return null;
            }
            tenantId = parsedTenantId;
        }

        return new GroupActor(userId, User.IsInRole("SuperAdmin"), tenantId);
    }

    private ObjectResult Forbidden(GroupAccessDeniedException ex) =>
        StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });

    /// <summary>
    /// Create a new group
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> CreateGroup([FromBody] CreateGroupRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { error = "Group name is required" });
        }

        if (request.TenantId == Guid.Empty)
        {
            return BadRequest(new { error = "Tenant ID is required" });
        }

        var actor = TryGetActor();
        if (actor == null)
        {
            return Unauthorized();
        }

        var metadataJson = request.Metadata != null
            ? JsonSerializer.Serialize(request.Metadata)
            : null;

        var group = new Group
        {
            Name = request.Name,
            Description = request.Description,
            TenantId = request.TenantId,
            ParentGroupId = request.ParentGroupId,
            GroupType = request.GroupType ?? "team",
            Metadata = metadataJson
        };

        try
        {
            var created = await _groupService.CreateGroupAsync(group, actor);

            return CreatedAtAction(
                nameof(GetGroup),
                new { id = created.Id },
                new
                {
                    id = created.Id,
                    name = created.Name,
                    description = created.Description,
                    tenantId = created.TenantId,
                    parentGroupId = created.ParentGroupId,
                    groupType = created.GroupType,
                    metadata = request.Metadata,
                    isActive = created.IsActive,
                    createdAt = created.CreatedAt
                });
        }
        catch (GroupAccessDeniedException ex)
        {
            return Forbidden(ex);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get group by ID with members and roles
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetGroup(Guid id, CancellationToken ct = default)
    {
        var (group, denied) = await LoadReadableGroupAsync(id, ct);
        if (group == null)
        {
            return denied!;
        }

        return Ok(new
        {
            id = group.Id,
            name = group.Name,
            description = group.Description,
            tenantId = group.TenantId,
            tenantName = group.Tenant?.Name,
            parentGroupId = group.ParentGroupId,
            parentGroupName = group.ParentGroup?.Name,
            groupType = group.GroupType,
            metadata = !string.IsNullOrWhiteSpace(group.Metadata)
                ? JsonSerializer.Deserialize<Dictionary<string, object>>(group.Metadata)
                : null,
            isActive = group.IsActive,
            memberCount = group.Members.Count,
            members = group.Members.Select(m => new
            {
                id = m.Id,
                userId = m.UserId,
                userName = m.User != null ? $"{m.User.FirstName} {m.User.LastName}" : null,
                userEmail = m.User?.Email,
                role = m.Role,
                joinedAt = m.JoinedAt,
                expiresAt = m.ExpiresAt
            }),
            roles = group.GroupRoles.Select(gr => new
            {
                id = gr.Id,
                roleId = gr.RoleId,
                roleName = gr.Role?.Name,
                tenantId = gr.TenantId,
                assignedAt = gr.AssignedAt
            }),
            childGroups = group.ChildGroups.Select(cg => new
            {
                id = cg.Id,
                name = cg.Name,
                groupType = cg.GroupType,
                isActive = cg.IsActive
            }),
            createdAt = group.CreatedAt,
            updatedAt = group.UpdatedAt
        });
    }

    /// <summary>
    /// List groups by tenant
    /// </summary>
    [HttpGet("by-tenant/{tenantId:guid}")]
    public async Task<IActionResult> GetGroupsByTenant(Guid tenantId, CancellationToken ct = default)
    {
        // Listing a tenant needs membership of that tenant (or SuperAdmin); the group-member exception covers
        // single groups only, never a list.
        var access = await _access.ResolveAsync(User, ct);
        if (!access.CanRead(tenantId))
        {
            return ForbiddenRead();
        }

        var groups = await _groupService.GetGroupsByTenantAsync(tenantId, ct);

        return Ok(groups.Select(g => new
        {
            id = g.Id,
            name = g.Name,
            description = g.Description,
            parentGroupId = g.ParentGroupId,
            parentGroupName = g.ParentGroup?.Name,
            groupType = g.GroupType,
            memberCount = g.Members.Count,
            isActive = g.IsActive,
            createdAt = g.CreatedAt
        }));
    }

    /// <summary>
    /// Update a group
    /// </summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateGroup(Guid id, [FromBody] UpdateGroupRequest request)
    {
        var actor = TryGetActor();
        if (actor == null)
        {
            return Unauthorized();
        }

        var metadataJson = request.Metadata != null
            ? JsonSerializer.Serialize(request.Metadata)
            : null;

        try
        {
            var group = await _groupService.UpdateGroupAsync(id, request.Name ?? string.Empty, request.Description, metadataJson, actor);

            return Ok(new
            {
                id = group.Id,
                name = group.Name,
                description = group.Description,
                groupType = group.GroupType,
                metadata = request.Metadata,
                isActive = group.IsActive,
                updatedAt = group.UpdatedAt
            });
        }
        catch (GroupAccessDeniedException ex)
        {
            return Forbidden(ex);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Delete a group
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteGroup(Guid id)
    {
        var actor = TryGetActor();
        if (actor == null)
        {
            return Unauthorized();
        }

        try
        {
            var result = await _groupService.DeleteGroupAsync(id, actor);
            if (!result)
            {
                return NotFound(new { error = "Group not found" });
            }

            return Ok(new { message = "Group deleted successfully" });
        }
        catch (GroupAccessDeniedException ex)
        {
            return Forbidden(ex);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Add a member to a group
    /// </summary>
    [HttpPost("{id:guid}/members")]
    public async Task<IActionResult> AddMember(Guid id, [FromBody] AddMemberRequest request)
    {
        if (request.UserId == Guid.Empty)
        {
            return BadRequest(new { error = "User ID is required" });
        }

        var actor = TryGetActor();
        if (actor == null)
        {
            return Unauthorized();
        }

        try
        {
            var membership = await _groupService.AddMemberAsync(
                id,
                request.UserId,
                actor,
                request.Role ?? "member",
                request.ExpiresAt);

            return Ok(new
            {
                id = membership.Id,
                groupId = membership.GroupId,
                userId = membership.UserId,
                role = membership.Role,
                joinedAt = membership.JoinedAt,
                expiresAt = membership.ExpiresAt,
                isActive = membership.IsActive
            });
        }
        catch (GroupAccessDeniedException ex)
        {
            return Forbidden(ex);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Remove a member from a group
    /// </summary>
    [HttpDelete("{id:guid}/members/{userId:guid}")]
    public async Task<IActionResult> RemoveMember(Guid id, Guid userId)
    {
        var actor = TryGetActor();
        if (actor == null)
        {
            return Unauthorized();
        }

        try
        {
            var result = await _groupService.RemoveMemberAsync(id, userId, actor);
            if (!result)
            {
                return NotFound(new { error = "Membership not found" });
            }

            return Ok(new { message = "Member removed from group" });
        }
        catch (GroupAccessDeniedException ex)
        {
            return Forbidden(ex);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// List members of a group
    /// </summary>
    [HttpGet("{id:guid}/members")]
    public async Task<IActionResult> GetMembers(Guid id, CancellationToken ct = default)
    {
        var (group, denied) = await LoadReadableGroupAsync(id, ct);
        if (group == null)
        {
            return denied!;
        }

        var members = await _groupService.GetMembersAsync(id, ct);

        return Ok(members.Select(m => new
        {
            id = m.Id,
            userId = m.UserId,
            userName = m.User != null ? $"{m.User.FirstName} {m.User.LastName}" : null,
            userEmail = m.User?.Email,
            role = m.Role,
            joinedAt = m.JoinedAt,
            expiresAt = m.ExpiresAt,
            isActive = m.IsActive
        }));
    }

    /// <summary>
    /// Get current user's groups
    /// </summary>
    [HttpGet("my-groups")]
    public async Task<IActionResult> GetMyGroups()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null)
        {
            return Unauthorized();
        }

        var groups = await _groupService.GetUserGroupsAsync(Guid.Parse(userId));

        return Ok(groups.Select(g => new
        {
            id = g.Id,
            name = g.Name,
            description = g.Description,
            tenantId = g.TenantId,
            tenantName = g.Tenant?.Name,
            groupType = g.GroupType,
            isActive = g.IsActive,
            createdAt = g.CreatedAt
        }));
    }

    /// <summary>
    /// Assign a role to a group
    /// </summary>
    [HttpPost("{id:guid}/roles")]
    public async Task<IActionResult> AssignRole(Guid id, [FromBody] GroupAssignRoleRequest request)
    {
        if (request.RoleId == Guid.Empty)
        {
            return BadRequest(new { error = "Role ID is required" });
        }

        if (request.TenantId == Guid.Empty)
        {
            return BadRequest(new { error = "Tenant ID is required" });
        }

        var actor = TryGetActor();
        if (actor == null)
        {
            return Unauthorized();
        }

        try
        {
            var groupRole = await _groupService.AssignRoleToGroupAsync(
                id,
                request.RoleId,
                request.TenantId,
                actor);

            return Ok(new
            {
                id = groupRole.Id,
                groupId = groupRole.GroupId,
                roleId = groupRole.RoleId,
                tenantId = groupRole.TenantId,
                assignedByUserId = groupRole.AssignedByUserId,
                assignedAt = groupRole.AssignedAt
            });
        }
        catch (GroupAccessDeniedException ex)
        {
            return Forbidden(ex);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Remove a role from a group
    /// </summary>
    [HttpDelete("{id:guid}/roles/{roleId:guid}")]
    public async Task<IActionResult> RemoveRole(Guid id, Guid roleId)
    {
        var actor = TryGetActor();
        if (actor == null)
        {
            return Unauthorized();
        }

        try
        {
            var result = await _groupService.RemoveRoleFromGroupAsync(id, roleId, actor);
            if (!result)
            {
                return NotFound(new { error = "Role assignment not found" });
            }

            return Ok(new { message = "Role removed from group" });
        }
        catch (GroupAccessDeniedException ex)
        {
            return Forbidden(ex);
        }
    }

    /// <summary>
    /// Groups without an active owner (backfill promotes nobody, so legacy groups may be
    /// ownerless; global admins adopt them from here). Task 4056.
    /// </summary>
    [HttpGet("ownerless")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<IActionResult> GetOwnerlessGroups()
    {
        var groups = await _groupService.GetGroupsWithoutActiveOwnerAsync();

        return Ok(groups.Select(g => new
        {
            id = g.Id,
            name = g.Name,
            description = g.Description,
            tenantId = g.TenantId,
            tenantName = g.Tenant?.Name,
            groupType = g.GroupType,
            isActive = g.IsActive,
            createdAt = g.CreatedAt
        }));
    }

    /// <summary>
    /// Get effective permissions for a user through group memberships
    /// </summary>
    [HttpGet("{id:guid}/effective-permissions")]
    public async Task<IActionResult> GetEffectivePermissions(Guid id, [FromQuery] Guid tenantId, CancellationToken ct = default)
    {
        // Authorize first so a caller without access cannot tell a missing parameter from a forbidden one's data.
        // Allowed: the user themselves, an admin of that tenant, SuperAdmin. Device/service tokens: never.
        var access = await _access.ResolveAsync(User, ct);
        var subject = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        var isSelf = !User.IsApiKey() && Guid.TryParse(subject, out var callerId) && callerId == id;
        if (access.IsRefused || !(isSelf || (tenantId != Guid.Empty && access.CanManage(tenantId)) || access.AllTenants))
        {
            return ForbiddenRead();
        }

        if (tenantId == Guid.Empty)
        {
            return BadRequest(new { error = "Tenant ID query parameter is required" });
        }

        // The id parameter here represents the user ID for which to calculate permissions
        var permissions = await _groupService.GetEffectivePermissionsAsync(id, tenantId, ct);

        return Ok(new
        {
            userId = id,
            tenantId,
            permissions,
            permissionCount = permissions.Count
        });
    }
}

public record CreateGroupRequest(
    string Name,
    string? Description,
    Guid TenantId,
    Guid? ParentGroupId,
    string? GroupType,
    Dictionary<string, object>? Metadata
);

public record UpdateGroupRequest(
    string? Name,
    string? Description,
    Dictionary<string, object>? Metadata
);

public record AddMemberRequest(
    Guid UserId,
    string? Role,
    DateTime? ExpiresAt
);

public record GroupAssignRoleRequest(
    Guid RoleId,
    Guid TenantId
);
