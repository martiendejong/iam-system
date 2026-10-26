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
        DelegationActor actor,
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
        // A delegation is the delegator's own authority being handed on: only they (or a global admin on
        // their behalf) may create it, and a tenant-scoped token carries no authority in another tenant.
        if (delegatorUserId != actor.UserId && !actor.IsGlobalAdmin)
            throw new DelegationAccessDeniedException("You can only delegate your own permissions");

        if (!actor.IsGlobalAdmin && actor.TenantId.HasValue && actor.TenantId.Value != tenantId)
            throw new DelegationAccessDeniedException("Token is scoped to a different tenant");

        if (delegatorUserId == delegateUserId)
            throw new InvalidOperationException("A user cannot delegate permissions to themselves");

        if (validUntil <= validFrom)
            throw new InvalidOperationException("ValidUntil must be after ValidFrom");

        permissions = (permissions ?? new List<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (permissions.Count == 0)
            throw new InvalidOperationException("At least one permission must be delegated");

        await EnsureDelegatorHoldsAsync(delegatorUserId, tenantId, permissions, ct);

        // Blanket authority is never self-service: the caller-chosen flag can only add approval, not remove it.
        requiresApproval |= permissions.Any(DelegationPermissions.IsWildcard);

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
                requiresApproval,
                // Only present when an admin created it on someone else's behalf, so the audit row says who did.
                createdByUserId = delegatorUserId == actor.UserId ? (Guid?)null : actor.UserId
            }, OmitNullOptions)
        });

        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Delegation {DelegationId} created from user {DelegatorId} to user {DelegateId} for {PermCount} permissions",
            delegation.Id, delegatorUserId, delegateUserId, permissions.Count);

        await PublishEventAsync("delegation.created", delegation, ct);

        return delegation;
    }

    public async Task<Delegation?> GetDelegationAsync(DelegationActor actor, Guid delegationId, CancellationToken ct = default)
    {
        var delegation = await _context.Delegations
            .Include(d => d.DelegatorUser)
            .Include(d => d.DelegateUser)
            .Include(d => d.Tenant)
            .FirstOrDefaultAsync(d => d.Id == delegationId, ct);

        if (delegation != null && !IsParticipant(actor, delegation) && !await IsAdminOfTenantAsync(actor, delegation.TenantId, ct))
            throw new DelegationAccessDeniedException("You can only view delegations you are part of");

        return delegation;
    }

    public async Task<List<Delegation>> GetDelegationsAsync(DelegationActor actor, Guid tenantId, CancellationToken ct = default)
    {
        var query = _context.Delegations
            .Include(d => d.DelegatorUser)
            .Include(d => d.DelegateUser)
            .Include(d => d.Tenant)
            .Where(d => d.TenantId == tenantId);

        if (!await IsAdminOfTenantAsync(actor, tenantId, ct))
            query = query.Where(d => d.DelegatorUserId == actor.UserId || d.DelegateUserId == actor.UserId);

        return await query
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

    public async Task<Delegation> ApproveDelegationAsync(DelegationActor actor, Guid delegationId, CancellationToken ct = default)
    {
        var approverUserId = actor.UserId;

        var delegation = await _context.Delegations
            .FirstOrDefaultAsync(d => d.Id == delegationId, ct)
            ?? throw new InvalidOperationException($"Delegation {delegationId} not found");

        // Four eyes: neither party can approve their own delegation, and the approver must be an admin.
        if (IsParticipant(actor, delegation))
            throw new DelegationAccessDeniedException("The delegator and the delegate cannot approve their own delegation");

        if (!await IsAdminOfTenantAsync(actor, delegation.TenantId, ct))
            throw new DelegationAccessDeniedException("Only an administrator of the tenant can approve a delegation");

        if (delegation.Status != DelegationStatus.PendingApproval)
            throw new InvalidOperationException($"Delegation {delegationId} is not pending approval");

        // A pending delegation may predate the authority checks, or the delegator may have lost the
        // permissions since: approving must never activate authority the delegator does not hold.
        List<string> delegated;
        try
        {
            delegated = JsonSerializer.Deserialize<List<string>>(delegation.Permissions) ?? new List<string>();
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"Delegation {delegationId} has unreadable permissions");
        }

        var notHeld = await FindNotHeldAsync(delegation.DelegatorUserId, delegation.TenantId, delegated, ct);
        if (notHeld.Count > 0)
            throw new InvalidOperationException(
                $"The delegator no longer holds: {string.Join(", ", notHeld)}");

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

    public async Task<Delegation> RevokeDelegationAsync(DelegationActor actor, Guid delegationId, CancellationToken ct = default)
    {
        var revokedByUserId = actor.UserId;

        var delegation = await _context.Delegations
            .FirstOrDefaultAsync(d => d.Id == delegationId, ct)
            ?? throw new InvalidOperationException($"Delegation {delegationId} not found");

        if (!IsParticipant(actor, delegation) && !await IsAdminOfTenantAsync(actor, delegation.TenantId, ct))
            throw new DelegationAccessDeniedException("Only the delegator, the delegate or an administrator can revoke a delegation");

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
        // Get direct role permissions (a time-limited role past its ExpiresAt no longer counts: it would
        // otherwise let a lapsed contractor role be delegated onward, task 4712)
        var roleNow = DateTime.UtcNow;
        var directPermissions = await _context.UserRoles
            .Include(ur => ur.Role)
            .Where(ur => ur.UserId == userId
                && (ur.TenantId == tenantId || ur.TenantId == null)
                && (ur.ExpiresAt == null || ur.ExpiresAt > roleNow))
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

    public async Task<List<string>> GetEffectivePermissionsAsync(DelegationActor actor, Guid userId, Guid tenantId, CancellationToken ct = default)
    {
        if (userId != actor.UserId && !await IsAdminOfTenantAsync(actor, tenantId, ct))
            throw new DelegationAccessDeniedException("You can only view your own effective permissions");

        return await GetEffectivePermissionsAsync(userId, tenantId, ct);
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

    public async Task<SodConstraint?> GetConstraintAsync(DelegationActor actor, Guid constraintId, CancellationToken ct = default)
    {
        var constraint = await _context.SodConstraints
            .Include(c => c.Tenant)
            .Include(c => c.RoleA)
            .Include(c => c.RoleB)
            .FirstOrDefaultAsync(c => c.Id == constraintId, ct);

        if (constraint != null && !await IsAdminOfTenantAsync(actor, constraint.TenantId, ct))
            throw new DelegationAccessDeniedException("Only an administrator of the tenant can read its segregation-of-duties rules");

        return constraint;
    }

    public async Task<List<SodConstraint>> GetConstraintsAsync(DelegationActor actor, Guid tenantId, CancellationToken ct = default)
    {
        if (!await IsAdminOfTenantAsync(actor, tenantId, ct))
            throw new DelegationAccessDeniedException("Only an administrator of the tenant can read its segregation-of-duties rules");

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

    public async Task<List<SodViolation>> CheckSodViolationsAsync(DelegationActor actor, Guid userId, CancellationToken ct = default)
    {
        // Get all roles for this user
        var userRoles = await _context.UserRoles
            .Where(ur => ur.UserId == userId)
            .Select(ur => new { ur.RoleId, ur.TenantId })
            .ToListAsync(ct);

        // Task 4741: a check writes violation and audit rows, so it needs the caller's authority over the user
        // first. Own id always; another user needs a global admin or an admin of every tenant they hold roles in.
        if (userId != actor.UserId && !actor.IsGlobalAdmin)
        {
            var tenantIds = userRoles.Select(ur => ur.TenantId).Distinct().ToList();
            var allowed = tenantIds.Count > 0 && tenantIds.All(t => t.HasValue);
            if (allowed)
            {
                foreach (var tenantId in tenantIds)
                {
                    if (!await IsAdminOfTenantAsync(actor, tenantId!.Value, ct))
                    {
                        allowed = false;
                        break;
                    }
                }
            }

            if (!allowed)
                throw new DelegationAccessDeniedException("You can only check your own roles, unless you administer the user's tenant");
        }

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

    public async Task<List<SodViolation>> GetViolationsAsync(DelegationActor actor, Guid? tenantId = null, CancellationToken ct = default)
    {
        var query = _context.SodViolations
            .Include(v => v.Constraint)
            .Include(v => v.User)
            .AsQueryable();

        if (tenantId.HasValue)
        {
            query = query.Where(v => v.Constraint.TenantId == tenantId.Value);
        }

        // Task 4741: everything for a global admin (or an admin of the asked tenant); otherwise only violations
        // about the caller themselves.
        var seesAll = tenantId.HasValue
            ? await IsAdminOfTenantAsync(actor, tenantId.Value, ct)
            : actor.IsGlobalAdmin;
        if (!seesAll)
            query = query.Where(v => v.UserId == actor.UserId);

        return await query
            .OrderByDescending(v => v.DetectedAt)
            .ToListAsync(ct);
    }

    public async Task<SodViolation> ResolveViolationAsync(DelegationActor actor, Guid violationId, string resolution, CancellationToken ct = default)
    {
        var resolvedByUserId = actor.UserId;
        var violation = await _context.SodViolations
            .Include(v => v.Constraint)
            .FirstOrDefaultAsync(v => v.Id == violationId, ct)
            ?? throw new InvalidOperationException($"SoD violation {violationId} not found");

        // Task 4741: before any change. Never the user the violation is about (even an admin), and only an admin
        // of the violation's tenant (derived through its constraint).
        if (violation.UserId == actor.UserId)
            throw new DelegationAccessDeniedException("You cannot resolve a violation about yourself");
        if (!await IsAdminOfTenantAsync(actor, violation.Constraint.TenantId, ct))
            throw new DelegationAccessDeniedException("Only an administrator of the tenant can resolve a segregation-of-duties violation");

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

    private static readonly JsonSerializerOptions OmitNullOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static bool IsParticipant(DelegationActor actor, Delegation delegation) =>
        delegation.DelegatorUserId == actor.UserId || delegation.DelegateUserId == actor.UserId;

    /// <summary>
    /// True for a global admin, or for an active TenantAdmin UserRole scoped to exactly this tenant. Role
    /// claims are global names and password-login tokens carry no tenant_id, so tenant authority comes from
    /// the caller's UserRoles rows; a tenant-scoped token only carries it for its own tenant.
    /// </summary>
    private async Task<bool> IsAdminOfTenantAsync(DelegationActor actor, Guid tenantId, CancellationToken ct)
    {
        if (actor.IsGlobalAdmin)
            return true;

        if (actor.TenantId.HasValue && actor.TenantId.Value != tenantId)
            return false;

        var now = DateTime.UtcNow;
        return await _context.UserRoles
            .AsNoTracking()
            .AnyAsync(ur => ur.UserId == actor.UserId
                && ur.TenantId == tenantId
                && (ur.ExpiresAt == null || ur.ExpiresAt > now)
                && ur.Role.Name == ApiKeyIssueRules.TenantAdminRoleName, ct);
    }

    private async Task EnsureDelegatorHoldsAsync(Guid delegatorUserId, Guid tenantId, List<string> permissions, CancellationToken ct)
    {
        var notHeld = await FindNotHeldAsync(delegatorUserId, tenantId, permissions, ct);
        if (notHeld.Count > 0)
            throw new DelegationAccessDeniedException(
                $"The delegator does not hold these permissions in this tenant: {string.Join(", ", notHeld)}");
    }

    /// <summary>The requested permissions the user does not currently hold (own roles or delegated) in the tenant.</summary>
    private async Task<List<string>> FindNotHeldAsync(Guid userId, Guid tenantId, IEnumerable<string> requested, CancellationToken ct)
    {
        var held = await GetEffectivePermissionsAsync(userId, tenantId, ct);
        return requested.Where(p => !DelegationPermissions.Covers(held, p)).ToList();
    }

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
