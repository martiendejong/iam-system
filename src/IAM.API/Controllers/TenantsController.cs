using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.Json;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class TenantsController : ControllerBase
{
    private readonly IAMDbContext _context;

    public TenantsController(IAMDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// List all tenants (hierarchical view)
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> ListTenants(
        [FromQuery] Guid? parentId = null,
        [FromQuery] string? type = null)
    {
        var query = _context.Tenants
            .Include(t => t.ParentTenant)
            .Include(t => t.ChildTenants)
            .AsQueryable();

        // Filter by parent
        if (parentId.HasValue)
        {
            query = query.Where(t => t.ParentTenantId == parentId.Value);
        }
        else if (parentId == null && !Request.Query.ContainsKey("parentId"))
        {
            // If parentId not specified at all, return only root tenants
            query = query.Where(t => t.ParentTenantId == null);
        }

        // Filter by type
        if (!string.IsNullOrWhiteSpace(type))
        {
            query = query.Where(t => t.Type == type);
        }

        var tenants = await query
            .OrderBy(t => t.Type)
            .ThenBy(t => t.Name)
            .ToListAsync();

        return Ok(tenants.Select(t => new
        {
            id = t.Id,
            name = t.Name,
            type = t.Type,
            parentTenantId = t.ParentTenantId,
            parentTenantName = t.ParentTenant?.Name,
            childCount = t.ChildTenants.Count,
            metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(t.Metadata ?? "{}"),
            settings = JsonSerializer.Deserialize<Dictionary<string, object>>(t.Settings ?? "{}"),
            isActive = t.IsActive,
            createdAt = t.CreatedAt
        }));
    }

    /// <summary>
    /// Get tenant hierarchy (tenant with all children recursively)
    /// </summary>
    [HttpGet("{id}/hierarchy")]
    public async Task<IActionResult> GetTenantHierarchy(Guid id)
    {
        var tenant = await _context.Tenants
            .Include(t => t.ParentTenant)
            .Include(t => t.ChildTenants)
                .ThenInclude(c => c.ChildTenants)
                    .ThenInclude(c => c.ChildTenants) // Up to 3 levels deep
            .FirstOrDefaultAsync(t => t.Id == id);

        if (tenant == null)
        {
            return NotFound();
        }

        return Ok(BuildTenantHierarchy(tenant));
    }

    private object BuildTenantHierarchy(Tenant tenant)
    {
        return new
        {
            id = tenant.Id,
            name = tenant.Name,
            type = tenant.Type,
            parentTenantId = tenant.ParentTenantId,
            metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(tenant.Metadata ?? "{}"),
            settings = JsonSerializer.Deserialize<Dictionary<string, object>>(tenant.Settings ?? "{}"),
            isActive = tenant.IsActive,
            createdAt = tenant.CreatedAt,
            children = tenant.ChildTenants.Select(c => BuildTenantHierarchy(c))
        };
    }

    /// <summary>
    /// Get specific tenant by ID
    /// </summary>
    [HttpGet("{id}")]
    public async Task<IActionResult> GetTenant(Guid id)
    {
        var tenant = await _context.Tenants
            .Include(t => t.ParentTenant)
            .Include(t => t.ChildTenants)
            .FirstOrDefaultAsync(t => t.Id == id);

        if (tenant == null)
        {
            return NotFound();
        }

        return Ok(new
        {
            id = tenant.Id,
            name = tenant.Name,
            type = tenant.Type,
            parentTenantId = tenant.ParentTenantId,
            parentTenantName = tenant.ParentTenant?.Name,
            childCount = tenant.ChildTenants.Count,
            metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(tenant.Metadata ?? "{}"),
            settings = JsonSerializer.Deserialize<Dictionary<string, object>>(tenant.Settings ?? "{}"),
            isActive = tenant.IsActive,
            createdAt = tenant.CreatedAt,
            updatedAt = tenant.UpdatedAt
        });
    }

    /// <summary>
    /// Create new tenant (Building, Floor, Room, etc.)
    /// </summary>
    [HttpPost]
    [Authorize(Roles = "SuperAdmin,BuildingOwner")]
    public async Task<IActionResult> CreateTenant([FromBody] CreateTenantRequest request)
    {
        // Validate
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { error = "Tenant name is required" });
        }

        if (string.IsNullOrWhiteSpace(request.Type))
        {
            return BadRequest(new { error = "Tenant type is required" });
        }

        // Validate parent exists if specified
        if (request.ParentTenantId.HasValue)
        {
            var parentExists = await _context.Tenants.AnyAsync(t => t.Id == request.ParentTenantId.Value);
            if (!parentExists)
            {
                return BadRequest(new { error = "Parent tenant not found" });
            }
        }

        // Serialize metadata and settings
        var metadataJson = JsonSerializer.Serialize(request.Metadata ?? new Dictionary<string, object>());
        var settingsJson = JsonSerializer.Serialize(request.Settings ?? new Dictionary<string, object>());

        var tenant = new Tenant
        {
            Name = request.Name,
            Type = request.Type,
            ParentTenantId = request.ParentTenantId,
            Metadata = metadataJson,
            Settings = settingsJson,
            IsActive = true
        };

        _context.Tenants.Add(tenant);
        await _context.SaveChangesAsync();

        return CreatedAtAction(
            nameof(GetTenant),
            new { id = tenant.Id },
            new
            {
                id = tenant.Id,
                name = tenant.Name,
                type = tenant.Type,
                parentTenantId = tenant.ParentTenantId,
                metadata = request.Metadata,
                settings = request.Settings,
                isActive = tenant.IsActive,
                createdAt = tenant.CreatedAt
            });
    }

    /// <summary>
    /// Update tenant
    /// </summary>
    [HttpPut("{id}")]
    [Authorize(Roles = "SuperAdmin,BuildingOwner,BuildingManager")]
    public async Task<IActionResult> UpdateTenant(Guid id, [FromBody] UpdateTenantRequest request)
    {
        var tenant = await _context.Tenants.FirstOrDefaultAsync(t => t.Id == id);

        if (tenant == null)
        {
            return NotFound();
        }

        // Update fields
        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            tenant.Name = request.Name;
        }

        if (!string.IsNullOrWhiteSpace(request.Type))
        {
            tenant.Type = request.Type;
        }

        if (request.Metadata != null)
        {
            tenant.Metadata = JsonSerializer.Serialize(request.Metadata);
        }

        if (request.Settings != null)
        {
            tenant.Settings = JsonSerializer.Serialize(request.Settings);
        }

        if (request.IsActive.HasValue)
        {
            tenant.IsActive = request.IsActive.Value;
        }

        tenant.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return Ok(new
        {
            id = tenant.Id,
            name = tenant.Name,
            type = tenant.Type,
            metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(tenant.Metadata ?? "{}"),
            settings = JsonSerializer.Deserialize<Dictionary<string, object>>(tenant.Settings ?? "{}"),
            isActive = tenant.IsActive,
            updatedAt = tenant.UpdatedAt
        });
    }

    /// <summary>
    /// Delete tenant (only if no children)
    /// </summary>
    [HttpDelete("{id}")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<IActionResult> DeleteTenant(Guid id)
    {
        var tenant = await _context.Tenants
            .Include(t => t.ChildTenants)
            .FirstOrDefaultAsync(t => t.Id == id);

        if (tenant == null)
        {
            return NotFound();
        }

        if (tenant.ChildTenants.Any())
        {
            return BadRequest(new
            {
                error = "Cannot delete tenant with child tenants",
                childCount = tenant.ChildTenants.Count
            });
        }

        // Check if tenant has role assignments
        var hasRoleAssignments = await _context.UserRoles.AnyAsync(ur => ur.TenantId == id);
        if (hasRoleAssignments)
        {
            return BadRequest(new
            {
                error = "Cannot delete tenant with active role assignments. Deactivate instead."
            });
        }

        _context.Tenants.Remove(tenant);
        await _context.SaveChangesAsync();

        return Ok(new { message = "Tenant deleted successfully" });
    }

    /// <summary>
    /// Get building structure (Building → Floors → Rooms)
    /// For building management UI
    /// </summary>
    [HttpGet("buildings/{buildingId}/structure")]
    public async Task<IActionResult> GetBuildingStructure(Guid buildingId)
    {
        var building = await _context.Tenants
            .Include(t => t.ChildTenants) // Floors
                .ThenInclude(f => f.ChildTenants) // Rooms
                    .ThenInclude(r => r.ChildTenants) // Devices/Zones
            .FirstOrDefaultAsync(t => t.Id == buildingId && t.Type == "Building");

        if (building == null)
        {
            return NotFound(new { error = "Building not found" });
        }

        return Ok(new
        {
            building = new
            {
                id = building.Id,
                name = building.Name,
                metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(building.Metadata ?? "{}"),
                floors = building.ChildTenants
                    .Where(f => f.Type == "Floor")
                    .OrderBy(f => f.Name)
                    .Select(floor => new
                    {
                        id = floor.Id,
                        name = floor.Name,
                        metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(floor.Metadata ?? "{}"),
                        rooms = floor.ChildTenants
                            .Where(r => r.Type == "Room")
                            .OrderBy(r => r.Name)
                            .Select(room => new
                            {
                                id = room.Id,
                                name = room.Name,
                                metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(room.Metadata ?? "{}"),
                                deviceCount = room.ChildTenants.Count(d => d.Type == "Device")
                            })
                    })
            }
        });
    }

    /// <summary>
    /// Get user's accessible tenants (based on role assignments)
    /// </summary>
    [HttpGet("my-tenants")]
    public async Task<IActionResult> GetMyTenants()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null)
        {
            return Unauthorized();
        }

        var tenantIds = await _context.UserRoles
            .Where(ur => ur.UserId == Guid.Parse(userId) && ur.TenantId != null)
            .Select(ur => ur.TenantId!.Value)
            .Distinct()
            .ToListAsync();

        var tenants = await _context.Tenants
            .Where(t => tenantIds.Contains(t.Id))
            .OrderBy(t => t.Type)
            .ThenBy(t => t.Name)
            .ToListAsync();

        return Ok(tenants.Select(t => new
        {
            id = t.Id,
            name = t.Name,
            type = t.Type,
            parentTenantId = t.ParentTenantId,
            isActive = t.IsActive
        }));
    }

    /// <summary>
    /// List members of a tenant (users with at least one role scoped to this tenant)
    /// </summary>
    [HttpGet("{id}/members")]
    public async Task<IActionResult> GetMembers(Guid id)
    {
        var tenantExists = await _context.Tenants.AnyAsync(t => t.Id == id);
        if (!tenantExists)
        {
            return NotFound(new { error = "Tenant not found" });
        }

        var userRoles = await _context.UserRoles
            .Include(ur => ur.User)
            .Include(ur => ur.Role)
            .Where(ur => ur.TenantId == id)
            .OrderBy(ur => ur.User.Email)
            .ToListAsync();

        var members = userRoles
            .GroupBy(ur => ur.User)
            .Select(g => new
            {
                userId = g.Key.Id,
                email = g.Key.Email,
                firstName = g.Key.FirstName,
                lastName = g.Key.LastName,
                isActive = g.Key.IsActive,
                joinedAt = g.Min(ur => ur.GrantedAt),
                roles = g.Select(ur => new
                {
                    roleId = ur.RoleId,
                    roleName = ur.Role.Name,
                    grantedAt = ur.GrantedAt,
                    expiresAt = ur.ExpiresAt
                })
            });

        return Ok(members);
    }

    /// <summary>
    /// Replace a member's role(s) within this tenant with a single new role
    /// </summary>
    [HttpPut("{id}/members/{userId}/role")]
    public async Task<IActionResult> ChangeMemberRole(Guid id, Guid userId, [FromBody] ChangeMemberRoleRequest request)
    {
        var role = await _context.Roles.FirstOrDefaultAsync(r => r.Id == request.RoleId);
        if (role == null)
        {
            return NotFound(new { error = "Role not found" });
        }

        var existingRoles = await _context.UserRoles
            .Where(ur => ur.UserId == userId && ur.TenantId == id)
            .ToListAsync();

        if (existingRoles.Count == 0)
        {
            return NotFound(new { error = "User is not a member of this tenant" });
        }

        var currentUserId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        _context.UserRoles.RemoveRange(existingRoles);
        _context.UserRoles.Add(new UserRole
        {
            UserId = userId,
            RoleId = request.RoleId,
            TenantId = id,
            GrantedBy = currentUserId,
            GrantedAt = DateTime.UtcNow
        });

        await _context.SaveChangesAsync();

        return Ok(new { message = "Member role updated", roleId = request.RoleId, roleName = role.Name });
    }

    /// <summary>
    /// Remove a member from this tenant (revokes all of their tenant-scoped roles)
    /// </summary>
    [HttpDelete("{id}/members/{userId}")]
    public async Task<IActionResult> RemoveMember(Guid id, Guid userId)
    {
        var existingRoles = await _context.UserRoles
            .Where(ur => ur.UserId == userId && ur.TenantId == id)
            .ToListAsync();

        if (existingRoles.Count == 0)
        {
            return NotFound(new { error = "User is not a member of this tenant" });
        }

        _context.UserRoles.RemoveRange(existingRoles);
        await _context.SaveChangesAsync();

        return Ok(new { message = "Member removed from tenant" });
    }
}

public record CreateTenantRequest(
    string Name,
    string Type, // Building, Floor, Room, Device, Organization, etc.
    Guid? ParentTenantId,
    Dictionary<string, object>? Metadata,
    Dictionary<string, object>? Settings
);

public record UpdateTenantRequest(
    string? Name,
    string? Type,
    Dictionary<string, object>? Metadata,
    Dictionary<string, object>? Settings,
    bool? IsActive
);

public record ChangeMemberRoleRequest(Guid RoleId);
