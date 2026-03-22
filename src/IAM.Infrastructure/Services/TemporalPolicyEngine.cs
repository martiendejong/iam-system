using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IAM.Infrastructure.Services;

/// <summary>
/// Implementation of temporal policy engine for time-based access control
/// </summary>
public class TemporalPolicyEngine : ITemporalPolicyEngine
{
    private readonly IAMDbContext _context;
    private readonly ILogger<TemporalPolicyEngine> _logger;

    public TemporalPolicyEngine(
        IAMDbContext context,
        ILogger<TemporalPolicyEngine> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<bool> IsPolicyActiveAtAsync(
        Guid policyId,
        DateTime dateTime,
        CancellationToken cancellationToken = default)
    {
        var policy = await _context.Policies
            .Include(p => p.ScheduleTemplate)
            .FirstOrDefaultAsync(p => p.Id == policyId, cancellationToken);

        if (policy == null)
            return false;

        // Check if policy is active
        if (!policy.IsActive)
            return false;

        // Check expiration
        if (policy.ExpiresAt.HasValue && dateTime > policy.ExpiresAt.Value)
            return false;

        // If schedule template is specified, check schedule
        if (policy.ScheduleTemplateId.HasValue && policy.ScheduleTemplate != null)
        {
            return policy.ScheduleTemplate.IsActiveAt(dateTime);
        }

        // Otherwise, check simple time constraints
        var timeConstraints = policy.GetTimeConstraints();
        if (timeConstraints != null)
        {
            // Check date range
            if (timeConstraints.StartDate.HasValue && dateTime < timeConstraints.StartDate.Value)
                return false;

            if (timeConstraints.EndDate.HasValue && dateTime > timeConstraints.EndDate.Value)
                return false;

            // Check day of week
            if (timeConstraints.DaysOfWeek.Count > 0)
            {
                var dayOfWeek = (int)dateTime.DayOfWeek;
                if (dayOfWeek == 0) dayOfWeek = 7; // Sunday = 7
                if (!timeConstraints.DaysOfWeek.Contains(dayOfWeek))
                    return false;
            }

            // Check time of day
            if (!string.IsNullOrWhiteSpace(timeConstraints.StartTime) &&
                !string.IsNullOrWhiteSpace(timeConstraints.EndTime))
            {
                var timeOfDay = dateTime.TimeOfDay;
                var startTime = TimeSpan.Parse(timeConstraints.StartTime);
                var endTime = TimeSpan.Parse(timeConstraints.EndTime);

                if (timeOfDay < startTime || timeOfDay > endTime)
                    return false;
            }
        }

        return true;
    }

    public async Task<List<MaintenanceWindow>> GetActiveMaintenanceWindowsAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        // Get maintenance windows that:
        // 1. Are in progress status
        // 2. Current time is within the window
        // 3. Affect this tenant (either direct match or system-wide)
        var windows = await _context.MaintenanceWindows
            .Where(w => w.Status == MaintenanceStatus.InProgress)
            .Where(w => w.StartTime <= now && w.EndTime >= now)
            .Where(w => w.TenantId == null || w.TenantId == tenantId)
            .ToListAsync(cancellationToken);

        return windows;
    }

    public async Task<List<TemporaryAccessGrant>> GetActiveTemporaryAccessAsync(
        Guid userId,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        var grants = await _context.TemporaryAccessGrants
            .Include(g => g.Role)
            .Where(g => g.UserId == userId)
            .Where(g => g.TenantId == tenantId)
            .Where(g => g.Status == TemporaryAccessStatus.Active)
            .Where(g => g.StartTime <= now && g.EndTime >= now)
            .Where(g => !g.MaxUseCount.HasValue || g.CurrentUseCount < g.MaxUseCount.Value)
            .ToListAsync(cancellationToken);

        return grants;
    }

    public async Task<TemporaryAccessGrant> GrantTemporaryAccessAsync(
        Guid userId,
        Guid roleId,
        Guid tenantId,
        DateTime startTime,
        DateTime endTime,
        string justification,
        Guid grantedByUserId,
        CancellationToken cancellationToken = default)
    {
        // Validate user exists
        var userExists = await _context.Users.AnyAsync(u => u.Id == userId, cancellationToken);
        if (!userExists)
            throw new InvalidOperationException($"User {userId} not found");

        // Validate role exists
        var roleExists = await _context.Roles.AnyAsync(r => r.Id == roleId, cancellationToken);
        if (!roleExists)
            throw new InvalidOperationException($"Role {roleId} not found");

        // Validate tenant exists
        var tenantExists = await _context.Tenants.AnyAsync(t => t.Id == tenantId, cancellationToken);
        if (!tenantExists)
            throw new InvalidOperationException($"Tenant {tenantId} not found");

        // Validate time range
        if (endTime <= startTime)
            throw new InvalidOperationException("End time must be after start time");

        var grant = new TemporaryAccessGrant
        {
            UserId = userId,
            RoleId = roleId,
            TenantId = tenantId,
            StartTime = startTime,
            EndTime = endTime,
            Justification = justification,
            Status = startTime <= DateTime.UtcNow ? TemporaryAccessStatus.Active : TemporaryAccessStatus.Pending,
            CreatedByUserId = grantedByUserId,
            ApprovedByUserId = grantedByUserId,
            ApprovedAt = DateTime.UtcNow,
            Name = $"Temporary access for {userId} - {roleId}"
        };

        _context.TemporaryAccessGrants.Add(grant);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Granted temporary access: User={UserId}, Role={RoleId}, Tenant={TenantId}, StartTime={StartTime}, EndTime={EndTime}",
            userId, roleId, tenantId, startTime, endTime);

