using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IAM.API.Controllers;

/// <summary>
/// Federated application role catalogs. An application (e.g. the JengoWork task manager)
/// registers the roles it understands at startup; they are stored as ordinary global
/// roles named "{clientId}:{role}" with Category "app:{clientId}", so the existing
/// role-assignment admin UI manages them without any schema or UI change. The OIDC
/// authorize endpoint uses the presence of such a catalog to gate application access:
/// once an app has registered roles, only users holding at least one of them can sign
/// in to that app (see AuthorizationController).
/// </summary>
[ApiController]
[Route("api/app-roles")]
public class AppRolesController : ControllerBase
{
    private readonly IAMDbContext _context;
    private readonly ILogger<AppRolesController> _logger;

    public AppRolesController(IAMDbContext context, ILogger<AppRolesController> logger)
    {
        _context = context;
        _logger = logger;
    }

    public record AppRoleDefinition(string Name, string? Description);
    public record RegisterAppRolesRequest(string ClientId, List<AppRoleDefinition> Roles);

    /// <summary>
    /// Idempotent catalog upsert, called by the application at startup with an IAM API key
    /// (X-API-Key header). Roles no longer in the catalog are never deleted (assignments
    /// would silently vanish) — they are reported back as stale for an admin to clean up.
    /// </summary>
    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register([FromBody] RegisterAppRolesRequest request)
    {
        // The API-key middleware validates X-API-Key when present and stamps this marker;
        // no marker means no (valid) key was supplied — JWT users should not call this.
        if (!HttpContext.Items.ContainsKey("ApiKeyPrefix"))
            return Unauthorized(new { error = "API key required (X-API-Key header)." });

        if (string.IsNullOrWhiteSpace(request.ClientId) || request.Roles == null || request.Roles.Count == 0)
            return BadRequest(new { error = "clientId and a non-empty roles list are required." });

        var clientId = request.ClientId.Trim().ToLowerInvariant();
        if (clientId.Contains(':'))
            return BadRequest(new { error = "clientId must not contain ':'." });

        var category = $"app:{clientId}";
        var prefix = $"{clientId}:";
        var existing = await _context.Roles
            .Where(r => r.Category == category)
            .ToListAsync();

        var registered = new List<string>();
        foreach (var def in request.Roles)
        {
            if (string.IsNullOrWhiteSpace(def.Name)) continue;
            var fullName = prefix + def.Name.Trim().ToLowerInvariant();
            var role = existing.FirstOrDefault(r => string.Equals(r.Name, fullName, StringComparison.OrdinalIgnoreCase));
            if (role == null)
            {
                role = new Role
                {
                    Name = fullName,
                    TenantId = null, // global
                    Category = category,
                    Description = def.Description ?? string.Empty,
                    IsSystemRole = false
                };
                _context.Roles.Add(role);
            }
            else
            {
                role.Description = def.Description ?? role.Description;
                role.UpdatedAt = DateTime.UtcNow;
            }
            registered.Add(fullName);
        }

        var stale = existing
            .Where(r => !registered.Contains(r.Name, StringComparer.OrdinalIgnoreCase))
            .Select(r => r.Name)
            .ToList();

        await _context.SaveChangesAsync();
        _logger.LogInformation("App role catalog registered for {ClientId}: {Count} roles ({Stale} stale left in place)",
            clientId, registered.Count, stale.Count);

        return Ok(new { clientId, registered, stale });
    }

    /// <summary>The registered catalog for an application (admin/debug convenience).</summary>
    [HttpGet("{clientId}")]
    [AllowAnonymous]
    public async Task<IActionResult> Get(string clientId)
    {
        if (!HttpContext.Items.ContainsKey("ApiKeyPrefix"))
            return Unauthorized(new { error = "API key required (X-API-Key header)." });

        var category = $"app:{clientId.Trim().ToLowerInvariant()}";
        var roles = await _context.Roles.Where(r => r.Category == category)
            .Select(r => new { r.Name, r.Description })
            .ToListAsync();
        return Ok(new { clientId, roles });
    }
}
