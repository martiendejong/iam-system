namespace IAM.Core.Entities;

public class EmailTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    /// <summary>
    /// Identifies which system email this template overrides, e.g. "Invitation".
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Supports placeholders: {{InviterName}}, {{TenantName}}, {{RoleName}}, {{InviteUrl}}, {{Email}}
    /// </summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>
    /// Supports the same placeholders as Subject.
    /// </summary>
    public string BodyHtml { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
