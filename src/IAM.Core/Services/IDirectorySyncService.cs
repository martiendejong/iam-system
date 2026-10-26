using IAM.Core.Entities;

namespace IAM.Core.Services;

public interface IDirectorySyncService
{
    /// <summary>
    /// Create a new directory sync configuration (task 4698). The LDAP URL must be ldaps://, and for a caller
    /// who is not a SuperAdmin its host must resolve only to public addresses; the group-to-role mapping may
    /// only name tenant roles; the bind password is stored encrypted.
    /// </summary>
    /// <exception cref="DirectorySyncValidationException">Unknown tenant, bad URL, or a mapping that names a platform-wide, unknown or foreign role.</exception>
    Task<DirectorySyncConfig> CreateConfigAsync(DirectorySyncConfig config, bool callerIsSuperAdmin = false, CancellationToken ct = default);

    /// <summary>
    /// Get a sync configuration by ID
    /// </summary>
    Task<DirectorySyncConfig?> GetConfigAsync(Guid configId, CancellationToken ct = default);

    /// <summary>
    /// The tenant a configuration belongs to, or null when it does not exist (one narrow query, for authorization).
    /// </summary>
    Task<Guid?> GetConfigTenantIdAsync(Guid configId, CancellationToken ct = default);

    /// <summary>
    /// Get all sync configurations for a tenant
    /// </summary>
    Task<List<DirectorySyncConfig>> GetConfigsByTenantAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Update an existing sync configuration. The tenant never changes; same validation as create.
    /// </summary>
    /// <exception cref="InvalidOperationException">The configuration does not exist.</exception>
    /// <exception cref="DirectorySyncValidationException">Bad URL, or a mapping that names a platform-wide, unknown or foreign role.</exception>
    Task<DirectorySyncConfig> UpdateConfigAsync(DirectorySyncConfig config, bool callerIsSuperAdmin = false, CancellationToken ct = default);

    /// <summary>
    /// Delete a sync configuration
    /// </summary>
    Task<bool> DeleteConfigAsync(Guid configId, CancellationToken ct = default);

    /// <summary>
    /// Test LDAP connection using the given configuration
    /// </summary>
    Task<DirectoryTestResult> TestConnectionAsync(Guid configId, CancellationToken ct = default);

    /// <summary>
    /// Run a full sync (fetches all users matching filter)
    /// </summary>
    Task<DirectorySyncLog> RunFullSyncAsync(Guid configId, CancellationToken ct = default);

    /// <summary>
    /// Run a delta sync (only users changed since last sync based on whenChanged attribute)
    /// </summary>
    Task<DirectorySyncLog> RunDeltaSyncAsync(Guid configId, CancellationToken ct = default);

    /// <summary>
    /// Get sync logs for a configuration
    /// </summary>
    Task<List<DirectorySyncLog>> GetSyncLogsAsync(Guid configId, int limit = 50, CancellationToken ct = default);

    /// <summary>
    /// Get all active configs that are due for automatic sync
    /// </summary>
    Task<List<DirectorySyncConfig>> GetConfigsDueForSyncAsync(CancellationToken ct = default);

    /// <summary>
    /// Encrypts bind passwords that were stored in plain text before task 4698. Idempotent.
    /// </summary>
    /// <returns>How many configurations were upgraded.</returns>
    Task<int> EncryptLegacyBindPasswordsAsync(CancellationToken ct = default);
}

public class DirectoryTestResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int? UserCount { get; set; }
    public string? ServerType { get; set; }
}

/// <summary>Thrown for an invalid directory-sync payload; controllers map it to HTTP 400 (task 4698).</summary>
public class DirectorySyncValidationException : Exception
{
    public DirectorySyncValidationException(string message) : base(message)
    {
    }
}
