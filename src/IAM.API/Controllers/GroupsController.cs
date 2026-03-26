using IAM.Core.Entities;
using IAM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.Text.Json;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class GroupsController : ControllerBase
{
    private readonly IGroupService _groupService;

    public GroupsController(IGroupService groupService)
    {
        _groupService = groupService;
    }

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
            var created = await _groupService.CreateGroupAsync(group);

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
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get group by ID with members and roles
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetGroup(Guid id)
    {
        var group = await _groupService.GetGroupAsync(id);
        if (group == null)
        {
            return NotFound(new { error = "Group not found" });
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
    public async Task<IActionResult> GetGroupsByTenant(Guid tenantId)
    {
        var groups = await _groupService.GetGroupsByTenantAsync(tenantId);

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
        var metadataJson = request.Metadata != null
            ? JsonSerializer.Serialize(request.Metadata)
            : null;

        try
        {
            var group = await _groupService.UpdateGroupAsync(id, request.Name ?? string.Empty, request.Description, metadataJson);

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
        try
        {
            var result = await _groupService.DeleteGroupAsync(id);
            if (!result)
            {
                return NotFound(new { error = "Group not found" });
            }

            return Ok(new { message = "Group deleted successfully" });
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

        try
        {
            var membership = await _groupService.AddMemberAsync(
                id,
                request.UserId,
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
        var result = await _groupService.RemoveMemberAsync(id, userId);
        if (!result)
        {
            return NotFound(new { error = "Membership not found" });
        }

        return Ok(new { message = "Member removed from group" });
    }

    /// <summary>
    /// List members of a group
    /// </summary>
    [HttpGet("{id:guid}/members")]
    public async Task<IActionResult> GetMembers(Guid id)
    {
        var members = await _groupService.GetMembersAsync(id);

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

        // Get the current user's ID for audit trail
        var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        Guid? assignedByUserId = currentUserId != null ? Guid.Parse(currentUserId) : null;

        try
        {
            var groupRole = await _groupService.AssignRoleToGroupAsync(
                id,
                request.RoleId,
                request.TenantId,
                assignedByUserId);

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
        var result = await _groupService.RemoveRoleFromGroupAsync(id, roleId);
        if (!result)
        {
            return NotFound(new { error = "Role assignment not found" });
        }

        return Ok(new { message = "Role removed from group" });
    }

    /// <summary>
    /// Get effective permissions for a user through group memberships
    /// </summary>
    [HttpGet("{id:guid}/effective-permissions")]
    public async Task<IActionResult> GetEffectivePermissions(Guid id, [FromQuery] Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            return BadRequest(new { error = "Tenant ID query parameter is required" });
        }

        // The id parameter here represents the user ID for which to calculate permissions
        var permissions = await _groupService.GetEffectivePermissionsAsync(id, tenantId);

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
