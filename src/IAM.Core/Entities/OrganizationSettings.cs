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
    /// What happens to members whose authenticator app was enrolled before the SHA-1 to SHA-256
    /// upgrade of TOTP and therefore no longer produces valid codes (task 3162).
    /// </summary>
    public LegacyTotpMigrationMode LegacyTotpMigration { get; set; } = LegacyTotpMigrationMode.EmailPin;

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

/// <summary>
/// Tenant policy for members with a legacy (SHA-1) authenticator-app enrollment.
/// Stored as an int - append new modes, never renumber.
/// </summary>
public enum LegacyTotpMigrationMode
{
    /// <summary>
    /// Default. At the member's next sign-in (after the password, or the passwordless proof, has succeeded) the
    /// account moves to e-mail PIN two-factor: a PIN is mailed and has to be entered before any token is issued.
    /// Nothing is mailed in bulk - a PIN only goes out when the member signs in.
    /// </summary>
    EmailPin = 0,

    /// <summary>
    /// Leave legacy enrollments untouched. The tenant handles them itself (for example by resetting the
    /// members' two-factor settings); /api/mfa/status keeps flagging them as <c>legacyTotpEnrollment</c>.
    /// </summary>
    Off = 1
}
