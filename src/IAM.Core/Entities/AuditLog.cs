namespace IAM.Core.Entities;

/// <summary>
/// Immutable audit log for all actions in the system
/// </summary>
public class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? UserId { get; set; }
    public User? User { get; set; }

    public Guid? TenantId { get; set; }

    /// <summary>
    /// Action performed (e.g., "Login", "RoleAssigned", "PermissionGranted")
    /// </summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>
    /// Resource affected (e.g., "User", "Role", "Building")
    /// </summary>
    public string Resource { get; set; } = string.Empty;

    /// <summary>
    /// JSON details about the action
    /// </summary>
    public string? Details { get; set; }

    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
