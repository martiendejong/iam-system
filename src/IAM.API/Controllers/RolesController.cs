using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize] // All endpoints require authentication
public class RolesController : ControllerBase
{
    private readonly IAMDbContext _context;

    public RolesController(IAMDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// List all roles (authenticated users can view roles)
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> ListRoles()
    {
        var roles = await _context.Roles
            .OrderBy(r => r.IsSystemRole ? 0 : 1) // System roles first
            .ThenBy(r => r.Name)
            .ToListAsync();

        return Ok(roles.Select(r => new
        {
            id = r.Id,
            name = r.Name,
            description = r.Description,
            isSystemRole = r.IsSystemRole,
            tenantId = r.TenantId,
            permissions = JsonSerializer.Deserialize<string[]>(r.Permissions ?? "[]"),
            createdAt = r.CreatedAt
        }));
    }

    /// <summary>
    /// Get specific role by ID
    /// </summary>
    [HttpGet("{id}")]
    public async Task<IActionResult> GetRole(Guid id)
    {
        var role = await _context.Roles
            .Include(r => r.UserRoles)
                .ThenInclude(ur => ur.User)
            .FirstOrDefaultAsync(r => r.Id == id);

        if (role == null)
        {
            return NotFound();
        }

        return Ok(new
        {
            id = role.Id,
            name = role.Name,
            description = role.Description,
            isSystemRole = role.IsSystemRole,
            tenantId = role.TenantId,
            permissions = JsonSerializer.Deserialize<string[]>(role.Permissions ?? "[]"),
            createdAt = role.CreatedAt,
            userCount = role.UserRoles.Count,
            users = role.UserRoles.Take(10).Select(ur => new
            {
                id = ur.User.Id,
                email = ur.User.Email,
                firstName = ur.User.FirstName,
                lastName = ur.User.LastName
            })
        });
    }

    /// <summary>
    /// Create new custom role (SuperAdmin or BuildingOwner only)
    /// </summary>
    [HttpPost]
    [Authorize(Roles = "SuperAdmin,BuildingOwner")]
    public async Task<IActionResult> CreateRole([FromBody] CreateRoleRequest request)
    {
        // Validate
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { error = "Role name is required" });
        }

        // Check for duplicate name
        var existingRole = await _context.Roles
            .FirstOrDefaultAsync(r => r.Name == request.Name && r.TenantId == request.TenantId);

        if (existingRole != null)
        {
            return BadRequest(new { error = "A role with this name already exists" });
        }

        // Serialize permissions
        var permissionsJson = JsonSerializer.Serialize(request.Permissions ?? Array.Empty<string>());

        var role = new Role
        {
            Name = request.Name,
            Description = request.Description ?? string.Empty,
            IsSystemRole = false, // Custom roles are never system roles
            TenantId = request.TenantId,
            Permissions = permissionsJson
        };

        _context.Roles.Add(role);
        await _context.SaveChangesAsync();

        return CreatedAtAction(
            nameof(GetRole),
            new { id = role.Id },
            new
            {
                id = role.Id,
                name = role.Name,
                description = role.Description,
                isSystemRole = role.IsSystemRole,
                tenantId = role.TenantId,
                permissions = request.Permissions,
                createdAt = role.CreatedAt
            });
    }

    /// <summary>
    /// Update custom role (SuperAdmin only, system roles cannot be modified)
    /// </summary>
    [HttpPut("{id}")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<IActionResult> UpdateRole(Guid id, [FromBody] UpdateRoleRequest request)
    {
        var role = await _context.Roles.FirstOrDefaultAsync(r => r.Id == id);

        if (role == null)
        {
            return NotFound();
        }

        if (role.IsSystemRole)
        {
            return BadRequest(new { error = "System roles cannot be modified" });
        }

        // Update fields
        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            // Check for duplicate name
            var existingRole = await _context.Roles
                .FirstOrDefaultAsync(r => r.Name == request.Name && r.Id != id);

            if (existingRole != null)
            {
                return BadRequest(new { error = "A role with this name already exists" });
            }

            role.Name = request.Name;
        }

        if (request.Description != null)
        {
            role.Description = request.Description;
        }

        if (request.Permissions != null)
        {
            role.Permissions = JsonSerializer.Serialize(request.Permissions);
        }

        role.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return Ok(new
        {
            id = role.Id,
            name = role.Name,
            description = role.Description,
            permissions = JsonSerializer.Deserialize<string[]>(role.Permissions ?? "[]"),
            updatedAt = role.UpdatedAt
        });
    }

    /// <summary>
    /// Delete custom role (SuperAdmin only, system roles cannot be deleted)
    /// </summary>
    [HttpDelete("{id}")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<IActionResult> DeleteRole(Guid id)
    {
        var role = await _context.Roles
            .Include(r => r.UserRoles)
            .FirstOrDefaultAsync(r => r.Id == id);

        if (role == null)
        {
            return NotFound();
        }

        if (role.IsSystemRole)
        {
            return BadRequest(new { error = "System roles cannot be deleted" });
        }

        if (role.UserRoles.Any())
        {
            return BadRequest(new
            {
                error = "Cannot delete role that is assigned to users",
                userCount = role.UserRoles.Count
            });
        }

        _context.Roles.Remove(role);
        await _context.SaveChangesAsync();

        return Ok(new { message = "Role deleted successfully" });
    }

    /// <summary>
    /// Get all available permissions (for UI permission pickers)
    /// </summary>
    [HttpGet("permissions")]
    [Authorize(Roles = "SuperAdmin,BuildingOwner")]
    public IActionResult GetAvailablePermissions()
    {
        // These would typically come from a configuration file or database
        // For now, return a static list of common permissions
        var permissions = new
        {
            users = new[]
            {
                "users.read",
                "users.create",
                "users.update",
                "users.delete",
                "users.roles.assign",
                "users.roles.remove"
            },
            roles = new[]
            {
                "roles.read",
                "roles.create",
                "roles.update",
                "roles.delete"
            },
            tenants = new[]
            {
                "tenants.read",
                "tenants.create",
                "tenants.update",
                "tenants.delete"
            },
            buildings = new[]
            {
                "buildings.read",
                "buildings.create",
                "buildings.update",
                "buildings.delete",
                "buildings.manage"
            },
            devices = new[]
            {
                "devices.read",
                "devices.create",
                "devices.update",
                "devices.delete",
                "devices.control"
            },
            analytics = new[]
            {
                "analytics.read",
                "analytics.export"
            },
            settings = new[]
            {
                "settings.read",
                "settings.update"
            }
        };

        return Ok(permissions);
    }
}

public record CreateRoleRequest(
    string Name,
    string? Description,
    Guid? TenantId,
    string[]? Permissions
);

public record UpdateRoleRequest(
    string? Name,
    string? Description,
    string[]? Permissions
);
