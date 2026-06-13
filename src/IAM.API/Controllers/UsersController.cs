using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize] // All endpoints require authentication
public class UsersController : ControllerBase
{
    private readonly IAMDbContext _context;

    public UsersController(IAMDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Get current user's profile
    /// </summary>
    [HttpGet("me")]
    public async Task<IActionResult> GetCurrentUser()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null)
        {
            return Unauthorized();
        }

        var user = await _context.Users
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Id == Guid.Parse(userId));

        if (user == null)
        {
            return NotFound();
        }

        return Ok(new
        {
            id = user.Id,
            email = user.Email,
            firstName = user.FirstName,
            lastName = user.LastName,
            emailConfirmed = user.EmailConfirmed,
            twoFactorEnabled = user.TwoFactorEnabled,
            isActive = user.IsActive,
            createdAt = user.CreatedAt,
            roles = user.UserRoles.Select(ur => new
            {
                id = ur.Role.Id,
                name = ur.Role.Name,
                tenantId = ur.TenantId,
                expiresAt = ur.ExpiresAt
            })
        });
    }

    /// <summary>
    /// Update current user's profile
    /// </summary>
    [HttpPut("me")]
    public async Task<IActionResult> UpdateCurrentUser([FromBody] UpdateProfileRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null)
        {
            return Unauthorized();
        }

        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == Guid.Parse(userId));
        if (user == null)
        {
            return NotFound();
        }

        // Update allowed fields
        if (!string.IsNullOrWhiteSpace(request.FirstName))
        {
            user.FirstName = request.FirstName;
        }

        if (!string.IsNullOrWhiteSpace(request.LastName))
        {
            user.LastName = request.LastName;
        }

        user.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return Ok(new
        {
            id = user.Id,
            email = user.Email,
            firstName = user.FirstName,
            lastName = user.LastName
        });
    }

    /// <summary>
    /// Update any user's profile (SuperAdmin only)
    /// </summary>
    [HttpPut("{id}")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<IActionResult> UpdateUser(Guid id, [FromBody] AdminUpdateUserRequest request)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user == null)
        {
            return NotFound();
        }

        if (!string.IsNullOrWhiteSpace(request.FirstName))
            user.FirstName = request.FirstName;
        if (!string.IsNullOrWhiteSpace(request.LastName))
            user.LastName = request.LastName;
        if (!string.IsNullOrWhiteSpace(request.Email))
            user.Email = request.Email;
        if (!string.IsNullOrWhiteSpace(request.PhoneNumber))
            user.PhoneNumber = request.PhoneNumber;

        user.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return Ok(new
        {
            id = user.Id,
            email = user.Email,
            firstName = user.FirstName,
            lastName = user.LastName
        });
    }

    /// <summary>
    /// List all users (SuperAdmin only)
    /// </summary>
    [HttpGet]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<IActionResult> ListUsers([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        var totalCount = await _context.Users.CountAsync();
        var users = await _context.Users
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
            .OrderBy(u => u.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return Ok(new
        {
            totalCount,
            page,
            pageSize,
            totalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
            items = users.Select(u => new
            {
                id = u.Id,
                email = u.Email,
                firstName = u.FirstName,
                lastName = u.LastName,
                emailConfirmed = u.EmailConfirmed,
                isActive = u.IsActive,
                createdAt = u.CreatedAt,
                lastLoginAt = u.LastLoginAt,
                roles = u.UserRoles.Select(ur => ur.Role.Name)
            })
        });
    }

    /// <summary>
    /// Get specific user by ID (SuperAdmin or BuildingOwner/BuildingManager for their tenants)
    /// </summary>
    [HttpGet("{id}")]
    public async Task<IActionResult> GetUser(Guid id)
    {
        var user = await _context.Users
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Id == id);

        if (user == null)
        {
            return NotFound();
        }

        // Authorization check
        var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var isSuperAdmin = User.IsInRole("SuperAdmin");
        var isOwnProfile = currentUserId == id.ToString();

        if (!isSuperAdmin && !isOwnProfile)
        {
            // TODO: Add tenant-based authorization for BuildingOwner/BuildingManager
            return Forbid();
        }

        return Ok(new
        {
            id = user.Id,
            email = user.Email,
            firstName = user.FirstName,
            lastName = user.LastName,
            emailConfirmed = user.EmailConfirmed,
            twoFactorEnabled = user.TwoFactorEnabled,
            isActive = user.IsActive,
            createdAt = user.CreatedAt,
            lastLoginAt = user.LastLoginAt,
            roles = user.UserRoles.Select(ur => new
            {
                id = ur.Role.Id,
                name = ur.Role.Name,
                tenantId = ur.TenantId,
                grantedBy = ur.GrantedBy,
                grantedAt = ur.GrantedAt,
                expiresAt = ur.ExpiresAt
            })
        });
    }

    /// <summary>
    /// Deactivate user (SuperAdmin only)
    /// </summary>
    [HttpPost("{id}/deactivate")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<IActionResult> DeactivateUser(Guid id)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user == null)
        {
            return NotFound();
        }

        user.IsActive = false;
        user.UpdatedAt = DateTime.UtcNow;

        // Revoke all refresh tokens
        var tokens = await _context.RefreshTokens
            .Where(rt => rt.UserId == id && rt.RevokedAt == null)
            .ToListAsync();

        foreach (var token in tokens)
        {
            token.RevokedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync();

        return Ok(new { message = "User deactivated successfully" });
    }

    /// <summary>
    /// Reactivate user (SuperAdmin only)
    /// </summary>
    [HttpPost("{id}/activate")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<IActionResult> ActivateUser(Guid id)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user == null)
        {
            return NotFound();
        }

        user.IsActive = true;
        user.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return Ok(new { message = "User activated successfully" });
    }

    /// <summary>
    /// Assign role to user (SuperAdmin only)
    /// </summary>
    [HttpPost("{userId}/roles")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<IActionResult> AssignRole(Guid userId, [FromBody] AssignRoleRequest request)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null)
        {
            return NotFound(new { error = "User not found" });
        }

        var role = await _context.Roles.FirstOrDefaultAsync(r => r.Id == request.RoleId);
        if (role == null)
        {
            return NotFound(new { error = "Role not found" });
        }

        // Check if role assignment already exists
        var existingAssignment = await _context.UserRoles
            .FirstOrDefaultAsync(ur => ur.UserId == userId && ur.RoleId == request.RoleId);

        if (existingAssignment != null)
        {
            return BadRequest(new { error = "User already has this role" });
        }

        var currentUserId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        var userRole = new UserRole
        {
            UserId = userId,
            RoleId = request.RoleId,
            TenantId = request.TenantId,
            GrantedBy = currentUserId,
            GrantedAt = DateTime.UtcNow,
            ExpiresAt = request.ExpiresAt
        };

        _context.UserRoles.Add(userRole);
        await _context.SaveChangesAsync();

        return Ok(new
        {
            message = "Role assigned successfully",
            userRole = new
            {
                userId = userRole.UserId,
                roleId = userRole.RoleId,
                roleName = role.Name,
                tenantId = userRole.TenantId,
                grantedBy = userRole.GrantedBy,
                grantedAt = userRole.GrantedAt,
                expiresAt = userRole.ExpiresAt
            }
        });
    }

    /// <summary>
    /// Remove role from user (SuperAdmin only)
    /// </summary>
    [HttpDelete("{userId}/roles/{roleId}")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<IActionResult> RemoveRole(Guid userId, Guid roleId)
    {
        var userRole = await _context.UserRoles
            .FirstOrDefaultAsync(ur => ur.UserId == userId && ur.RoleId == roleId);

        if (userRole == null)
        {
            return NotFound(new { error = "Role assignment not found" });
        }

        _context.UserRoles.Remove(userRole);
        await _context.SaveChangesAsync();

        return Ok(new { message = "Role removed successfully" });
    }
}

public record UpdateProfileRequest(string? FirstName, string? LastName);
public record AssignRoleRequest(Guid RoleId, Guid? TenantId, DateTime? ExpiresAt);
public record AdminUpdateUserRequest(string? FirstName, string? LastName, string? Email, string? PhoneNumber);
