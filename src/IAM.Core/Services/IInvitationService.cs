using IAM.Core.Entities;

namespace IAM.Core.Services;

public interface IInvitationService
{
    /// <summary>
    /// Send a single invitation email to a user
    /// </summary>
    Task<Invitation> SendInvitationAsync(
        string email,
        Guid tenantId,
        Guid roleId,
        Guid invitedByUserId,
        int? expiryDays = null,
        CancellationToken ct = default);

    /// <summary>
    /// Send bulk invitations from parsed CSV data (name, email, role columns)
    /// </summary>
    Task<BulkInviteResult> SendBulkInvitationsAsync(
        IEnumerable<BulkInviteEntry> entries,
        Guid tenantId,
        Guid invitedByUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Accept an invitation by token - creates/links user and assigns tenant + role
    /// </summary>
    Task<InvitationAcceptResult> AcceptInvitationAsync(
        string token,
        string? password,
        string? firstName,
        string? lastName,
        CancellationToken ct = default);

    /// <summary>
    /// Revoke a pending invitation
    /// </summary>
    Task<bool> RevokeInvitationAsync(Guid invitationId, CancellationToken ct = default);

    /// <summary>
    /// Get all invitations for a tenant
    /// </summary>
    Task<List<Invitation>> GetInvitationsByTenantAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Get pending invitations for a tenant
    /// </summary>
    Task<List<Invitation>> GetPendingInvitationsAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Get a specific invitation by token
    /// </summary>
    Task<Invitation?> GetInvitationByTokenAsync(string token, CancellationToken ct = default);
}

public class BulkInviteEntry
{
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? RoleName { get; set; }
}

public class BulkInviteResult
{
    public int TotalProcessed { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public List<BulkInviteError> Errors { get; set; } = new();
}

public class BulkInviteError
{
    public int Row { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
}

public class InvitationAcceptResult
{
    public bool Success { get; set; }
    public User? User { get; set; }
    public string? Error { get; set; }
    public string? WelcomeMessage { get; set; }
}
