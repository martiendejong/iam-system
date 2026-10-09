using IAM.Core.Entities;

namespace IAM.Core.Services;

/// <summary>
/// Service for managing visitor pre-registration, check-in/out, QR code access,
/// and temporary resource access grants for visitors.
/// </summary>
public interface IVisitorService
{
    /// <summary>
    /// Pre-register a new visitor with optional access grants.
    /// </summary>
    Task<Visitor> PreRegisterVisitorAsync(
        Guid tenantId,
        Guid hostUserId,
        string name,
        string email,
        string? company,
        DateTime visitDate,
        string? purpose,
        List<VisitorAccessGrantRequest>? accessGrants,
        CancellationToken ct = default);

    /// <summary>
    /// Get a paginated list of visitors for a tenant, optionally only those hosted by one user.
    /// </summary>
    Task<List<Visitor>> GetVisitorsAsync(
        Guid tenantId,
        int skip = 0,
        int take = 20,
        VisitorStatus? statusFilter = null,
        Guid? hostUserId = null,
        CancellationToken ct = default);

    /// <summary>
    /// True when the user holds a non-expired role row for the tenant. Users have no tenant column, so this is what
    /// "member of the tenant" means (a role row without a tenant does not count).
    /// </summary>
    Task<bool> IsTenantMemberAsync(
        Guid userId,
        Guid tenantId,
        CancellationToken ct = default);

    /// <summary>
    /// Get a single visitor by ID (with access grants).
    /// </summary>
    Task<Visitor?> GetVisitorAsync(
        Guid visitorId,
        CancellationToken ct = default);

    /// <summary>
    /// Check in a visitor (mark as arrived).
    /// </summary>
    Task<Visitor?> CheckInAsync(
        Guid visitorId,
        CancellationToken ct = default);

    /// <summary>
    /// Check out a visitor (mark as departed).
    /// </summary>
    Task<Visitor?> CheckOutAsync(
        Guid visitorId,
        CancellationToken ct = default);

    /// <summary>
    /// Get visitor QR code data (returns the QR token for the visitor).
    /// </summary>
    Task<string?> GetQrTokenAsync(
        Guid visitorId,
        CancellationToken ct = default);
}

/// <summary>
/// Request DTO for creating a visitor access grant.
/// </summary>
public class VisitorAccessGrantRequest
{
    public List<string> Resources { get; set; } = new();
    public DateTime ValidFrom { get; set; }
    public DateTime ValidUntil { get; set; }
}
