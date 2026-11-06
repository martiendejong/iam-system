using IAM.API.Authorization;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.Json;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize] // All endpoints require authentication
public class RolesController : ControllerBase
{
    /// <summary>Platform roles that may read every role and its holders (SuperAdmin is covered by the tenant resolver).</summary>
    private static readonly string[] PlatformReaderRoles = { "SuperAdmin", "SystemAdmin", "SecurityAdmin" };

    private readonly IAMDbContext _context;
    private readonly ITenantAccessResolver _tenantAccess;

    public RolesController(IAMDbContext context, ITenantAccessResolver tenantAccess)
    {
        _context = context;
        _tenantAccess = tenantAccess;
    }

    /// <summary>
    /// What the caller may read of the role catalog (task 5163): nothing (not an administrator), everything (platform
    /// administrators), or the global roles plus the roles of the tenants the caller administers. Holders (names and
    /// e-mails) follow the same tenants, so a tenant administrator never learns who holds a platform role.
    /// </summary>
    private sealed record RoleReadScope(bool IsAdmin, IReadOnlyCollection<Guid>? Tenants)
    {
        public static RoleReadScope None { get; } = new(false, Array.Empty<Guid>());
        public static RoleReadScope Everything { get; } = new(true, null);

        public bool CanSee(Role role) =>
            IsAdmin && (Tenants == null || role.TenantId == null || Tenants.Contains(role.TenantId.Value));
    }

    private async Task<RoleReadScope> ResolveReadScopeAsync(CancellationToken cancellationToken)
    {
        // Device and service-account tokens are refused by the resolver; API keys are scoped by the resolver too.
        var access = await _tenantAccess.ResolveAsync(User, cancellationToken);
        if (access.IsRefused)
            return RoleReadScope.None;

        if (access.AllTenants || PlatformReaderRoles.Any(User.IsInRole))
            return RoleReadScope.Everything;

        var tenants = access.ManageableTenants;
        return tenants == null || tenants.Count == 0 ? RoleReadScope.None : new RoleReadScope(true, tenants);
    }

    private ObjectResult AdministratorsOnly() =>
        StatusCode(StatusCodes.Status403Forbidden, new { error = "Role information is only available to administrators" });

