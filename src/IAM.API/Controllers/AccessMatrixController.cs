using IAM.Core.Configuration;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using System.Security.Claims;
using System.Text.Json;

namespace IAM.API.Controllers;

/// <summary>
/// Admin access matrix: users x applications with per-app base access (checkbox)
/// and fine-grained permissions ("..." menu). SuperAdmin-only.
///
/// The set of manageable roles is manifest-driven (AccessMatrix config section)
/// plus a default "app:{clientId}" base role for every registered OpenIddict
/// application without a manifest entry. Only roles in that managed set can be
/// granted/revoked here — this endpoint is deliberately NOT a generic role editor
/// (SuperAdmin and other system roles cannot be touched via the matrix).
///
/// CSRF: all endpoints require a Bearer access token (never cookie auth), and
/// writes are JSON POSTs, so cross-site request forgery does not apply.
/// </summary>
[ApiController]
[Route("api/access-matrix")]
[Authorize(Roles = "SuperAdmin")]
public class AccessMatrixController : ControllerBase
{
    private readonly IAMDbContext _context;
    private readonly IOptionsSnapshot<AccessMatrixOptions> _options;
    private readonly IOpenIddictApplicationManager? _applicationManager;
    private readonly ILogger<AccessMatrixController> _logger;

    public AccessMatrixController(
        IAMDbContext context,
        IOptionsSnapshot<AccessMatrixOptions> options,
        ILogger<AccessMatrixController> logger,
        IOpenIddictApplicationManager? applicationManager = null)
    {
        _context = context;
        _options = options;
        _logger = logger;
        _applicationManager = applicationManager;
    }

    /// <summary>
    /// The applications (columns) of the matrix: manifest entries merged with
    /// registered OpenIddict applications that have no manifest entry.
    /// </summary>
    [HttpGet("applications")]
    public async Task<IActionResult> GetApplications(CancellationToken cancellationToken)
    {
        var apps = await BuildApplicationListAsync(cancellationToken);

        return Ok(apps.Select(a => new
        {
            clientId = a.App.ClientId,
            displayName = a.App.DisplayName,
            baseRole = a.App.BaseRole,
            permissions = a.App.Permissions.Select(p => new
            {
                role = p.Role,
                label = p.Label,
                description = p.Description
            }),
            source = a.Source
        }));
    }

    /// <summary>
    /// The matrix itself: users (rows, searchable/paged) with their current
    /// matrix-managed role names, read live from role assignments.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetMatrix(
        [FromQuery] string? search,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        take = Math.Clamp(take, 1, 200);
        skip = Math.Max(skip, 0);

        var apps = await BuildApplicationListAsync(cancellationToken);
        var managedRoles = BuildManagedRoleSet(apps);

