namespace IAM.Core.Entities;

public class OrganizationSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Tenant this settings record belongs to (one-to-one)
    /// </summary>
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    /// <summary>
    /// JSON array of allowed email domains for invitations (e.g., ["acme.com","example.org"])
    /// Empty array or null means all domains are allowed.
    /// </summary>
    public string AllowedEmailDomains { get; set; } = "[]";

    /// <summary>
    /// Whether MFA is required for all members of this organization
    /// </summary>
    public bool RequireMfa { get; set; }

    /// <summary>
    /// Default role assigned to new members who accept an invitation without a specific role
    /// </summary>
    public Guid? DefaultRoleId { get; set; }
    public Role? DefaultRole { get; set; }

    /// <summary>
    /// Maximum number of members allowed in this organization (0 = unlimited)
    /// </summary>
    public int MaxMembers { get; set; }

    /// <summary>
    /// Custom welcome message shown to new members after accepting an invitation
    /// </summary>
    public string? WelcomeMessage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
