using System.Text.Json;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace IAM.Infrastructure.Services;

/// <summary>
/// Manager link + principal kind assignment (task 4057).
///
/// Rules enforced here (identical for users and service accounts):
/// - manager must be an existing user (may be disabled - that is allowed);
/// - manager = self is rejected (users);
/// - any cycle through the manager chain is rejected;
/// - the resulting chain may be at most <see cref="MaxChainDepth"/> principals deep,
///   counting the ancestors of the new manager, the moved principal itself AND the
///   longest chain of reports below the moved principal (direct service-account
///   reports count as one leaf level);
/// - unless the caller is SuperAdmin, the manager must share at least one tenant
///   with the report (a user's tenants = distinct UserRole.TenantId values; a
///   service account's tenant = its TenantId; two principals without any tenant
///   also count as "same tenant").
///
/// Race safety: validation and write share one transaction. On PostgreSQL an
/// advisory transaction lock serializes concurrent assignments; both the
/// transaction and the lock are skipped on non-relational providers so the EF
/// InMemory test provider never sees Npgsql-only calls.
/// </summary>
public class PrincipalDirectoryService : IPrincipalDirectoryService
{
    /// <summary>Maximum number of principals in a manager chain (inclusive).</summary>
    public const int MaxChainDepth = 8;

    /// <summary>Advisory lock key for pg_advisory_xact_lock (task number).</summary>
    private const long AdvisoryLockKey = 4057;

    private readonly IAMDbContext _context;

    public PrincipalDirectoryService(IAMDbContext context)
    {
        _context = context;
    }

    public async Task<PrincipalAssignmentResult> SetUserPrincipalAsync(
        Guid userId,
        Guid? managerUserId,
        PrincipalKind kind,
        Guid? actorUserId,
        bool callerIsSuperAdmin,
        CancellationToken ct = default)
    {
        await using var tx = await BeginTransactionIfRelationalAsync(ct);

        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user == null)
            return PrincipalAssignmentResult.NotFound();

        if (managerUserId.HasValue)
        {
            if (managerUserId.Value == userId)
                return PrincipalAssignmentResult.Invalid("A user cannot be their own manager.");

            var validation = await ValidateManagerAsync(
                managerUserId.Value,
                movedUserId: userId,
                reportTenantIds: await GetUserTenantIdsAsync(userId, ct),
                callerIsSuperAdmin,
                ct);
            if (validation != null)
                return validation;
        }

        var beforeManager = user.ManagerUserId;
        var beforeKind = user.PrincipalKind;

        if (beforeManager != managerUserId || beforeKind != kind)
        {
            user.ManagerUserId = managerUserId;
            user.PrincipalKind = kind;
            user.UpdatedAt = DateTime.UtcNow;

            AddAuditEntry(actorUserId, "User", userId, beforeManager, beforeKind, managerUserId, kind);
            await _context.SaveChangesAsync(ct);
        }

        if (tx != null)
            await tx.CommitAsync(ct);

