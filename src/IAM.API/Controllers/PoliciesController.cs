using IAM.API.Authorization;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.Json;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PoliciesController : ControllerBase
{
    private readonly IAMDbContext _context;
    private readonly IPolicyInheritanceEngine _policyEngine;
    private readonly ITenantAccessResolver _access;

    public PoliciesController(IAMDbContext context, IPolicyInheritanceEngine policyEngine, ITenantAccessResolver access)
    {
        _context = context;
        _policyEngine = policyEngine;
        _access = access;
    }

    // Task 5149: the role attributes below are only a coarse gate. Every action is decided from the caller's
    // UserRoles in the tenant the policy belongs to: managing needs SuperAdmin or an active BuildingOwner /
    // BuildingManager row in that tenant (TenantManagementAuthority), reading needs membership of it
    // (ITenantAccessResolver). A policy of a tenant the caller cannot reach looks like an unknown id (404).

    private IActionResult Refused() => StatusCode(StatusCodes.Status403Forbidden, new { error = "Forbidden" });

    private async Task<bool> CanManageTenantAsync(Guid userId, Guid tenantId, CancellationToken ct) =>
        await TenantManagementAuthority.ResolveAsync(_context, userId, User.IsInRole("SuperAdmin"), tenantId, ct) != null;

    /// <summary>
    /// A policy is visible to members of its own tenant and to members of a tenant that inherits it, so a policy of a
    /// parent tenant stays readable for the people it applies to.
    /// </summary>
    private async Task<bool> CanSeePolicyAsync(TenantAccess access, Policy policy, CancellationToken ct)
    {
        if (access.CanRead(policy.TenantId))
            return true;

        var inheritedBy = await _policyEngine.GetInheritedByTenantsAsync(policy.Id, ct);
        return inheritedBy.Any(access.CanRead);
    }

    /// <summary>
    /// List all policies for a tenant (with optional filters)
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> ListPolicies(
        [FromQuery] Guid? tenantId = null,
        [FromQuery] Guid? roleId = null,
        [FromQuery] Guid? userId = null,
        [FromQuery] string? resource = null,
        [FromQuery] bool? isActive = true,
        CancellationToken ct = default)
    {
        var access = await _access.ResolveAsync(User, ct);
        if (access.IsRefused)
            return Refused();

        var query = _context.Policies
            .Include(p => p.Tenant)
            .Include(p => p.Role)
            .Include(p => p.User)
            .AsQueryable();

        // Only the tenants the caller belongs to (SuperAdmin: all); asking for another tenant just shows nothing.
        if (access.ReadableTenants is { } readableTenants)
        {
            var readable = readableTenants.ToList();
            query = query.Where(p => readable.Contains(p.TenantId));
        }

        if (tenantId.HasValue)
        {
            query = query.Where(p => p.TenantId == tenantId.Value);
        }

        if (roleId.HasValue)
        {
            query = query.Where(p => p.RoleId == roleId.Value);
        }

        if (userId.HasValue)
        {
            query = query.Where(p => p.UserId == userId.Value);
        }

        if (!string.IsNullOrWhiteSpace(resource))
        {
            query = query.Where(p => p.Resource.StartsWith(resource));
        }

        if (isActive.HasValue)
        {
            query = query.Where(p => p.IsActive == isActive.Value);
        }

        var policies = await query
            .OrderByDescending(p => p.Priority)
            .ThenBy(p => p.CreatedAt)
            .ToListAsync(ct);

        return Ok(policies.Select(p => new
        {
            id = p.Id,
            name = p.Name,
            description = p.Description,
            tenantId = p.TenantId,
            tenantName = p.Tenant?.Name,
            inheritanceScope = p.InheritanceScope.ToString(),
            roleId = p.RoleId,
            roleName = p.Role?.Name,
            userId = p.UserId,
            userName = p.User != null ? $"{p.User.FirstName} {p.User.LastName}" : null,
            resource = p.Resource,
            action = p.Action,
            effect = p.Effect.ToString(),
            priority = p.Priority,
            timeConstraints = p.TimeConstraints != null ? JsonSerializer.Deserialize<object>(p.TimeConstraints) : null,
            conditions = p.Conditions != null ? JsonSerializer.Deserialize<object>(p.Conditions) : null,
            expiresAt = p.ExpiresAt,
            isActive = p.IsActive,
            createdAt = p.CreatedAt
        }));
    }

    /// <summary>
    /// Get specific policy by ID
    /// </summary>
    [HttpGet("{id}")]
    public async Task<IActionResult> GetPolicy(Guid id, CancellationToken ct)
    {
        var access = await _access.ResolveAsync(User, ct);
        if (access.IsRefused)
            return Refused();

        var policy = await _context.Policies
            .Include(p => p.Tenant)
            .Include(p => p.Role)
            .Include(p => p.User)
            .Include(p => p.InheritedFromPolicy)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        if (policy == null || !await CanSeePolicyAsync(access, policy, ct))
        {
            return NotFound();
        }

        return Ok(new
        {
            id = policy.Id,
            name = policy.Name,
            description = policy.Description,
            tenantId = policy.TenantId,
            tenantName = policy.Tenant?.Name,
            inheritanceScope = policy.InheritanceScope.ToString(),
            inheritedFromPolicyId = policy.InheritedFromPolicyId,
            inheritedFromPolicyName = policy.InheritedFromPolicy?.Name,
            roleId = policy.RoleId,
            roleName = policy.Role?.Name,
            userId = policy.UserId,
            userName = policy.User != null ? $"{policy.User.FirstName} {policy.User.LastName}" : null,
            resource = policy.Resource,
            action = policy.Action,
            effect = policy.Effect.ToString(),
            priority = policy.Priority,
            timeConstraints = policy.TimeConstraints != null ? JsonSerializer.Deserialize<object>(policy.TimeConstraints) : null,
            conditions = policy.Conditions != null ? JsonSerializer.Deserialize<object>(policy.Conditions) : null,
            expiresAt = policy.ExpiresAt,
            isActive = policy.IsActive,
            createdAt = policy.CreatedAt,
            createdByUserId = policy.CreatedByUserId,
            updatedAt = policy.UpdatedAt,
            updatedByUserId = policy.UpdatedByUserId
        });
    }

    /// <summary>
    /// Get effective policies for a tenant (includes inherited)
    /// </summary>
    [HttpGet("tenant/{tenantId}/effective")]
    public async Task<IActionResult> GetEffectivePolicies(Guid tenantId, CancellationToken ct)
    {
        var access = await _access.ResolveAsync(User, ct);
        if (!access.CanRead(tenantId))
            return Refused();

        var effectivePolicies = await _policyEngine.GetEffectivePoliciesForTenantAsync(tenantId, ct);

        return Ok(effectivePolicies.Select(p => new
        {
            id = p.Id,
            name = p.Name,
            description = p.Description,
            tenantId = p.TenantId,
            tenantName = p.Tenant?.Name,
            inheritanceScope = p.InheritanceScope.ToString(),
            isInherited = p.TenantId != tenantId,
            resource = p.Resource,
            action = p.Action,
            effect = p.Effect.ToString(),
            priority = p.Priority,
            roleId = p.RoleId,
            roleName = p.Role?.Name,
            userId = p.UserId
        }));
    }

    /// <summary>
    /// Get tenants that inherit a specific policy
    /// </summary>
    [HttpGet("{id}/inherited-by")]
    public async Task<IActionResult> GetInheritedByTenants(Guid id, CancellationToken ct)
    {
        var access = await _access.ResolveAsync(User, ct);
        if (access.IsRefused)
            return Refused();

        var policy = await _context.Policies.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (policy == null || !await CanSeePolicyAsync(access, policy, ct))
            return NotFound();

        // Only the tenants the caller belongs to are named.
        var tenantIds = (await _policyEngine.GetInheritedByTenantsAsync(id, ct)).Where(access.CanRead).ToList();

        var tenants = await _context.Tenants
            .Where(t => tenantIds.Contains(t.Id))
            .ToListAsync(ct);

        return Ok(tenants.Select(t => new
        {
            id = t.Id,
            name = t.Name,
            type = t.Type
        }));
    }

    /// <summary>
    /// Evaluate policy access for current user
    /// </summary>
    [HttpPost("evaluate")]
    public async Task<IActionResult> EvaluateAccess([FromBody] EvaluatePolicyRequest request, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null)
        {
            return Unauthorized();
        }

        // Evaluating is a read of the tenant's policies: members only, so it cannot probe other tenants.
        var access = await _access.ResolveAsync(User, ct);
        if (!access.CanRead(request.TenantId))
            return Refused();

        var context = new PolicyEvaluationContext
        {
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            DeviceId = request.DeviceId,
            DeviceHealth = request.DeviceHealth,
            Location = request.Location,
            EvaluationTime = DateTime.UtcNow,
            CustomAttributes = request.CustomAttributes ?? new Dictionary<string, object>()
        };

        var result = await _policyEngine.EvaluateAsync(
            Guid.Parse(userId),
            request.TenantId,
            request.Resource,
            request.Action,
            context,
            ct
        );

        return Ok(new
        {
            isAllowed = result.IsAllowed,
            reason = result.Reason,
            matchedPolicy = result.MatchedPolicy != null ? new
            {
                id = result.MatchedPolicy.Id,
                name = result.MatchedPolicy.Name,
                effect = result.MatchedPolicy.Effect.ToString(),
                priority = result.MatchedPolicy.Priority
            } : null,
            evaluatedPolicyCount = result.EvaluatedPolicies.Count,
            evaluationTimeMs = result.EvaluationTimeMs
        });
    }

    /// <summary>
    /// Test policy impact (simulate what would change)
    /// </summary>
    [HttpPost("simulate")]
    [Authorize(Roles = "SuperAdmin,BuildingOwner")]
    public async Task<IActionResult> SimulatePolicyImpact([FromBody] SimulatePolicyRequest request, CancellationToken ct)
    {
        var callerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(callerId, out var simulatingUserId))
            return Unauthorized();

        // Simulating shows who a policy would reach: only for a tenant the caller manages.
        if (!await CanManageTenantAsync(simulatingUserId, request.TenantId, ct))
            return Refused();

        var policy = new Policy
        {
            TenantId = request.TenantId,
            InheritanceScope = Enum.Parse<InheritanceScope>(request.InheritanceScope),
            RoleId = request.RoleId,
            UserId = request.UserId,
            Resource = request.Resource,
            Action = request.Action,
            Effect = Enum.Parse<PolicyEffect>(request.Effect)
        };

        var analysis = await _policyEngine.SimulatePolicyImpactAsync(policy, ct);

        return Ok(new
        {
            affectedTenantCount = analysis.AffectedTenantCount,
            affectedUserCount = analysis.AffectedUserCount,
            summary = analysis.Summary,
            affectedTenants = analysis.AffectedTenantIds.Take(20) // Limit to first 20 for preview
        });
    }

    /// <summary>
    /// Create new policy
    /// </summary>
    [HttpPost]
    [Authorize(Roles = "SuperAdmin,BuildingOwner,BuildingManager")]
    public async Task<IActionResult> CreatePolicy([FromBody] CreatePolicyRequest request, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userId, out var creatorId))
        {
            return Unauthorized();
        }

        // Checked before any lookup, so it also hides whether the tenant exists.
        if (!await CanManageTenantAsync(creatorId, request.TenantId, ct))
            return Refused();

        // Validate tenant exists
        var tenantExists = await _context.Tenants.AnyAsync(t => t.Id == request.TenantId, ct);
        if (!tenantExists)
        {
            return BadRequest(new { error = "Tenant not found" });
        }

        // Validate role exists if specified
        if (request.RoleId.HasValue)
        {
            var roleExists = await _context.Roles.AnyAsync(r => r.Id == request.RoleId.Value);
            if (!roleExists)
            {
                return BadRequest(new { error = "Role not found" });
            }
        }

        // Validate user exists if specified
        if (request.UserId.HasValue)
        {
            var userExists = await _context.Users.AnyAsync(u => u.Id == request.UserId.Value);
            if (!userExists)
            {
                return BadRequest(new { error = "User not found" });
            }
        }

        var policy = new Policy
        {
            Name = request.Name,
            Description = request.Description ?? string.Empty,
            TenantId = request.TenantId,
            InheritanceScope = Enum.Parse<InheritanceScope>(request.InheritanceScope ?? "Self"),
            RoleId = request.RoleId,
            UserId = request.UserId,
            Resource = request.Resource,
            Action = request.Action,
            Effect = Enum.Parse<PolicyEffect>(request.Effect ?? "Allow"),
            Priority = request.Priority ?? 0,
            TimeConstraints = request.TimeConstraints != null ? JsonSerializer.Serialize(request.TimeConstraints) : null,
            Conditions = request.Conditions != null ? JsonSerializer.Serialize(request.Conditions) : null,
            ExpiresAt = request.ExpiresAt,
            IsActive = true,
            CreatedByUserId = Guid.Parse(userId),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.Policies.Add(policy);
        await _context.SaveChangesAsync(ct);

        return CreatedAtAction(
            nameof(GetPolicy),
            new { id = policy.Id },
            new
            {
                id = policy.Id,
                name = policy.Name,
                description = policy.Description,
                tenantId = policy.TenantId,
                resource = policy.Resource,
                action = policy.Action,
                effect = policy.Effect.ToString(),
                createdAt = policy.CreatedAt
            });
    }

    /// <summary>
    /// Update policy
    /// </summary>
    [HttpPut("{id}")]
    [Authorize(Roles = "SuperAdmin,BuildingOwner,BuildingManager")]
    public async Task<IActionResult> UpdatePolicy(Guid id, [FromBody] UpdatePolicyRequest request, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userId, out var updaterId))
        {
            return Unauthorized();
        }

        // A caller who manages no tenant at all gets 403 for every id, before any lookup can reveal one.
        if (!await TenantManagementAuthority.ManagesAnyTenantAsync(_context, updaterId, User.IsInRole("SuperAdmin"), ct))
            return Refused();

        // The tenant is the stored policy's, never one from the request; another tenant's policy looks unknown.
        var policy = await _context.Policies.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (policy == null || !await CanManageTenantAsync(updaterId, policy.TenantId, ct))
        {
            return NotFound();
        }

        // Update fields
        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            policy.Name = request.Name;
        }

        if (request.Description != null)
        {
            policy.Description = request.Description;
        }

        if (!string.IsNullOrWhiteSpace(request.InheritanceScope))
        {
            policy.InheritanceScope = Enum.Parse<InheritanceScope>(request.InheritanceScope);
        }

        if (request.Priority.HasValue)
        {
            policy.Priority = request.Priority.Value;
        }

        if (request.TimeConstraints != null)
        {
            policy.TimeConstraints = JsonSerializer.Serialize(request.TimeConstraints);
        }

        if (request.Conditions != null)
        {
            policy.Conditions = JsonSerializer.Serialize(request.Conditions);
        }

        if (request.ExpiresAt.HasValue)
        {
            policy.ExpiresAt = request.ExpiresAt.Value;
        }

        if (request.IsActive.HasValue)
        {
            policy.IsActive = request.IsActive.Value;
        }

        policy.UpdatedAt = DateTime.UtcNow;
        policy.UpdatedByUserId = updaterId;
        await _context.SaveChangesAsync(ct);

        return Ok(new
        {
            id = policy.Id,
            name = policy.Name,
            description = policy.Description,
            isActive = policy.IsActive,
            updatedAt = policy.UpdatedAt
        });
    }

    /// <summary>
    /// Delete policy
    /// </summary>
    [HttpDelete("{id}")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<IActionResult> DeletePolicy(Guid id)
    {
        var policy = await _context.Policies
            .Include(p => p.InheritedPolicies)
            .FirstOrDefaultAsync(p => p.Id == id);

        if (policy == null)
        {
            return NotFound();
        }

        if (policy.InheritedPolicies.Any())
        {
            return BadRequest(new
            {
                error = "Cannot delete policy that is inherited by other policies",
                inheritedCount = policy.InheritedPolicies.Count
            });
        }

        _context.Policies.Remove(policy);
        await _context.SaveChangesAsync();

        return Ok(new { message = "Policy deleted successfully" });
    }
}

public record CreatePolicyRequest(
    string Name,
    string? Description,
    Guid TenantId,
    string? InheritanceScope, // Self, Children, Descendants
    Guid? RoleId,
    Guid? UserId,
    string Resource,
    string Action,
    string? Effect, // Allow, Deny
    int? Priority,
    Dictionary<string, object>? TimeConstraints,
    Dictionary<string, object>? Conditions,
    DateTime? ExpiresAt
);

public record UpdatePolicyRequest(
    string? Name,
    string? Description,
    string? InheritanceScope,
    int? Priority,
    Dictionary<string, object>? TimeConstraints,
    Dictionary<string, object>? Conditions,
    DateTime? ExpiresAt,
    bool? IsActive
);

public record EvaluatePolicyRequest(
    Guid TenantId,
    string Resource,
    string Action,
    string? DeviceId,
    string? DeviceHealth,
    string? Location,
    Dictionary<string, object>? CustomAttributes
);

public record SimulatePolicyRequest(
    Guid TenantId,
    string InheritanceScope,
    Guid? RoleId,
    Guid? UserId,
    string Resource,
    string Action,
    string Effect
);