    /// <summary>
    /// The tenants where the caller holds an active BuildingOwner role, from their own tenant roles (never from the
    /// request). A tenant_id claim pins a token to that one tenant, like everywhere else.
    /// </summary>
    private async Task<IReadOnlySet<Guid>> OwnedTenantIdsAsync(CancellationToken cancellationToken)
    {
        var none = new HashSet<Guid>();
        if (User.FindFirst(ServiceAccountAuthorization.TokenTypeClaim) != null)
            return none;

        var subject = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (!Guid.TryParse(subject, out var userId))
            return none;

        var now = DateTime.UtcNow;
        var owned = await _context.UserRoles
            .AsNoTracking()
            .Where(ur => ur.UserId == userId
                && ur.TenantId != null
                && (ur.ExpiresAt == null || ur.ExpiresAt > now)
                && ur.Role.Name == "BuildingOwner")
            .Select(ur => ur.TenantId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

        var tenantClaim = User.FindFirst("tenant_id")?.Value;
        if (!string.IsNullOrWhiteSpace(tenantClaim))
        {
            if (!Guid.TryParse(tenantClaim, out var claimedTenantId))
                return none;

            owned = owned.Where(t => t == claimedTenantId).ToList();
        }

        return owned.ToHashSet();
    }

    /// <summary>
    /// List roles (administrators only: SuperAdmin/SystemAdmin/SecurityAdmin see all, a tenant administrator sees the
    /// global roles and the roles of the tenants they administer)
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> ListRoles(CancellationToken cancellationToken)
    {
        var scope = await ResolveReadScopeAsync(cancellationToken);
        if (!scope.IsAdmin)
        {
            return AdministratorsOnly();
        }

        var query = _context.Roles.AsNoTracking();
        if (scope.Tenants != null)
        {
            var tenantIds = scope.Tenants.ToArray();
            query = query.Where(r => r.TenantId == null || tenantIds.Contains(r.TenantId.Value));
        }

        var roles = await query
            .OrderBy(r => r.IsSystemRole ? 0 : 1) // System roles first
            .ThenBy(r => r.Name)
            .ToListAsync(cancellationToken);

        return Ok(roles.Select(r => new
        {
            id = r.Id,
            name = r.Name,
            description = r.Description,
            category = r.Category,
            isSystemRole = r.IsSystemRole,
            tenantId = r.TenantId,
            permissions = JsonSerializer.Deserialize<string[]>(r.Permissions ?? "[]"),
            createdAt = r.CreatedAt
        }));
    }

    /// <summary>
    /// Get specific role by ID (administrators only; the holders are limited to the tenants the caller administers)
    /// </summary>
    [HttpGet("{id}")]
    public async Task<IActionResult> GetRole(Guid id, CancellationToken cancellationToken)
    {
        // Checked before the lookup so a caller who administers nothing learns nothing about which ids exist.
        var scope = await ResolveReadScopeAsync(cancellationToken);
        if (!scope.IsAdmin)
        {
            return AdministratorsOnly();
        }

        var role = await _context.Roles
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

        // A role of a tenant the caller does not administer looks like a role that does not exist.
        if (role == null || !scope.CanSee(role))
        {
            return NotFound();
        }

        var holders = _context.UserRoles.AsNoTracking().Where(ur => ur.RoleId == id);
        if (scope.Tenants != null)
        {
            var tenantIds = scope.Tenants.ToArray();
            holders = holders.Where(ur => ur.TenantId != null && tenantIds.Contains(ur.TenantId.Value));
        }

        var userCount = await holders.CountAsync(cancellationToken);
        var users = await holders
            .OrderBy(ur => ur.User.Email)
            .Take(10)
            .Select(ur => new
            {
                id = ur.User.Id,
                email = ur.User.Email,
                firstName = ur.User.FirstName,
                lastName = ur.User.LastName
            })
            .ToListAsync(cancellationToken);

        return Ok(new
        {
            id = role.Id,
            name = role.Name,
            description = role.Description,
            category = role.Category,
            isSystemRole = role.IsSystemRole,
            tenantId = role.TenantId,
            permissions = JsonSerializer.Deserialize<string[]>(role.Permissions ?? "[]"),
            createdAt = role.CreatedAt,
            userCount,
            users
        });
    }

    /// <summary>
    /// Create new custom role. A SuperAdmin may create any role; a BuildingOwner only a plain role inside a tenant they
    /// own (no global role, no app category, no wildcard permissions), see <see cref="RoleCreationRules"/>.
    /// </summary>
    [HttpPost]
    [Authorize(Roles = "SuperAdmin,BuildingOwner")]
    public async Task<IActionResult> CreateRole([FromBody] CreateRoleRequest request, CancellationToken cancellationToken)
    {
        // Validate
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { error = "Role name is required" });
        }

        // SuperAdmin also carries the BuildingOwner claim (SuperAdminClaimsTransformation), so ask for SuperAdmin first.
        if (!User.IsInRole("SuperAdmin"))
        {
            var ownedTenants = await OwnedTenantIdsAsync(cancellationToken);
            var problem = RoleCreationRules.CheckForTenantOwner(
                request.Name, request.TenantId, request.Category, request.Permissions, ownedTenants);
            if (problem != RoleCreateProblem.None)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { error = RoleCreationRules.Describe(problem) });
            }
        }

        // Check for duplicate name
        var existingRole = await _context.Roles
            .FirstOrDefaultAsync(r => r.Name == request.Name && r.TenantId == request.TenantId, cancellationToken);

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
            Category = request.Category,
            IsSystemRole = false, // Custom roles are never system roles
            TenantId = request.TenantId,
            Permissions = permissionsJson
        };

        _context.Roles.Add(role);
        await _context.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(
            nameof(GetRole),
            new { id = role.Id },
            new
            {
                id = role.Id,
                name = role.Name,
                description = role.Description,
                category = role.Category,
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

        if (request.Category != null)
        {
            role.Category = request.Category == "" ? null : request.Category;
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
            category = role.Category,
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
    string? Category,
    Guid? TenantId,
    string[]? Permissions
);

public record UpdateRoleRequest(
    string? Name,
    string? Description,
    string? Category,
    string[]? Permissions
);
