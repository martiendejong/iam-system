using System.Text.Json;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
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
    /// Get organization settings for a tenant
    /// </summary>
    [HttpGet("{tenantId}")]
    public async Task<IActionResult> GetSettings(Guid tenantId)
    {
        var settings = await _context.Set<OrganizationSettings>()
            .Include(os => os.DefaultRole)
            .FirstOrDefaultAsync(os => os.TenantId == tenantId);

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
                welcomeMessage = (string?)null
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
            createdAt = settings.CreatedAt,
            updatedAt = settings.UpdatedAt
        });
    }

    /// <summary>
    /// Create or update organization settings for a tenant
    /// </summary>
    [HttpPut("{tenantId}")]
    public async Task<IActionResult> UpdateSettings(Guid tenantId, [FromBody] UpdateOrganizationSettingsRequest request)
    {
        // Validate tenant exists
        var tenantExists = await _context.Tenants.AnyAsync(t => t.Id == tenantId);
        if (!tenantExists)
        {
            return NotFound(new { error = "Tenant not found" });
        }

        // Validate default role exists if specified
        if (request.DefaultRoleId.HasValue)
        {
            var roleExists = await _context.Roles.AnyAsync(r => r.Id == request.DefaultRoleId.Value);
            if (!roleExists)
            {
                return BadRequest(new { error = "Default role not found" });
            }
        }

        var settings = await _context.Set<OrganizationSettings>()
            .FirstOrDefaultAsync(os => os.TenantId == tenantId);

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

        settings.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        // Reload with navigation properties
        await _context.Entry(settings).Reference(s => s.DefaultRole).LoadAsync();

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
            updatedAt = settings.UpdatedAt
        });
    }
}

public record UpdateOrganizationSettingsRequest(
    List<string>? AllowedEmailDomains,
    bool? RequireMfa,
    Guid? DefaultRoleId,
    int? MaxMembers,
    string? WelcomeMessage
);