        var query = _context.Users.AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(u =>
                u.Email.ToLower().Contains(term) ||
                u.FirstName.ToLower().Contains(term) ||
                u.LastName.ToLower().Contains(term));
        }

        var total = await query.CountAsync(cancellationToken);

        var users = await query
            .OrderBy(u => u.Email)
            .Skip(skip)
            .Take(take)
            .Select(u => new
            {
                u.Id,
                u.Email,
                u.FirstName,
                u.LastName,
                u.IsActive,
                RoleNames = u.UserRoles.Select(ur => ur.Role.Name).ToList()
            })
            .ToListAsync(cancellationToken);

        return Ok(new
        {
            total,
            skip,
            take,
            applications = apps.Select(a => new
            {
                clientId = a.App.ClientId,
                displayName = a.App.DisplayName,
                baseRole = a.App.BaseRole,
                permissions = a.App.Permissions.Select(p => new
                {
                    role = p.Role,
                    label = p.Label,
                    description = p.Description
                }),
                source = a.Source
            }),
            users = users.Select(u => new
            {
                id = u.Id,
                email = u.Email,
                firstName = u.FirstName,
                lastName = u.LastName,
                isActive = u.IsActive,
                // Only expose matrix-managed roles; the page has no business
                // seeing e.g. SuperAdmin assignments through this endpoint.
                roles = u.RoleNames.Distinct().Where(managedRoles.Contains).OrderBy(r => r)
            })
        });
    }

    /// <summary>
    /// Grant a matrix-managed role (base access or fine-grained permission) to a user.
    /// Idempotent; audit-logged with before/after. Effective on next token refresh.
    /// </summary>
    [HttpPost("grant")]
    public async Task<IActionResult> Grant([FromBody] AccessMatrixChangeRequest request, CancellationToken cancellationToken)
    {
        return await ApplyChangeAsync(request, grant: true, cancellationToken);
    }

    /// <summary>
    /// Revoke a matrix-managed role from a user. Removes every assignment of the
    /// role name (any tenant scope) so the checkbox state is unambiguous.
    /// Idempotent; audit-logged with before/after.
    /// </summary>
    [HttpPost("revoke")]
    public async Task<IActionResult> Revoke([FromBody] AccessMatrixChangeRequest request, CancellationToken cancellationToken)
    {
        return await ApplyChangeAsync(request, grant: false, cancellationToken);
    }

    private async Task<IActionResult> ApplyChangeAsync(AccessMatrixChangeRequest request, bool grant, CancellationToken cancellationToken)
    {
        if (request.UserId == Guid.Empty || string.IsNullOrWhiteSpace(request.Role))
        {
            return BadRequest(new { error = "userId and role are required" });
        }

        var roleName = request.Role.Trim();
        var currentUserId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        // Safety rail 1: you can never lock yourself out of SuperAdmin here.
        if (!grant && roleName == "SuperAdmin" && request.UserId == currentUserId)
        {
            return BadRequest(new { error = "You cannot remove your own SuperAdmin role" });
        }

        // Safety rail 2: only manifest/registered app roles are manageable via
        // the matrix. SuperAdmin and other system roles are never in this set.
        var apps = await BuildApplicationListAsync(cancellationToken);
        var managedRoles = BuildManagedRoleSet(apps);
        if (!managedRoles.Contains(roleName))
        {
            return BadRequest(new { error = $"Role '{roleName}' is not managed by the access matrix" });
        }

        var user = await _context.Users
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Id == request.UserId, cancellationToken);

        if (user == null)
        {
            return NotFound(new { error = "User not found" });
        }

        var before = user.UserRoles
            .Select(ur => ur.Role.Name)
            .Distinct()
            .Where(managedRoles.Contains)
            .OrderBy(r => r)
            .ToArray();

        bool changed;
        if (grant)
        {
            changed = await GrantRoleAsync(user, roleName, currentUserId, cancellationToken);
        }
        else
        {
            changed = RevokeRole(user, roleName);
        }

        if (changed)
        {
            var after = grant
                ? before.Concat(new[] { roleName }).Distinct().OrderBy(r => r).ToArray()
                : before.Where(r => r != roleName).ToArray();

            _context.AuditLogs.Add(new AuditLog
            {
                UserId = currentUserId,
                Action = grant ? "AccessMatrix.RoleGranted" : "AccessMatrix.RoleRevoked",
                Resource = "UserRole",
                Details = JsonSerializer.Serialize(new
                {
                    targetUserId = user.Id,
                    targetEmail = user.Email,
                    role = roleName,
                    before,
                    after
                }),
                IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent = Request.Headers.UserAgent.ToString()
            });

            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Access matrix: {Action} role {Role} for user {UserId} by {AdminId}",
                grant ? "granted" : "revoked", roleName, user.Id, currentUserId);
        }

        return Ok(new
        {
            message = changed
                ? (grant ? "Role granted" : "Role revoked")
                : "No change (already in requested state)",
            changed,
            userId = user.Id,
            role = roleName,
            note = "Effective on the user's next token refresh"
        });
    }

    private async Task<bool> GrantRoleAsync(User user, string roleName, Guid grantedBy, CancellationToken cancellationToken)
    {
        if (user.UserRoles.Any(ur => ur.Role.Name == roleName))
        {
            return false; // idempotent
        }

        // Find the role (prefer a global one), or auto-create it. App roles are
        // plain name-carrying rows: token issuance copies UserRole.Role.Name into
        // role claims, which is what the relying apps consume.
        var role = await _context.Roles
            .Where(r => r.Name == roleName)
            .OrderBy(r => r.TenantId == null ? 0 : 1)
            .FirstOrDefaultAsync(cancellationToken);

        if (role == null)
        {
            role = new Role
            {
                Name = roleName,
                Description = "Application access role (managed via access matrix)",
                Category = "AppAccess",
                IsSystemRole = false,
                TenantId = null
            };
            _context.Roles.Add(role);
        }

        _context.UserRoles.Add(new UserRole
        {
            UserId = user.Id,
            User = user,
            RoleId = role.Id,
            Role = role,
            TenantId = null,
            GrantedBy = grantedBy,
            GrantedAt = DateTime.UtcNow
        });

        return true;
    }

    private bool RevokeRole(User user, string roleName)
    {
        var assignments = user.UserRoles.Where(ur => ur.Role.Name == roleName).ToList();
        if (assignments.Count == 0)
        {
            return false; // idempotent
        }

        _context.UserRoles.RemoveRange(assignments);
        return true;
    }

    private sealed record MatrixApp(AccessMatrixApplication App, string Source);

    /// <summary>
    /// Manifest entries first (they define permissions), then any registered
    /// OpenIddict application without a manifest entry, with a default
    /// "app:{clientId}" base role and no fine-grained permissions.
    /// </summary>
    private async Task<List<MatrixApp>> BuildApplicationListAsync(CancellationToken cancellationToken)
    {
        var result = _options.Value.Applications
            .Where(a => !string.IsNullOrWhiteSpace(a.ClientId) && !string.IsNullOrWhiteSpace(a.BaseRole))
            .Select(a => new MatrixApp(a, "manifest"))
            .ToList();

        if (_applicationManager != null)
        {
            var known = new HashSet<string>(result.Select(a => a.App.ClientId), StringComparer.OrdinalIgnoreCase);
            try
            {
                await foreach (var app in _applicationManager.ListAsync(cancellationToken: cancellationToken))
                {
                    string? clientId = null;
                    string? displayName = null;
                    try
                    {
                        clientId = await _applicationManager.GetClientIdAsync(app, cancellationToken);
                        displayName = await _applicationManager.GetDisplayNameAsync(app, cancellationToken);
                    }
                    catch
                    {
                        // Corrupt/manually inserted client rows must not break the matrix.
                    }

                    if (string.IsNullOrWhiteSpace(clientId) || !known.Add(clientId))
                    {
                        continue;
                    }

                    result.Add(new MatrixApp(new AccessMatrixApplication
                    {
                        ClientId = clientId,
                        DisplayName = displayName ?? clientId,
                        BaseRole = $"app:{clientId}",
                        Permissions = new List<AccessMatrixPermission>()
                    }, "registered"));
                }
            }
            catch (Exception ex)
            {
                // The matrix should still render from the manifest if OpenIddict
                // listing fails (e.g. store not configured in a test host).
                _logger.LogWarning(ex, "Access matrix: failed to list OpenIddict applications; using manifest only");
            }
        }

        return result;
    }

    private static HashSet<string> BuildManagedRoleSet(List<MatrixApp> apps)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in apps)
        {
            set.Add(entry.App.BaseRole);
            foreach (var permission in entry.App.Permissions)
            {
                if (!string.IsNullOrWhiteSpace(permission.Role))
                {
                    set.Add(permission.Role);
                }
            }
        }

        // Hard exclusions: the matrix must never manage system-level admin roles,
        // even if someone mistakenly adds them to a manifest.
        set.Remove("SuperAdmin");
        return set;
    }
}

public record AccessMatrixChangeRequest(Guid UserId, string Role);
