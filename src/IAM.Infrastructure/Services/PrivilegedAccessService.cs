using System.Text.Json;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IAM.Infrastructure.Services;

/// <summary>
/// Implementation of Privileged Access Management (PAM) service.
/// Handles time-boxed privilege elevation, break-glass emergency access,
/// and auto de-escalation with full audit trail.
/// </summary>
public class PrivilegedAccessService : IPrivilegedAccessService
{
    private readonly IAMDbContext _context;
    private readonly ILogger<PrivilegedAccessService> _logger;

    public PrivilegedAccessService(
        IAMDbContext context,
        ILogger<PrivilegedAccessService> logger)
    {
        _context = context;
        _logger = logger;
    }

    // ─── Session Management ──────────────────────────────────

    public async Task<PrivilegedSession> CheckoutAsync(
        Guid userId,
        Guid roleId,
        Guid tenantId,
        string justification,
        int? durationMinutes = null,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        // Look up PAM policy for this role/tenant
        var policy = await GetPolicyForRoleAsync(roleId, tenantId, cancellationToken);

        // Validate justification requirement
        if (policy?.RequireJustification == true && string.IsNullOrWhiteSpace(justification))
            throw new ArgumentException("Justification is required for this privileged role");

        // Determine session duration
        var maxDuration = policy?.MaxDurationMinutes ?? 480;
        var requestedDuration = durationMinutes ?? maxDuration;
        if (requestedDuration > maxDuration)
            throw new ArgumentException($"Requested duration ({requestedDuration}min) exceeds maximum allowed ({maxDuration}min)");

        if (requestedDuration <= 0)
            throw new ArgumentException("Duration must be greater than 0 minutes");

        // Check for existing active session
        var hasActive = await HasActiveSessionAsync(userId, roleId, tenantId, cancellationToken);
        if (hasActive)
            throw new InvalidOperationException("User already has an active privileged session for this role");

        // Generate audit correlation ID for session recording
        var correlationId = $"pam-{Guid.NewGuid():N}";

        // Determine initial status based on policy
        var requiresApproval = policy?.RequireApproval ?? false;

        var session = new PrivilegedSession
        {
            UserId = userId,
            RoleId = roleId,
            TenantId = tenantId,
            Justification = justification,
            ExpiresAt = DateTime.UtcNow.AddMinutes(requestedDuration),
            IpAddress = ipAddress,
            AuditCorrelationId = correlationId,
            PamPolicyId = policy?.Id,
            Status = requiresApproval ? PrivilegedSessionStatus.Pending : PrivilegedSessionStatus.Active,
            CheckedOutAt = requiresApproval ? null : DateTime.UtcNow,
            GrantedAt = DateTime.UtcNow
        };

        _context.PrivilegedSessions.Add(session);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogWarning(
            "PAM CHECKOUT: User:{UserId} Role:{RoleId} Tenant:{TenantId} Duration:{Duration}min Status:{Status} Correlation:{CorrelationId}",
            userId, roleId, tenantId, requestedDuration, session.Status, correlationId);

        return session;
    }

    public async Task<PrivilegedSession> CheckinAsync(
        Guid sessionId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var session = await _context.PrivilegedSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

        if (session == null)
            throw new ArgumentException($"Privileged session {sessionId} not found");

        if (session.UserId != userId)
            throw new InvalidOperationException("Only the session owner can check in");

        if (session.Status != PrivilegedSessionStatus.Active)
            throw new InvalidOperationException($"Session is not active (current status: {session.Status})");

        session.CheckIn();
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "PAM CHECKIN: Session:{SessionId} User:{UserId} Role:{RoleId} Duration:{Duration}min",
            sessionId, userId, session.RoleId,
            (session.CheckedInAt!.Value - session.CheckedOutAt!.Value).TotalMinutes);

