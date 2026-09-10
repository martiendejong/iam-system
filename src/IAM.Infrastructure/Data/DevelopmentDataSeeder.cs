using IAM.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace IAM.Infrastructure.Data;

/// <summary>
/// Seeds development database with test users and roles.
/// ONLY runs in Development environment - never in Production.
/// </summary>
public class DevelopmentDataSeeder
{
    private readonly IAMDbContext _context;
    private readonly bool _isDevelopment;

    public DevelopmentDataSeeder(IAMDbContext context, bool isDevelopment = false)
    {
        _context = context;
        _isDevelopment = isDevelopment;
    }

    /// <summary>
    /// Seeds the database with development test data.
    /// Safe to call multiple times - checks for existing data.
    /// </summary>
    public async Task SeedAsync()
    {
        // Primary guard: must be Development environment
        if (!_isDevelopment)
        {
            // Silently skip — caller should not invoke this in production
            return;
        }

        // Double-check: if we somehow got here in production, abort
        if (!_isDevelopment)
        {
            // This branch is theoretically unreachable but kept as a defense-in-depth guard
            return;
        }

        // Don't seed if we already have users
        if (await _context.Users.AnyAsync())
        {
            return;
        }

        // Reuse the migration-seeded "SuperAdmin" role rather than creating a bare "Admin"
        // role - no [Authorize(Roles=...)] policy in the API recognizes "Admin", so a user
        // seeded with it gets 403s on every admin-only endpoint (Users list, OAuth Clients, ...).
        var adminRole = await _context.Roles.FirstOrDefaultAsync(r => r.Name == "SuperAdmin");
        if (adminRole == null)
        {
            adminRole = new Role
            {
                Id = Guid.NewGuid(),
                Name = "SuperAdmin",
                Description = "Full system access",
                IsSystemRole = true,
                Permissions = "[\"*\"]", // Wildcard = all permissions
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _context.Roles.Add(adminRole);
        }

        // Create User Role
        var userRole = await _context.Roles.FirstOrDefaultAsync(r => r.Name == "User");
        if (userRole == null)
        {
            userRole = new Role
            {
                Id = Guid.NewGuid(),
                Name = "User",
                Description = "Standard user access",
                IsSystemRole = true,
                Permissions = "[\"User.View\", \"User.Update\"]",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _context.Roles.Add(userRole);
        }

        // Create Admin User (already verified)
        var adminUser = new User
        {
            Id = Guid.NewGuid(),
            Email = "admin@test.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Admin123!", workFactor: 12),
            FirstName = "Admin",
            LastName = "User",
            EmailConfirmed = true, // ← Already verified for testing
            EmailVerificationToken = null,
            EmailVerificationTokenExpiry = null,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            LastLoginAt = null
        };

        // Create Regular User (already verified)
        var regularUser = new User
        {
            Id = Guid.NewGuid(),
            Email = "user@test.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("User123!", workFactor: 12),
            FirstName = "Test",
            LastName = "User",
            EmailConfirmed = true, // ← Already verified for testing
            EmailVerificationToken = null,
            EmailVerificationTokenExpiry = null,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            LastLoginAt = null
        };

        // Create Developer User (already verified)
        var devUser = new User
        {
            Id = Guid.NewGuid(),
            Email = "dev@test.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Dev123!", workFactor: 12),
            FirstName = "Developer",
            LastName = "User",
            EmailConfirmed = true, // ← Already verified for testing
            EmailVerificationToken = null,
            EmailVerificationTokenExpiry = null,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            LastLoginAt = null
        };

        _context.Users.AddRange(adminUser, regularUser, devUser);

        // Assign Admin Role to Admin User
        var adminUserRole = new UserRole
        {
            UserId = adminUser.Id,
            RoleId = adminRole.Id,
            GrantedAt = DateTime.UtcNow
        };

        // Assign User Role to Regular User
        var regularUserRole = new UserRole
        {
            UserId = regularUser.Id,
            RoleId = userRole.Id,
            GrantedAt = DateTime.UtcNow
        };

        // Assign both roles to Dev User
        var devUserAdminRole = new UserRole
        {
            UserId = devUser.Id,
            RoleId = adminRole.Id,
            GrantedAt = DateTime.UtcNow
        };

        var devUserUserRole = new UserRole
        {
            UserId = devUser.Id,
            RoleId = userRole.Id,
            GrantedAt = DateTime.UtcNow
        };

        _context.UserRoles.AddRange(adminUserRole, regularUserRole, devUserAdminRole, devUserUserRole);

        await _context.SaveChangesAsync();
    }
}
