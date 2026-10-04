using System.Security.Claims;
using System.Text.Json;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/organization-settings")]
[Authorize]
public class OrganizationSettingsController : ControllerBase
{
    private readonly IAMDbContext _context;

    public OrganizationSettingsController(IAMDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Authority over one tenant's settings (task 4738): SuperAdmin/SystemAdmin, or an active TenantAdmin
    /// UserRole scoped to exactly that tenant. A token that carries a tenant_id claim for another tenant
    /// confers nothing, and a malformed claim or subject fails closed. Runs before any settings or tenant
    /// lookup, so a caller without authority gets the same answer whether or not the tenant exists.
    /// Returns null when the caller may proceed, otherwise the response to send.
    /// </summary>
    private async Task<IActionResult?> RequireTenantAdminAsync(Guid tenantId, CancellationToken ct)
    {
        var subject = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (!Guid.TryParse(subject, out var userId))
            return Unauthorized();

        Guid? tokenTenantId = null;
        var tenantClaim = User.FindFirst("tenant_id")?.Value;
        if (!string.IsNullOrWhiteSpace(tenantClaim))
        {
            if (!Guid.TryParse(tenantClaim, out var parsedTenantId))
                return Unauthorized();
            tokenTenantId = parsedTenantId;
        }

        if (User.IsInRole("SuperAdmin") || User.IsInRole("SystemAdmin"))
            return null;

        if (tokenTenantId.HasValue && tokenTenantId.Value != tenantId)
            return Forbidden("Token is scoped to a different tenant");

        if (!await TenantAdminAuthority.IsAdminOfTenantAsync(_context, userId, tenantId, ct))
            return Forbidden("Only SuperAdmin, SystemAdmin or an admin of this tenant can access its organization settings");

        return null;
    }

    private ObjectResult Forbidden(string message) =>
        StatusCode(StatusCodes.Status403Forbidden, new { error = message });

    /// <summary>
    /// Get organization settings for a tenant. SuperAdmin/SystemAdmin, or an admin of that tenant.
    /// </summary>
    [HttpGet("{tenantId}")]
    public async Task<IActionResult> GetSettings(Guid tenantId, CancellationToken ct)
    {
        var denied = await RequireTenantAdminAsync(tenantId, ct);
        if (denied != null)
            return denied;

        var settings = await _context.Set<OrganizationSettings>()
            .AsNoTracking()
            .Include(os => os.DefaultRole)
            .FirstOrDefaultAsync(os => os.TenantId == tenantId, ct);

        if (settings == null)
        {
            // Return default settings if none exist yet
            return Ok(new
            {
                tenantId,
                allowedEmailDomains = Array.Empty<string>(),
                requireMfa = false,
                defaultRoleId = (Guid?)null,
                defaultRoleName = (string?)null,
                maxMembers = 0,
                welcomeMessage = (string?)null,
                legacyTotpMigration = LegacyTotpMigrationMode.EmailPin.ToString(),
                legacyTotpUserCount = await CountLegacyTotpUsersAsync(tenantId, ct)
            });
        }

        return Ok(new
        {
            id = settings.Id,
            tenantId = settings.TenantId,
            allowedEmailDomains = JsonSerializer.Deserialize<List<string>>(settings.AllowedEmailDomains ?? "[]"),
            requireMfa = settings.RequireMfa,
            defaultRoleId = settings.DefaultRoleId,
            defaultRoleName = settings.DefaultRole?.Name,
            maxMembers = settings.MaxMembers,
            welcomeMessage = settings.WelcomeMessage,
            legacyTotpMigration = settings.LegacyTotpMigration.ToString(),
            legacyTotpUserCount = await CountLegacyTotpUsersAsync(tenantId, ct),
            createdAt = settings.CreatedAt,
            updatedAt = settings.UpdatedAt
        });
    }

    /// <summary>
    /// Create or update organization settings for a tenant. SuperAdmin/SystemAdmin, or an admin of that
    /// tenant. The default role (handed to role-less bulk invitations) must exist, be non-privileged, and
    /// be global or owned by the tenant; otherwise 400 and nothing is changed.
    /// </summary>
    [HttpPut("{tenantId}")]
    public async Task<IActionResult> UpdateSettings(
        Guid tenantId, [FromBody] UpdateOrganizationSettingsRequest request, CancellationToken ct)
    {
        var denied = await RequireTenantAdminAsync(tenantId, ct);
        if (denied != null)
            return denied;

        // Validate tenant exists
        var tenantExists = await _context.Tenants.AnyAsync(t => t.Id == tenantId, ct);
        if (!tenantExists)
        {
            return NotFound(new { error = "Tenant not found" });
        }

        // Validate the legacy-TOTP policy before anything is written (task 3162)
        LegacyTotpMigrationMode? legacyTotpMigration = null;
        if (request.LegacyTotpMigration != null)
        {
            if (!Enum.TryParse<LegacyTotpMigrationMode>(request.LegacyTotpMigration, ignoreCase: true, out var parsedMode)
                || !Enum.IsDefined(parsedMode))
            {
                return BadRequest(new
                {
                    error = $"LegacyTotpMigration must be one of: {string.Join(", ", Enum.GetNames<LegacyTotpMigrationMode>())}"
                });
            }

            legacyTotpMigration = parsedMode;
        }

        // Validate the default role before anything is written
        if (request.DefaultRoleId.HasValue)
        {
            var role = await _context.Roles.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == request.DefaultRoleId.Value, ct);

            switch (DefaultRoleRules.Check(role, tenantId))
            {
                case DefaultRoleProblem.NotFound:
                    return BadRequest(new { error = "Default role not found" });
                case DefaultRoleProblem.Privileged:
                    return BadRequest(new
                    {
                        error = $"Default role '{role!.Name}' is a privileged role and cannot be the default role of an organization"
                    });
                case DefaultRoleProblem.OtherTenant:
                    return BadRequest(new { error = "Default role belongs to a different tenant" });
            }
        }

        var settings = await _context.Set<OrganizationSettings>()
            .FirstOrDefaultAsync(os => os.TenantId == tenantId, ct);

        if (settings == null)
        {
            // Create new settings
            settings = new OrganizationSettings
            {
                TenantId = tenantId
            };
            _context.Set<OrganizationSettings>().Add(settings);
        }

        // Update fields
        if (request.AllowedEmailDomains != null)
        {
            settings.AllowedEmailDomains = JsonSerializer.Serialize(request.AllowedEmailDomains);
        }

        if (request.RequireMfa.HasValue)
        {
            settings.RequireMfa = request.RequireMfa.Value;
        }

        if (request.DefaultRoleId.HasValue)
        {
            settings.DefaultRoleId = request.DefaultRoleId.Value;
        }

        if (request.MaxMembers.HasValue)
        {
            settings.MaxMembers = request.MaxMembers.Value;
        }

        if (request.WelcomeMessage != null)
        {
            settings.WelcomeMessage = string.IsNullOrWhiteSpace(request.WelcomeMessage) ? null : request.WelcomeMessage;
        }

        if (legacyTotpMigration.HasValue)
        {
            settings.LegacyTotpMigration = legacyTotpMigration.Value;
        }

        settings.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        // Reload with navigation properties
        await _context.Entry(settings).Reference(s => s.DefaultRole).LoadAsync(ct);

        return Ok(new
        {
            id = settings.Id,
            tenantId = settings.TenantId,
            allowedEmailDomains = JsonSerializer.Deserialize<List<string>>(settings.AllowedEmailDomains ?? "[]"),
            requireMfa = settings.RequireMfa,
            defaultRoleId = settings.DefaultRoleId,
            defaultRoleName = settings.DefaultRole?.Name,
            maxMembers = settings.MaxMembers,
            welcomeMessage = settings.WelcomeMessage,
            legacyTotpMigration = settings.LegacyTotpMigration.ToString(),
            legacyTotpUserCount = await CountLegacyTotpUsersAsync(tenantId, ct),
            updatedAt = settings.UpdatedAt
        });
    }

    /// <summary>
    /// Members of the tenant (any role row scoped to it) whose authenticator app was enrolled before the
    /// SHA-1 to SHA-256 upgrade (task 3162): the people the legacy-TOTP policy applies to.
    /// </summary>
    private Task<int> CountLegacyTotpUsersAsync(Guid tenantId, CancellationToken ct) =>
        _context.Users
            .AsNoTracking()
            .Where(u => _context.UserRoles.Any(ur => ur.UserId == u.Id && ur.TenantId == tenantId))
            .Where(IAM.Core.Entities.User.LegacyTotpEnrollment)
            .CountAsync(ct);
}

public record UpdateOrganizationSettingsRequest(
    List<string>? AllowedEmailDomains,
    bool? RequireMfa,
    Guid? DefaultRoleId,
    int? MaxMembers,
    string? WelcomeMessage,
    string? LegacyTotpMigration = null
);
