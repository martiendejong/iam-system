namespace IAM.Core.Entities;

/// <summary>
/// Configuration for LDAP/Active Directory synchronization.
/// Each config defines a connection to an external directory and how to map users/groups.
/// </summary>
public class DirectorySyncConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Tenant this directory sync belongs to
    /// </summary>
    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>
    /// Friendly name for this directory connection (e.g., "Corporate AD", "Azure AD")
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// LDAP URL (e.g., "ldap://dc.example.com:389" or "ldaps://dc.example.com:636")
    /// </summary>
    public string LdapUrl { get; set; } = string.Empty;

    /// <summary>
    /// Bind DN for authenticating to the LDAP server (e.g., "cn=admin,dc=example,dc=com")
    /// </summary>
    public string BindDn { get; set; } = string.Empty;

    /// <summary>
    /// Bind password (stored encrypted)
    /// </summary>
    public string BindPassword { get; set; } = string.Empty;

    /// <summary>
    /// Base DN for user searches (e.g., "ou=users,dc=example,dc=com")
    /// </summary>
    public string SearchBase { get; set; } = string.Empty;

    /// <summary>
    /// LDAP search filter for finding users (e.g., "(objectClass=person)")
    /// </summary>
    public string SearchFilter { get; set; } = "(objectClass=person)";

    /// <summary>
    /// How often to run automatic sync, in minutes. 0 = manual only.
    /// </summary>
    public int SyncInterval { get; set; } = 60;

    /// <summary>
    /// JSON mapping of LDAP attributes to IAM User properties.
    /// Example: {"email": "mail", "firstName": "givenName", "lastName": "sn", "phoneNumber": "telephoneNumber"}
    /// </summary>
    public string AttributeMapping { get; set; } = "{}";

    /// <summary>
    /// JSON mapping of LDAP group DNs to IAM Role IDs.
    /// Example: {"cn=admins,ou=groups,dc=example,dc=com": "role-guid-here"}
    /// </summary>
    public string GroupToRoleMapping { get; set; } = "{}";

    /// <summary>
    /// Whether this sync configuration is active
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Timestamp of the last successful sync
    /// </summary>
    public DateTime? LastSyncAt { get; set; }

    /// <summary>
    /// Status of the last sync attempt (e.g., "Success", "Failed", "PartialSuccess")
    /// </summary>
    public string? LastSyncStatus { get; set; }

    // Timestamps
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public ICollection<DirectorySyncLog> SyncLogs { get; set; } = new List<DirectorySyncLog>();
}
