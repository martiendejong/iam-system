using System.Text.Json;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IAM.Infrastructure.Services;

public class DelegationService : IDelegationService
{
    private readonly IAMDbContext _context;
    private readonly IEventBus _eventBus;
    private readonly ILogger<DelegationService> _logger;

    public DelegationService(
        IAMDbContext context,
        IEventBus eventBus,
        ILogger<DelegationService> logger)
    {
        _context = context;
        _eventBus = eventBus;
        _logger = logger;
    }

    // ─── Delegations ─────────────────────────────────────────

    public async Task<Delegation> CreateDelegationAsync(
        Guid delegatorUserId,
        Guid delegateUserId,
        Guid tenantId,
        List<string> permissions,
        DateTime validFrom,
        DateTime validUntil,
        string reason,
        bool requiresApproval = false,
        CancellationToken ct = default)
    {
        if (delegatorUserId == delegateUserId)
            throw new InvalidOperationException("A user cannot delegate permissions to themselves");

        if (validUntil <= validFrom)
            throw new InvalidOperationException("ValidUntil must be after ValidFrom");

        if (permissions.Count == 0)
            throw new InvalidOperationException("At least one permission must be delegated");

        var delegation = new Delegation
        {
            DelegatorUserId = delegatorUserId,
            DelegateUserId = delegateUserId,
            TenantId = tenantId,
            Permissions = JsonSerializer.Serialize(permissions),
            ValidFrom = validFrom,
            ValidUntil = validUntil,
            Reason = reason,
            RequiresApproval = requiresApproval,
            Status = requiresApproval ? DelegationStatus.PendingApproval : DelegationStatus.Active,
            IsActive = !requiresApproval
        };

        _context.Delegations.Add(delegation);

        _context.AuditLogs.Add(new AuditLog
        {
            UserId = delegatorUserId,
            TenantId = tenantId,
            Action = "DelegationCreated",
            Resource = "Delegation",
            Details = JsonSerializer.Serialize(new
            {
                delegationId = delegation.Id,
                delegatorUserId,
                delegateUserId,
                permissions,
                validFrom,
                validUntil,
                requiresApproval
            })
        });

        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Delegation {DelegationId} created from user {DelegatorId} to user {DelegateId} for {PermCount} permissions",
            delegation.Id, delegatorUserId, delegateUserId, permissions.Count);

        await PublishEventAsync("delegation.created", delegation, ct);

