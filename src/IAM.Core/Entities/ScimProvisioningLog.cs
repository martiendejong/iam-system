using System.ComponentModel.DataAnnotations;

namespace IAM.Core.Entities;

/// <summary>
/// Tracks SCIM 2.0 provisioning operations for audit and troubleshooting.
/// Each entry records a Create/Update/Delete/Sync operation on a User or Group resource.
/// </summary>
public class ScimProvisioningLog
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>
    /// Operation type: Create, Update, Delete, Sync
    /// </summary>
    [Required, MaxLength(50)]
    public string Operation { get; set; } = string.Empty;

    /// <summary>
    /// Resource type: User, Group
    /// </summary>
    [Required, MaxLength(50)]
    public string ResourceType { get; set; } = string.Empty;

    /// <summary>
    /// The SCIM externalId or internal resource ID
    /// </summary>
    [MaxLength(500)]
    public string? ExternalId { get; set; }

    /// <summary>
    /// The internal IAM resource ID (User.Id or Group.Id)
    /// </summary>
    public Guid? ResourceId { get; set; }

    /// <summary>
    /// Status: Success, Failed, Partial
    /// </summary>
    [Required, MaxLength(50)]
    public string Status { get; set; } = "Success";

    /// <summary>
    /// JSON details of the operation (request/response summary, error messages, etc.)
    /// </summary>
    public string? Details { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