        return session;
    }

    public async Task<PrivilegedSession> ApproveSessionAsync(
        Guid sessionId,
        Guid approverUserId,
        CancellationToken cancellationToken = default)
    {
        var session = await _context.PrivilegedSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

        if (session == null)
            throw new ArgumentException($"Privileged session {sessionId} not found");

        if (session.Status != PrivilegedSessionStatus.Pending)
            throw new InvalidOperationException($"Session is not pending approval (current status: {session.Status})");

        if (session.UserId == approverUserId)
            throw new InvalidOperationException("Cannot approve your own privileged access request");

        session.Status = PrivilegedSessionStatus.Active;
        session.ApprovedByUserId = approverUserId;
        session.CheckedOutAt = DateTime.UtcNow;
        session.ExpiresAt = DateTime.UtcNow.AddMinutes(
            (session.ExpiresAt - session.GrantedAt).TotalMinutes); // Reset duration from approval time
        session.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogWarning(
            "PAM APPROVED: Session:{SessionId} User:{UserId} ApprovedBy:{ApproverId} Role:{RoleId}",
            sessionId, session.UserId, approverUserId, session.RoleId);

        return session;
    }

    public async Task<PrivilegedSession> DenySessionAsync(
        Guid sessionId,
        Guid approverUserId,
        CancellationToken cancellationToken = default)
    {
        var session = await _context.PrivilegedSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

        if (session == null)
            throw new ArgumentException($"Privileged session {sessionId} not found");

        if (session.Status != PrivilegedSessionStatus.Pending)
            throw new InvalidOperationException($"Session is not pending approval (current status: {session.Status})");

        session.Status = PrivilegedSessionStatus.Denied;
        session.ApprovedByUserId = approverUserId;
        session.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "PAM DENIED: Session:{SessionId} User:{UserId} DeniedBy:{ApproverId} Role:{RoleId}",
            sessionId, session.UserId, approverUserId, session.RoleId);

        return session;
    }

    // ─── Break-Glass Emergency Access ────────────────────────

    public async Task<PrivilegedSession> BreakGlassAsync(
        Guid userId,
        Guid roleId,
        Guid tenantId,
        string justification,
        List<Guid> approverUserIds,
        int? durationMinutes = null,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(justification))
            throw new ArgumentException("Justification is required for break-glass access");

        // Look up PAM policy
        var policy = await GetPolicyForRoleAsync(roleId, tenantId, cancellationToken);

        if (policy != null && !policy.BreakGlassEnabled)
            throw new InvalidOperationException("Break-glass access is not enabled for this role");

        // Validate 4-eyes principle: require minimum approvers
        var requiredApprovers = policy?.BreakGlassApproversRequired ?? 2;
        if (approverUserIds.Count < requiredApprovers)
            throw new ArgumentException(
                $"Break-glass requires at least {requiredApprovers} approvers (provided: {approverUserIds.Count})");

        // Ensure requester is not among approvers
        if (approverUserIds.Contains(userId))
            throw new ArgumentException("Requester cannot be one of the break-glass approvers");

        // Ensure all approvers are distinct
        if (approverUserIds.Distinct().Count() != approverUserIds.Count)
            throw new ArgumentException("Duplicate approver IDs are not allowed");

        // Verify all approver users exist
        var approverCount = await _context.Users
            .CountAsync(u => approverUserIds.Contains(u.Id) && u.IsActive, cancellationToken);

        if (approverCount != approverUserIds.Count)
            throw new ArgumentException("One or more approver users not found or inactive");

        // Determine duration (shorter default for break-glass: 60 min)
        var maxDuration = policy?.MaxDurationMinutes ?? 60;
        var requestedDuration = durationMinutes ?? Math.Min(60, maxDuration);
        if (requestedDuration > maxDuration)
            requestedDuration = maxDuration;

        var correlationId = $"pam-bg-{Guid.NewGuid():N}";

        var session = new PrivilegedSession
        {
            UserId = userId,
            RoleId = roleId,
            TenantId = tenantId,
            Justification = justification,
            ExpiresAt = DateTime.UtcNow.AddMinutes(requestedDuration),
            IpAddress = ipAddress,
            AuditCorrelationId = correlationId,
            PamPolicyId = policy?.Id,
            Status = PrivilegedSessionStatus.Active,
            CheckedOutAt = DateTime.UtcNow,
            GrantedAt = DateTime.UtcNow,
            IsBreakGlass = true,
            BreakGlassApprovers = JsonSerializer.Serialize(approverUserIds)
        };

        _context.PrivilegedSessions.Add(session);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogCritical(
            "PAM BREAK-GLASS: User:{UserId} Role:{RoleId} Tenant:{TenantId} Duration:{Duration}min Approvers:{Approvers} Correlation:{CorrelationId} Justification:{Justification}",
            userId, roleId, tenantId, requestedDuration,
            string.Join(",", approverUserIds), correlationId, justification);

        return session;
    }

    // ─── Session Queries ─────────────────────────────────────

    public async Task<List<PrivilegedSession>> GetActiveSessionsAsync(
        Guid? tenantId = null,
        Guid? userId = null,
        CancellationToken cancellationToken = default)
    {
        var query = _context.PrivilegedSessions
            .Include(s => s.User)
            .Include(s => s.Role)
            .Where(s => s.Status == PrivilegedSessionStatus.Active && s.ExpiresAt > DateTime.UtcNow);

        if (tenantId.HasValue)
            query = query.Where(s => s.TenantId == tenantId.Value);

        if (userId.HasValue)
            query = query.Where(s => s.UserId == userId.Value);

        return await query
            .OrderBy(s => s.ExpiresAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<PrivilegedSession>> GetSessionHistoryAsync(
        Guid? userId = null,
        Guid? tenantId = null,
        DateTime? startDate = null,
        DateTime? endDate = null,
        int skip = 0,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        var query = _context.PrivilegedSessions
            .Include(s => s.User)
            .Include(s => s.Role)
            .AsQueryable();

        if (userId.HasValue)
            query = query.Where(s => s.UserId == userId.Value);

        if (tenantId.HasValue)
            query = query.Where(s => s.TenantId == tenantId.Value);

        if (startDate.HasValue)
            query = query.Where(s => s.CreatedAt >= startDate.Value);

        if (endDate.HasValue)
            query = query.Where(s => s.CreatedAt <= endDate.Value);

        return await query
            .OrderByDescending(s => s.CreatedAt)
            .Skip(skip)
            .Take(Math.Min(take, 1000))
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> HasActiveSessionAsync(
        Guid userId,
        Guid roleId,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        return await _context.PrivilegedSessions
            .AnyAsync(s =>
                s.UserId == userId &&
                s.RoleId == roleId &&
                s.TenantId == tenantId &&
                s.Status == PrivilegedSessionStatus.Active &&
                s.ExpiresAt > DateTime.UtcNow,
                cancellationToken);
    }

    // ─── Auto De-escalation ──────────────────────────────────

    public async Task<int> ProcessExpiredSessionsAsync(CancellationToken cancellationToken = default)
    {
        var expiredSessions = await _context.PrivilegedSessions
            .Where(s =>
                s.Status == PrivilegedSessionStatus.Active &&
                s.ExpiresAt <= DateTime.UtcNow)
            .ToListAsync(cancellationToken);

        foreach (var session in expiredSessions)
        {
            session.Expire();

            _logger.LogWarning(
                "PAM AUTO DE-ESCALATION: Session:{SessionId} User:{UserId} Role:{RoleId} ExpiredAfter:{Duration}min",
                session.Id, session.UserId, session.RoleId,
                session.CheckedOutAt.HasValue
                    ? (DateTime.UtcNow - session.CheckedOutAt.Value).TotalMinutes
                    : 0);
        }

        // Also expire sessions stuck in Pending status beyond their ExpiresAt
        var expiredPending = await _context.PrivilegedSessions
            .Where(s =>
                s.Status == PrivilegedSessionStatus.Pending &&
                s.ExpiresAt <= DateTime.UtcNow)
            .ToListAsync(cancellationToken);

        foreach (var session in expiredPending)
        {
            session.Expire();
        }

        var totalExpired = expiredSessions.Count + expiredPending.Count;

        if (totalExpired > 0)
        {
            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "PAM de-escalation: {ActiveCount} active sessions expired, {PendingCount} pending sessions expired",
                expiredSessions.Count, expiredPending.Count);
        }

        return totalExpired;
    }

    // ─── PAM Policy Management ───────────────────────────────

    public async Task<List<PamPolicy>> GetPoliciesAsync(
        Guid? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        var query = _context.PamPolicies
            .Include(p => p.Role)
            .AsQueryable();

        if (tenantId.HasValue)
            query = query.Where(p => p.TenantId == tenantId.Value);

        return await query
            .OrderBy(p => p.Role.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<PamPolicy?> GetPolicyByIdAsync(
        Guid policyId,
        CancellationToken cancellationToken = default)
    {
        return await _context.PamPolicies
            .Include(p => p.Role)
            .FirstOrDefaultAsync(p => p.Id == policyId, cancellationToken);
    }

    public async Task<PamPolicy?> GetPolicyForRoleAsync(
        Guid roleId,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        return await _context.PamPolicies
            .FirstOrDefaultAsync(p =>
                p.RoleId == roleId &&
                p.TenantId == tenantId &&
                p.IsActive,
                cancellationToken);
    }

    public async Task<PamPolicy> CreatePolicyAsync(
        PamPolicy policy,
        CancellationToken cancellationToken = default)
    {
        // Check for duplicate policy (same role+tenant)
        var existing = await _context.PamPolicies
            .AnyAsync(p => p.RoleId == policy.RoleId && p.TenantId == policy.TenantId, cancellationToken);

        if (existing)
            throw new InvalidOperationException("A PAM policy already exists for this role and tenant combination");

        if (policy.MaxDurationMinutes <= 0 || policy.MaxDurationMinutes > 1440)
            throw new ArgumentException("MaxDurationMinutes must be between 1 and 1440 (24 hours)");

        if (policy.BreakGlassApproversRequired < 2)
            throw new ArgumentException("BreakGlassApproversRequired must be at least 2 (4-eyes principle)");

        _context.PamPolicies.Add(policy);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "PAM Policy created: Policy:{PolicyId} Role:{RoleId} Tenant:{TenantId} MaxDuration:{MaxDuration}min",
            policy.Id, policy.RoleId, policy.TenantId, policy.MaxDurationMinutes);

        return policy;
    }

    public async Task<PamPolicy> UpdatePolicyAsync(
        PamPolicy policy,
        CancellationToken cancellationToken = default)
    {
        var existing = await _context.PamPolicies
            .FirstOrDefaultAsync(p => p.Id == policy.Id, cancellationToken);

        if (existing == null)
            throw new ArgumentException($"PAM policy {policy.Id} not found");

        if (policy.MaxDurationMinutes <= 0 || policy.MaxDurationMinutes > 1440)
            throw new ArgumentException("MaxDurationMinutes must be between 1 and 1440 (24 hours)");

        if (policy.BreakGlassApproversRequired < 2)
            throw new ArgumentException("BreakGlassApproversRequired must be at least 2 (4-eyes principle)");

        existing.MaxDurationMinutes = policy.MaxDurationMinutes;
        existing.RequireJustification = policy.RequireJustification;
        existing.RequireApproval = policy.RequireApproval;
        existing.ApproverRoleId = policy.ApproverRoleId;
        existing.BreakGlassEnabled = policy.BreakGlassEnabled;
        existing.BreakGlassApproversRequired = policy.BreakGlassApproversRequired;
        existing.IsActive = policy.IsActive;
        existing.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "PAM Policy updated: Policy:{PolicyId} Role:{RoleId} Tenant:{TenantId}",
            policy.Id, existing.RoleId, existing.TenantId);

        return existing;
    }

    public async Task DeletePolicyAsync(
        Guid policyId,
        CancellationToken cancellationToken = default)
    {
        var policy = await _context.PamPolicies
            .FirstOrDefaultAsync(p => p.Id == policyId, cancellationToken);

        if (policy == null)
            throw new ArgumentException($"PAM policy {policyId} not found");

        _context.PamPolicies.Remove(policy);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "PAM Policy deleted: Policy:{PolicyId} Role:{RoleId} Tenant:{TenantId}",
            policyId, policy.RoleId, policy.TenantId);
    }
}
