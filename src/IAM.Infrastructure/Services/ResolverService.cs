using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace IAM.Infrastructure.Services;

/// <summary>
/// Authority-chain resolver (task 4059).
///
/// Chain-walk rules (user / service-account targets):
///   - Walk the ManagerUserId link from the starting principal upward.
///   - Skip managers that are inactive (IsActive = false); continue through them to
///     their own manager so a single disabled manager does not break the chain.
///   - Stop when we hit a manager that is in a different tenant from the starting
///     principal (tenant boundary enforcement matching task 4057 assignment rules).
///   - Stop at <see cref="MaxChainHops"/> active hops and set TruncatedAt8Hops = true.
///   - A visited-set guards against cycles in pre-existing data.
///
/// Group targets: return active, non-expired owners then admins.
/// </summary>
public class ResolverService : IResolverService
{
    /// <summary>Maximum authority hops returned by the resolver.</summary>
    public const int MaxChainHops = 8;

    private readonly IAMDbContext _context;

    public ResolverService(IAMDbContext context)
    {
        _context = context;
    }

    public async Task<ResolverResult> ResolveUserChainAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _context.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user == null)
            return ResolverResult.NotFound($"User {userId} not found.");

        var subjectTenantIds = await GetUserTenantIdsAsync(userId, ct);

        return await WalkManagerChainAsync(
            startingManagerId: user.ManagerUserId,
            subjectTenantIds: subjectTenantIds,
            ct);
    }

    public async Task<ResolverResult> ResolveServiceAccountChainAsync(Guid serviceAccountId, CancellationToken ct = default)
    {
        var sa = await _context.ServiceAccounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == serviceAccountId, ct);

        if (sa == null)
            return ResolverResult.NotFound($"Service account {serviceAccountId} not found.");

        var subjectTenantIds = sa.TenantId.HasValue
            ? new HashSet<Guid> { sa.TenantId.Value }
            : new HashSet<Guid>();

        return await WalkManagerChainAsync(
            startingManagerId: sa.ManagerUserId,
            subjectTenantIds: subjectTenantIds,
            ct);
    }

    public async Task<ResolverResult> ResolveGroupChainAsync(Guid groupId, CancellationToken ct = default)
    {
        var group = await _context.Groups.AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == groupId, ct);

        if (group == null)
            return ResolverResult.NotFound($"Group {groupId} not found.");

        var now = DateTime.UtcNow;

        // Active, non-expired memberships with a meaningful role
        var memberships = await _context.GroupMemberships
            .AsNoTracking()
            .Where(m => m.GroupId == groupId
                        && m.IsActive
                        && (m.ExpiresAt == null || m.ExpiresAt > now))
            .Join(_context.Users.AsNoTracking(),
                m => m.UserId,
                u => u.Id,
                (m, u) => new { m.UserId, m.Role, u.FirstName, u.LastName, u.Email, u.IsActive })
            .Where(x => x.IsActive)
            .ToListAsync(ct);

        var chain = new List<AuthorityHop>();

        // Owners first, then admins
        foreach (var rolePriority in new[] { "owner", "admin" })
        {
            foreach (var m in memberships.Where(m =>
                         string.Equals(m.Role, rolePriority, StringComparison.OrdinalIgnoreCase)))
            {
                chain.Add(new AuthorityHop(
                    PrincipalId: m.UserId,
                    DisplayName: $"{m.FirstName} {m.LastName}".Trim(),
                    Kind: "Human",
                    IamSubject: $"user:{m.Email}",
                    HopRole: $"group-{rolePriority}",
                    TenantId: group.TenantId));
            }
        }

        return ResolverResult.Ok(chain);
    }

    // --- private helpers ---

    /// <summary>
    /// Walk the manager chain iteratively, one hop at a time (max <see cref="MaxChainHops"/>
    /// per-hop queries). This avoids a full Users-table scan: the chain is at most 8 hops deep,
    /// so at most 8 user-lookups + 8 tenant-lookups are executed.
    /// </summary>
    private async Task<ResolverResult> WalkManagerChainAsync(
        Guid? startingManagerId,
        HashSet<Guid> subjectTenantIds,
        CancellationToken ct)
    {
        if (!startingManagerId.HasValue)
            return ResolverResult.Ok(Array.Empty<AuthorityHop>());

        var chain = new List<AuthorityHop>();
        var visited = new HashSet<Guid>();
        var current = startingManagerId;
        var truncated = false;

        while (current.HasValue)
        {
            // Cycle guard
            if (!visited.Add(current.Value))
                break;

            var manager = await _context.Users
                .AsNoTracking()
                .Where(u => u.Id == current.Value)
                .Select(u => new { u.Id, u.FirstName, u.LastName, u.Email, u.IsActive, u.ManagerUserId, u.PrincipalKind })
                .FirstOrDefaultAsync(ct);

            if (manager == null)
                break; // Deleted from DB — chain ends

            // Fetch this manager's tenant IDs for boundary check
            var managerTenantIds = await _context.UserRoles
                .AsNoTracking()
                .Where(ur => ur.UserId == current.Value && ur.TenantId != null)
                .Select(ur => ur.TenantId!.Value)
                .Distinct()
                .ToListAsync(ct);

            // Tenant boundary: stop when the manager has no tenant overlap with the subject
            if (!IsInBounds(managerTenantIds, subjectTenantIds))
                break;

            if (manager.IsActive)
            {
                if (chain.Count >= MaxChainHops)
                {
                    truncated = true;
                    break;
                }

                // Deterministic primary tenant: smallest GUID so the value is stable
                // across restarts regardless of query row order.
                var primaryTenantId = managerTenantIds.Count > 0
                    ? managerTenantIds.OrderBy(t => t).First()
                    : (Guid?)null;

                chain.Add(new AuthorityHop(
                    PrincipalId: current.Value,
                    DisplayName: $"{manager.FirstName} {manager.LastName}".Trim(),
                    Kind: manager.PrincipalKind.ToString(),
                    IamSubject: $"user:{manager.Email}",
                    HopRole: "manager",
                    TenantId: primaryTenantId));
            }
            // Inactive managers are silently skipped; the walk continues to their own manager.

            current = manager.ManagerUserId;
        }

        return ResolverResult.Ok(chain, truncated);
    }

    /// <summary>
    /// A manager is "in bounds" when they share at least one tenant with the subject,
    /// or when both parties have no tenant (global principals).
    /// </summary>
    private static bool IsInBounds(IReadOnlyList<Guid> managerTenantIds, HashSet<Guid> subjectTenantIds)
    {
        if (managerTenantIds.Count == 0)
            return subjectTenantIds.Count == 0;

        return managerTenantIds.Any(t => subjectTenantIds.Contains(t));
    }

    private async Task<HashSet<Guid>> GetUserTenantIdsAsync(Guid userId, CancellationToken ct)
    {
        var ids = await _context.UserRoles
            .AsNoTracking()
            .Where(ur => ur.UserId == userId && ur.TenantId != null)
            .Select(ur => ur.TenantId!.Value)
            .Distinct()
            .ToListAsync(ct);
        return ids.ToHashSet();
    }
}
