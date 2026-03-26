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
    /// Get a paginated list of visitors for a tenant.
    /// </summary>
    Task<List<Visitor>> GetVisitorsAsync(
        Guid tenantId,
        int skip = 0,
        int take = 20,
        VisitorStatus? statusFilter = null,
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
