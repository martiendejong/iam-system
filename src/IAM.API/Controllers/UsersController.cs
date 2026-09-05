using IAM.API.Authorization;
using IAM.Core.Entities;
using IAM.Core.Services;
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
    private readonly IAuthService _authService;

    public UsersController(IAMDbContext context, IAuthService authService)
    {
        _context = context;
        _authService = authService;
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
    /// Create a new, already-active user with an admin-supplied password. Allowed for
    /// SuperAdmin humans and (task 1496) for service accounts holding the exact
    /// "users:create" permission — TaskManager's Add-Team-Member flow provisions users
    /// server-to-server with its own scoped service credential.
    /// No invitation email is sent; the user can log in immediately with the given password.
    /// </summary>
    [HttpPost]
    [Authorize]
    public async Task<IActionResult> CreateUser([FromBody] AdminCreateUserRequest request)
    {
        if (!User.IsInRole("SuperAdmin")
            && !ServiceAccountAuthorization.HasPermission(User, ServiceAccountAuthorization.UsersCreatePermission))
            return Forbid();

        var email = request.Email?.Trim();
        if (string.IsNullOrWhiteSpace(email))
            return BadRequest(new { error = "Email is required" });

        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 8)
            return BadRequest(new { error = "Password must be at least 8 characters" });

        var emailExists = await _context.Users.AnyAsync(u => u.Email == email);
        if (emailExists)
            return Conflict(new { error = "A user with this email already exists" });

        var user = new User
        {
            Email = email,
            FirstName = request.FirstName?.Trim() ?? string.Empty,
            LastName = request.LastName?.Trim() ?? string.Empty,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            EmailConfirmed = true, // Admin-created accounts are pre-verified (mirrors invitation acceptance)
            IsActive = true
        };

        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetUser), new { id = user.Id }, new
        {
            id = user.Id,
            email = user.Email,
            firstName = user.FirstName,
            lastName = user.LastName,
            emailConfirmed = user.EmailConfirmed,
            isActive = user.IsActive,
            createdAt = user.CreatedAt
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

        // Check if role assignment already exists (scoped to tenant)
        var existingAssignment = await _context.UserRoles
            .FirstOrDefaultAsync(ur => ur.UserId == userId && ur.RoleId == request.RoleId && ur.TenantId == request.TenantId);

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
    /// Resend email verification to an unverified user (SuperAdmin only)
    /// </summary>
    [HttpPost("{id}/resend-verification")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<IActionResult> ResendVerification(Guid id)
    {
        var sent = await _authService.ResendVerificationEmailAsync(id);

        if (!sent)
            return BadRequest(new { error = "User not found or email is already verified" });

        return Ok(new { message = "Verification email sent" });
    }

    /// <summary>
    /// Trigger a password reset email for a user (SuperAdmin only)
    /// </summary>
    [HttpPost("{id}/send-password-reset")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<IActionResult> SendPasswordReset(Guid id)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user == null)
            return NotFound(new { error = "User not found" });

        await _authService.SendPasswordResetAsync(user.Email);

        return Ok(new { message = "Password reset email sent" });
    }

    /// <summary>
    /// Remove role from user (SuperAdmin only)
    /// </summary>
    [HttpDelete("{userId}/roles/{roleId}")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<IActionResult> RemoveRole(Guid userId, Guid roleId, [FromQuery] Guid? tenantId)
    {
        var userRole = await _context.UserRoles
            .FirstOrDefaultAsync(ur => ur.UserId == userId && ur.RoleId == roleId && ur.TenantId == tenantId);

        if (userRole == null)
        {
            return NotFound(new { error = "Role assignment not found" });
        }

        _context.UserRoles.Remove(userRole);
        await _context.SaveChangesAsync();

        return Ok(new { message = "Role removed successfully" });
    }

    /// <summary>
    /// Change password for any user (SuperAdmin only)
    /// </summary>
    [HttpPost("{id}/change-password")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<IActionResult> ChangeUserPassword(Guid id, [FromBody] AdminChangePasswordRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 8)
            return BadRequest(new { error = "Password must be at least 8 characters" });

        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user == null)
            return NotFound(new { error = "User not found" });

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        user.FailedLoginAttempts = 0;
        user.IsLockedOut = false;
        user.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        return Ok(new { message = "Password changed successfully" });
    }
}

public record UpdateProfileRequest(string? FirstName, string? LastName);
public record AssignRoleRequest(Guid RoleId, Guid? TenantId, DateTime? ExpiresAt);
public record AdminChangePasswordRequest(string NewPassword);
public record AdminCreateUserRequest(string? Email, string? Password, string? FirstName, string? LastName);
public record AdminUpdateUserRequest(string? FirstName, string? LastName, string? Email, string? PhoneNumber);