        return grant;
    }

    public async Task<bool> RevokeTemporaryAccessAsync(
        Guid grantId,
        Guid revokedByUserId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var grant = await _context.TemporaryAccessGrants
            .FirstOrDefaultAsync(g => g.Id == grantId, cancellationToken);

        if (grant == null)
            return false;

        // Can only revoke active or pending grants
        if (grant.Status != TemporaryAccessStatus.Active && grant.Status != TemporaryAccessStatus.Pending)
            return false;

        grant.Revoke(revokedByUserId, reason);
        grant.UpdatedAt = DateTime.UtcNow;
        grant.UpdatedByUserId = revokedByUserId;

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Revoked temporary access: GrantId={GrantId}, RevokedBy={RevokedBy}, Reason={Reason}",
            grantId, revokedByUserId, reason);

        return true;
    }

    public async Task<int> ProcessExpiredGrantsAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var count = 0;

        // Find grants that should be expired
        var expiredGrants = await _context.TemporaryAccessGrants
            .Where(g => g.Status == TemporaryAccessStatus.Active || g.Status == TemporaryAccessStatus.Pending)
            .Where(g => g.EndTime < now)
            .ToListAsync(cancellationToken);

        foreach (var grant in expiredGrants)
        {
            grant.Status = TemporaryAccessStatus.Expired;
            grant.UpdatedAt = DateTime.UtcNow;
            count++;
        }

        // Find grants that reached use limit
        var limitReachedGrants = await _context.TemporaryAccessGrants
            .Where(g => g.Status == TemporaryAccessStatus.Active)
            .Where(g => g.MaxUseCount.HasValue && g.CurrentUseCount >= g.MaxUseCount.Value)
            .ToListAsync(cancellationToken);

        foreach (var grant in limitReachedGrants)
        {
            grant.Status = TemporaryAccessStatus.UseLimitReached;
            grant.UpdatedAt = DateTime.UtcNow;
            count++;
        }

        // Activate pending grants whose start time has passed
        var pendingGrants = await _context.TemporaryAccessGrants
            .Where(g => g.Status == TemporaryAccessStatus.Pending)
            .Where(g => g.StartTime <= now)
            .ToListAsync(cancellationToken);

        foreach (var grant in pendingGrants)
        {
            grant.Status = TemporaryAccessStatus.Active;
            grant.UpdatedAt = DateTime.UtcNow;
            count++;
        }

        if (count > 0)
        {
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Processed {Count} temporary access grants", count);
        }

        return count;
    }

    public async Task<int> UpdateMaintenanceWindowStatusAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var count = 0;

        // Start scheduled maintenance windows
        var toStart = await _context.MaintenanceWindows
            .Where(w => w.Status == MaintenanceStatus.Scheduled)
            .Where(w => w.StartTime <= now)
            .ToListAsync(cancellationToken);

        foreach (var window in toStart)
        {
            window.Status = MaintenanceStatus.InProgress;
            window.ActualStartTime = now;
            window.UpdatedAt = now;
            count++;

            _logger.LogInformation(
                "Started maintenance window: {Name} (Id={Id})",
                window.Name, window.Id);
        }

        // Complete in-progress maintenance windows
        var toComplete = await _context.MaintenanceWindows
            .Where(w => w.Status == MaintenanceStatus.InProgress)
            .Where(w => w.EndTime <= now)
            .ToListAsync(cancellationToken);

        foreach (var window in toComplete)
        {
            window.Status = MaintenanceStatus.Completed;
            window.ActualEndTime = now;
            window.UpdatedAt = now;
            count++;

            _logger.LogInformation(
                "Completed maintenance window: {Name} (Id={Id})",
                window.Name, window.Id);
        }

        if (count > 0)
        {
            await _context.SaveChangesAsync(cancellationToken);
        }

        return count;
    }

    public async Task<(bool IsUnderMaintenance, MaintenancePolicyBehavior? Behavior)> CheckMaintenanceStatusAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        var activeWindows = await GetActiveMaintenanceWindowsAsync(tenantId, cancellationToken);

        if (activeWindows.Count == 0)
            return (false, null);

        // If multiple maintenance windows are active, use the most restrictive policy behavior
        // Priority: Deny > Suspend > Unchanged > Allow
        var behavior = activeWindows
            .Select(w => w.PolicyBehavior)
            .OrderByDescending(b => b)
            .FirstOrDefault();

        return (true, behavior);
    }
}