        return PrincipalAssignmentResult.Success();
    }

    public async Task<PrincipalAssignmentResult> SetServiceAccountPrincipalAsync(
        Guid serviceAccountId,
        Guid? managerUserId,
        PrincipalKind kind,
        Guid? actorUserId,
        bool callerIsSuperAdmin,
        CancellationToken ct = default)
    {
        await using var tx = await BeginTransactionIfRelationalAsync(ct);

        var account = await _context.ServiceAccounts.FirstOrDefaultAsync(a => a.Id == serviceAccountId, ct);
        if (account == null)
            return PrincipalAssignmentResult.NotFound();

        if (managerUserId.HasValue)
        {
            var reportTenantIds = account.TenantId.HasValue
                ? new HashSet<Guid> { account.TenantId.Value }
                : new HashSet<Guid>();

            var validation = await ValidateManagerAsync(
                managerUserId.Value,
                movedUserId: null, // a service account is a leaf: it manages nobody
                reportTenantIds,
                callerIsSuperAdmin,
                ct);
            if (validation != null)
                return validation;
        }

        var beforeManager = account.ManagerUserId;
        var beforeKind = account.PrincipalKind;

        if (beforeManager != managerUserId || beforeKind != kind)
        {
            account.ManagerUserId = managerUserId;
            account.PrincipalKind = kind;
            account.UpdatedAt = DateTime.UtcNow;

            AddAuditEntry(actorUserId, "ServiceAccount", serviceAccountId, beforeManager, beforeKind, managerUserId, kind);
            await _context.SaveChangesAsync(ct);
        }

        if (tx != null)
            await tx.CommitAsync(ct);

        return PrincipalAssignmentResult.Success();
    }

    /// <summary>
    /// Shared manager validation: existence, cycles, chain depth, tenant overlap.
    /// Returns null when the manager is acceptable.
    /// </summary>
    private async Task<PrincipalAssignmentResult?> ValidateManagerAsync(
        Guid managerUserId,
        Guid? movedUserId,
        HashSet<Guid> reportTenantIds,
        bool callerIsSuperAdmin,
        CancellationToken ct)
    {
        var managerExists = await _context.Users.AnyAsync(u => u.Id == managerUserId, ct);
        if (!managerExists)
            return PrincipalAssignmentResult.Invalid("Manager user not found.");

        // One snapshot of the whole user hierarchy: cheap at IAM scale and lets us
        // detect cycles and measure depth without N round-trips.
        var managerMap = await _context.Users
            .Select(u => new { u.Id, u.ManagerUserId })
            .ToDictionaryAsync(u => u.Id, u => u.ManagerUserId, ct);

        // Walk UP from the new manager: cycle + ancestor count.
        var ancestorCount = 0;
        var visited = new HashSet<Guid>();
        Guid? current = managerUserId;
        while (current.HasValue)
        {
            if (movedUserId.HasValue && current.Value == movedUserId.Value)
                return PrincipalAssignmentResult.Invalid(
                    "This manager assignment would create a cycle in the management chain.");

            if (!visited.Add(current.Value))
                return PrincipalAssignmentResult.Invalid(
                    "The existing management chain above this manager contains a cycle; fix it first.");

            ancestorCount++;
            current = managerMap.TryGetValue(current.Value, out var next) ? next : null;
        }

        // Walk DOWN from the moved principal: longest chain of reports below it.
        var descendantLevels = 0;
        if (movedUserId.HasValue)
        {
            var childrenByManager = managerMap
                .Where(kv => kv.Value.HasValue)
                .GroupBy(kv => kv.Value!.Value)
                .ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToList());

            var saManagerIds = await _context.ServiceAccounts
                .Where(sa => sa.ManagerUserId != null)
                .Select(sa => sa.ManagerUserId!.Value)
                .Distinct()
                .ToListAsync(ct);
            var managesServiceAccounts = saManagerIds.ToHashSet();

            descendantLevels = MeasureDescendantLevels(
                movedUserId.Value, childrenByManager, managesServiceAccounts);
        }

        var chainDepth = ancestorCount + 1 + descendantLevels;
        if (chainDepth > MaxChainDepth)
            return PrincipalAssignmentResult.Invalid(
                $"This assignment would make the management chain {chainDepth} deep " +
                $"(including reports below the moved principal); the maximum is {MaxChainDepth}.");

        if (!callerIsSuperAdmin)
        {
            var managerTenantIds = await GetUserTenantIdsAsync(managerUserId, ct);
            var sameTenant = reportTenantIds.Overlaps(managerTenantIds)
                             || (reportTenantIds.Count == 0 && managerTenantIds.Count == 0);
            if (!sameTenant)
                return PrincipalAssignmentResult.Invalid(
                    "Manager does not belong to any tenant of this principal. Only a SuperAdmin can assign a cross-tenant manager.");
        }

        return null;
    }

    /// <summary>
    /// Longest chain of reports strictly below <paramref name="rootUserId"/>:
    /// 0 = no reports, 1 = direct reports only, etc. A direct service-account
    /// report counts as one leaf level. Iterative DFS; a visited set makes even
    /// (bad) pre-existing cyclic data terminate.
    /// </summary>
    private static int MeasureDescendantLevels(
        Guid rootUserId,
        Dictionary<Guid, List<Guid>> childrenByManager,
        HashSet<Guid> managesServiceAccounts)
    {
        var max = managesServiceAccounts.Contains(rootUserId) ? 1 : 0;
        var visited = new HashSet<Guid> { rootUserId };
        var stack = new Stack<(Guid UserId, int Level)>();
        stack.Push((rootUserId, 0));

        while (stack.Count > 0)
        {
            var (userId, level) = stack.Pop();
            if (!childrenByManager.TryGetValue(userId, out var children))
                continue;

            foreach (var child in children)
            {
                if (!visited.Add(child))
                    continue;

                var childLevel = level + 1;
                var childChain = childLevel + (managesServiceAccounts.Contains(child) ? 1 : 0);
                if (childChain > max) max = childChain;
                stack.Push((child, childLevel));
            }
        }

        return max;
    }

    /// <summary>Distinct non-null tenant ids of a user's role assignments.</summary>
    private async Task<HashSet<Guid>> GetUserTenantIdsAsync(Guid userId, CancellationToken ct)
    {
        var ids = await _context.UserRoles
            .Where(ur => ur.UserId == userId && ur.TenantId != null)
            .Select(ur => ur.TenantId!.Value)
            .Distinct()
            .ToListAsync(ct);
        return ids.ToHashSet();
    }

    private void AddAuditEntry(
        Guid? actorUserId,
        string resource,
        Guid targetId,
        Guid? beforeManager,
        PrincipalKind beforeKind,
        Guid? afterManager,
        PrincipalKind afterKind)
    {
        _context.AuditLogs.Add(new AuditLog
        {
            UserId = actorUserId,
            Action = "PrincipalChanged",
            Resource = resource,
            Details = JsonSerializer.Serialize(new
            {
                targetId,
                before = new { managerUserId = beforeManager, principalKind = beforeKind.ToString() },
                after = new { managerUserId = afterManager, principalKind = afterKind.ToString() }
            })
        });
    }

    /// <summary>
    /// One transaction around validation + write. On PostgreSQL an advisory
    /// transaction-scoped lock serializes concurrent principal assignments (it is
    /// released automatically at commit/rollback). Non-relational providers (EF
    /// InMemory in tests) do not support transactions, so both are skipped there.
    /// </summary>
    private async Task<IDbContextTransaction?> BeginTransactionIfRelationalAsync(CancellationToken ct)
    {
        if (!_context.Database.IsRelational())
            return null;

        var tx = await _context.Database.BeginTransactionAsync(ct);
        if (_context.Database.IsNpgsql())
        {
            await _context.Database.ExecuteSqlRawAsync(
                $"SELECT pg_advisory_xact_lock({AdvisoryLockKey})", ct);
        }

        return tx;
    }
}
