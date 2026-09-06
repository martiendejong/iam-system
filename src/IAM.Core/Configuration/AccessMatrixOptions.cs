namespace IAM.Core.Configuration;

/// <summary>
/// Manifest-driven configuration for the admin access matrix.
/// Each application declares its base access role and an optional set of
/// fine-grained permission roles. Adding a new app or permission is config-only:
/// add an entry under the "AccessMatrix:Applications" section — no code changes.
/// </summary>
public class AccessMatrixOptions
{
    public const string SectionName = "AccessMatrix";

    public List<AccessMatrixApplication> Applications { get; set; } = new();
}

/// <summary>
/// One column in the access matrix: an application with its base access role
/// and declared fine-grained permission roles.
/// </summary>
public class AccessMatrixApplication
{
    /// <summary>
    /// OAuth client_id of the application (matches OpenIddictApplications.ClientId
    /// where the app is a registered relying party).
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Human-friendly name shown as the column header.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Role name that grants base access to the application (the cell checkbox).
    /// Convention: "app:{name}" (e.g. app:workspace), but existing role names
    /// consumed by apps (e.g. JengoAdmin) are used verbatim.
    /// </summary>
    public string BaseRole { get; set; } = string.Empty;

    /// <summary>
    /// Fine-grained permissions within the application (the "..." menu).
    /// Convention: "app:{name}:{permission}".
    /// </summary>
    public List<AccessMatrixPermission> Permissions { get; set; } = new();
}

/// <summary>
/// A fine-grained permission inside an application, mapped to a role claim.
/// </summary>
public class AccessMatrixPermission
{
    /// <summary>
    /// Role name written as a role claim when granted (e.g. "app:vault:approver").
    /// </summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>
    /// Human-friendly label shown in the permission menu (e.g. "Beheerder").
    /// </summary>
    public string Label { get; set; } = string.Empty;

    public string? Description { get; set; }
}
