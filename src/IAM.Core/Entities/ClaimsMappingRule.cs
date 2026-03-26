using System.ComponentModel.DataAnnotations;

namespace IAM.Core.Entities;

public class ClaimsMappingRule
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The OAuth2/OpenIddict client application this rule applies to.
    /// </summary>
    [Required, MaxLength(255)]
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Optional tenant scope. When null, the rule applies to all tenants for this client.
    /// </summary>
    public Guid? TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>
    /// Where the claim value comes from.
    /// </summary>
    public ClaimSourceType SourceType { get; set; } = ClaimSourceType.UserAttribute;

    /// <summary>
    /// Path to the source value (e.g. "Email", "FirstName", "Department",
    /// group name for GroupMembership, role name for RolePermission,
    /// static value for Static, external attribute path for External).
    /// </summary>
    [Required, MaxLength(500)]
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>
    /// The claim type in the token (e.g. "email", "custom:department", "https://myapp.com/roles").
    /// </summary>
    [Required, MaxLength(500)]
    public string TargetClaim { get; set; } = string.Empty;

    /// <summary>
    /// Optional transformation to apply to the source value before emitting the claim.
    /// </summary>
    public ClaimTransform Transform { get; set; } = ClaimTransform.None;

    /// <summary>
    /// Pattern used by Format/Join/Split transforms (e.g. format string, delimiter).
    /// </summary>
    [MaxLength(500)]
    public string? TransformPattern { get; set; }

    /// <summary>
    /// Processing order. Lower priority rules are evaluated first.
    /// </summary>
    public int Priority { get; set; } = 100;

    /// <summary>
    /// Whether this rule is currently active.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum ClaimSourceType
{
    UserAttribute = 0,
    GroupMembership = 1,
    RolePermission = 2,
    Static = 3,
    External = 4
}

public enum ClaimTransform
{
    None = 0,
    ToUpper = 1,
    ToLower = 2,
    Join = 3,
    Split = 4,
    Format = 5
}