        return delegation;
    }

    public async Task<Delegation?> GetDelegationAsync(Guid delegationId, CancellationToken ct = default)
    {
        return await _context.Delegations
            .Include(d => d.DelegatorUser)
            .Include(d => d.DelegateUser)
            .Include(d => d.Tenant)
            .FirstOrDefaultAsync(d => d.Id == delegationId, ct);
    }

    public async Task<List<Delegation>> GetDelegationsAsync(Guid tenantId, CancellationToken ct = default)
    {
        return await _context.Delegations
            .Include(d => d.DelegatorUser)
            .Include(d => d.DelegateUser)
            .Include(d => d.Tenant)
            .Where(d => d.TenantId == tenantId)
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<List<Delegation>> GetActiveDelegationsForUserAsync(Guid delegateUserId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        return await _context.Delegations
            .Include(d => d.DelegatorUser)
            .Include(d => d.Tenant)
            .Where(d => d.DelegateUserId == delegateUserId
                && d.IsActive
                && d.Status == DelegationStatus.Active
                && d.ValidFrom <= now
                && d.ValidUntil > now)
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<Delegation> ApproveDelegationAsync(Guid delegationId, Guid approverUserId, CancellationToken ct = default)
    {
        var delegation = await _context.Delegations
            .FirstOrDefaultAsync(d => d.Id == delegationId, ct)
            ?? throw new InvalidOperationException($"Delegation {delegationId} not found");

        if (delegation.Status != DelegationStatus.PendingApproval)
            throw new InvalidOperationException($"Delegation {delegationId} is not pending approval");

        delegation.Status = DelegationStatus.Active;
        delegation.IsActive = true;
        delegation.ApprovedByUserId = approverUserId;
        delegation.ApprovedAt = DateTime.UtcNow;
        delegation.UpdatedAt = DateTime.UtcNow;

        _context.AuditLogs.Add(new AuditLog
        {
            UserId = approverUserId,
            TenantId = delegation.TenantId,
            Action = "DelegationApproved",
            Resource = "Delegation",
            Details = JsonSerializer.Serialize(new
            {
                delegationId,
                approverUserId
            })
        });

        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Delegation {DelegationId} approved by {ApproverId}", delegationId, approverUserId);

        await PublishEventAsync("delegation.approved", delegation, ct);

        return delegation;
    }

    public async Task<Delegation> RevokeDelegationAsync(Guid delegationId, Guid revokedByUserId, CancellationToken ct = default)
    {
        var delegation = await _context.Delegations
            .FirstOrDefaultAsync(d => d.Id == delegationId, ct)
            ?? throw new InvalidOperationException($"Delegation {delegationId} not found");

        delegation.Revoke(revokedByUserId);

        _context.AuditLogs.Add(new AuditLog
        {
            UserId = revokedByUserId,
            TenantId = delegation.TenantId,
            Action = "DelegationRevoked",
            Resource = "Delegation",
            Details = JsonSerializer.Serialize(new
            {
                delegationId,
                revokedByUserId
            })
        });

        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Delegation {DelegationId} revoked by {RevokedById}", delegationId, revokedByUserId);

        await PublishEventAsync("delegation.revoked", delegation, ct);

        return delegation;
    }

    public async Task<List<string>> GetEffectivePermissionsAsync(Guid userId, Guid tenantId, CancellationToken ct = default)
    {
        // Get direct role permissions
        var directPermissions = await _context.UserRoles
            .Include(ur => ur.Role)
            .Where(ur => ur.UserId == userId && (ur.TenantId == tenantId || ur.TenantId == null))
            .Select(ur => ur.Role.Permissions)
            .ToListAsync(ct);

        var allPermissions = new HashSet<string>();

        foreach (var permJson in directPermissions)
        {
            if (string.IsNullOrEmpty(permJson)) continue;
            try
            {
                var perms = JsonSerializer.Deserialize<List<string>>(permJson);
                if (perms != null)
                {
                    foreach (var p in perms)
                        allPermissions.Add(p);
                }
            }
            catch (JsonException)
            {
                // Skip malformed JSON
            }
        }

        // Get delegated permissions
        var now = DateTime.UtcNow;
        var delegatedPermissions = await _context.Delegations
            .Where(d => d.DelegateUserId == userId
                && d.TenantId == tenantId
                && d.IsActive
                && d.Status == DelegationStatus.Active
                && d.ValidFrom <= now
                && d.ValidUntil > now)
            .Select(d => d.Permissions)
            .ToListAsync(ct);

        foreach (var permJson in delegatedPermissions)
        {
            if (string.IsNullOrEmpty(permJson)) continue;
            try
            {
                var perms = JsonSerializer.Deserialize<List<string>>(permJson);
                if (perms != null)
                {
                    foreach (var p in perms)
                        allPermissions.Add(p);
                }
            }
            catch (JsonException)
            {
                // Skip malformed JSON
            }
        }

        return allPermissions.OrderBy(p => p).ToList();
    }

    public async Task<int> ExpireDelegationsAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        var expiredDelegations = await _context.Delegations
            .Where(d => d.IsActive
                && d.Status == DelegationStatus.Active
                && d.ValidUntil < now)
            .ToListAsync(ct);

        foreach (var delegation in expiredDelegations)
        {
            delegation.IsActive = false;
            delegation.Status = DelegationStatus.Expired;
            delegation.UpdatedAt = now;

            _context.AuditLogs.Add(new AuditLog
            {
                UserId = delegation.DelegatorUserId,
                TenantId = delegation.TenantId,
                Action = "DelegationExpired",
                Resource = "Delegation",
                Details = JsonSerializer.Serialize(new
                {
                    delegationId = delegation.Id,
                    validUntil = delegation.ValidUntil
                })
            });
        }

        if (expiredDelegations.Count > 0)
        {
            await _context.SaveChangesAsync(ct);
            _logger.LogInformation("Expired {Count} delegations", expiredDelegations.Count);
        }

        return expiredDelegations.Count;
    }

    // ─── SoD Constraints ─────────────────────────────────────

    public async Task<SodConstraint> CreateConstraintAsync(
        string name,
        Guid tenantId,
        Guid conflictingRoleA,
        Guid conflictingRoleB,
        string description,
        SodSeverity severity = SodSeverity.Warning,
        CancellationToken ct = default)
    {
        if (conflictingRoleA == conflictingRoleB)
            throw new InvalidOperationException("Conflicting roles must be different");

        // Check if a constraint already exists for this pair
        var existing = await _context.SodConstraints
            .AnyAsync(c => c.TenantId == tenantId
                && c.IsActive
                && ((c.ConflictingRoleA == conflictingRoleA && c.ConflictingRoleB == conflictingRoleB)
                    || (c.ConflictingRoleA == conflictingRoleB && c.ConflictingRoleB == conflictingRoleA)),
                ct);

        if (existing)
            throw new InvalidOperationException("A SoD constraint already exists for this role pair");

        var constraint = new SodConstraint
        {
            Name = name,
            TenantId = tenantId,
            ConflictingRoleA = conflictingRoleA,
            ConflictingRoleB = conflictingRoleB,
            Description = description,
            Severity = severity
        };

        _context.SodConstraints.Add(constraint);

        _context.AuditLogs.Add(new AuditLog
        {
            TenantId = tenantId,
            Action = "SodConstraintCreated",
            Resource = "SodConstraint",
            Details = JsonSerializer.Serialize(new
            {
                constraintId = constraint.Id,
                name,
                conflictingRoleA,
                conflictingRoleB,
                severity = severity.ToString()
            })
        });

        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "SoD constraint {ConstraintId} '{Name}' created for roles {RoleA} vs {RoleB}",
            constraint.Id, name, conflictingRoleA, conflictingRoleB);

        return constraint;
    }

    public async Task<SodConstraint?> GetConstraintAsync(Guid constraintId, CancellationToken ct = default)
    {
        return await _context.SodConstraints
            .Include(c => c.Tenant)
            .Include(c => c.RoleA)
            .Include(c => c.RoleB)
            .FirstOrDefaultAsync(c => c.Id == constraintId, ct);
    }

    public async Task<List<SodConstraint>> GetConstraintsAsync(Guid tenantId, CancellationToken ct = default)
    {
        return await _context.SodConstraints
            .Include(c => c.RoleA)
            .Include(c => c.RoleB)
            .Where(c => c.TenantId == tenantId)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
    }

    public async Task<SodConstraint> UpdateConstraintAsync(Guid constraintId, SodConstraint updated, CancellationToken ct = default)
    {
        var constraint = await _context.SodConstraints
            .FirstOrDefaultAsync(c => c.Id == constraintId, ct)
            ?? throw new InvalidOperationException($"SoD constraint {constraintId} not found");

        constraint.Name = updated.Name;
        constraint.Description = updated.Description;
        constraint.Severity = updated.Severity;
        constraint.IsActive = updated.IsActive;
        constraint.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("SoD constraint {ConstraintId} updated", constraintId);

        return constraint;
    }

    public async Task<bool> DeleteConstraintAsync(Guid constraintId, CancellationToken ct = default)
    {
        var constraint = await _context.SodConstraints
            .FirstOrDefaultAsync(c => c.Id == constraintId, ct);

        if (constraint == null)
            return false;

        _context.SodConstraints.Remove(constraint);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("SoD constraint {ConstraintId} deleted", constraintId);

        return true;
    }

    public async Task<List<SodViolation>> CheckSodViolationsAsync(Guid userId, CancellationToken ct = default)
    {
        // Get all roles for this user
        var userRoles = await _context.UserRoles
            .Where(ur => ur.UserId == userId)
            .Select(ur => new { ur.RoleId, ur.TenantId })
            .ToListAsync(ct);

        var violations = new List<SodViolation>();

        foreach (var userRole in userRoles)
        {
            var tenantId = userRole.TenantId;

            // Get active constraints for this tenant
            var constraints = await _context.SodConstraints
                .Where(c => c.IsActive && (tenantId == null || c.TenantId == tenantId))
                .ToListAsync(ct);

            foreach (var constraint in constraints)
            {
                // Check if user has both conflicting roles
                var hasRoleA = userRoles.Any(ur => ur.RoleId == constraint.ConflictingRoleA);
                var hasRoleB = userRoles.Any(ur => ur.RoleId == constraint.ConflictingRoleB);

                if (hasRoleA && hasRoleB)
                {
                    // Check if this violation is already recorded
                    var existingViolation = await _context.SodViolations
                        .AnyAsync(v => v.ConstraintId == constraint.Id
                            && v.UserId == userId
                            && v.ResolvedAt == null,
                            ct);

                    if (!existingViolation)
                    {
                        var violation = new SodViolation
                        {
                            ConstraintId = constraint.Id,
                            UserId = userId,
                            RoleA = constraint.ConflictingRoleA,
                            RoleB = constraint.ConflictingRoleB,
                            DetectedAt = DateTime.UtcNow
                        };

                        _context.SodViolations.Add(violation);
                        violations.Add(violation);

                        _context.AuditLogs.Add(new AuditLog
                        {
                            UserId = userId,
                            TenantId = constraint.TenantId,
                            Action = "SodViolationDetected",
                            Resource = "SodViolation",
                            Details = JsonSerializer.Serialize(new
                            {
                                violationId = violation.Id,
                                constraintId = constraint.Id,
                                constraintName = constraint.Name,
                                roleA = constraint.ConflictingRoleA,
                                roleB = constraint.ConflictingRoleB,
                                severity = constraint.Severity.ToString()
                            })
                        });

                        _logger.LogWarning(
                            "SoD violation detected: User {UserId} holds conflicting roles {RoleA} and {RoleB} (Constraint: {ConstraintName}, Severity: {Severity})",
                            userId, constraint.ConflictingRoleA, constraint.ConflictingRoleB,
                            constraint.Name, constraint.Severity);
                    }
                }
            }
        }

        if (violations.Count > 0)
        {
            await _context.SaveChangesAsync(ct);
        }

        return violations;
    }

    public async Task<List<SodViolation>> GetViolationsAsync(Guid? tenantId = null, CancellationToken ct = default)
    {
        var query = _context.SodViolations
            .Include(v => v.Constraint)
            .Include(v => v.User)
            .AsQueryable();

        if (tenantId.HasValue)
        {
            query = query.Where(v => v.Constraint.TenantId == tenantId.Value);
        }

        return await query
            .OrderByDescending(v => v.DetectedAt)
            .ToListAsync(ct);
    }

    public async Task<SodViolation> ResolveViolationAsync(Guid violationId, Guid resolvedByUserId, string resolution, CancellationToken ct = default)
    {
        var violation = await _context.SodViolations
            .Include(v => v.Constraint)
            .FirstOrDefaultAsync(v => v.Id == violationId, ct)
            ?? throw new InvalidOperationException($"SoD violation {violationId} not found");

        if (violation.ResolvedAt != null)
            throw new InvalidOperationException($"SoD violation {violationId} is already resolved");

        violation.Resolution = resolution;
        violation.ResolvedAt = DateTime.UtcNow;
        violation.ResolvedByUserId = resolvedByUserId;

        _context.AuditLogs.Add(new AuditLog
        {
            UserId = resolvedByUserId,
            TenantId = violation.Constraint.TenantId,
            Action = "SodViolationResolved",
            Resource = "SodViolation",
            Details = JsonSerializer.Serialize(new
            {
                violationId,
                resolution,
                resolvedByUserId
            })
        });

        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("SoD violation {ViolationId} resolved by {ResolvedById}: {Resolution}",
            violationId, resolvedByUserId, resolution);

        return violation;
    }

    // ─── Private Helpers ─────────────────────────────────────

    private async Task PublishEventAsync(string eventType, Delegation delegation, CancellationToken ct)
    {
        try
        {
            await _eventBus.PublishAsync(eventType, new
            {
                delegationId = delegation.Id,
                delegatorUserId = delegation.DelegatorUserId,
                delegateUserId = delegation.DelegateUserId,
                tenantId = delegation.TenantId,
                status = delegation.Status.ToString(),
                validFrom = delegation.ValidFrom,
                validUntil = delegation.ValidUntil
            }, delegation.TenantId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish event {EventType} for delegation {DelegationId}", eventType, delegation.Id);
        }
    }
}
