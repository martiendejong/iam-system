using IAM.Core.Entities;

namespace IAM.Core.Services;

public interface IDirectorySyncService
{
    /// <summary>
    /// Create a new directory sync configuration
    /// </summary>
    Task<DirectorySyncConfig> CreateConfigAsync(DirectorySyncConfig config, CancellationToken ct = default);

    /// <summary>
    /// Get a sync configuration by ID
    /// </summary>
    Task<DirectorySyncConfig?> GetConfigAsync(Guid configId, CancellationToken ct = default);

    /// <summary>
    /// Get all sync configurations for a tenant
    /// </summary>
    Task<List<DirectorySyncConfig>> GetConfigsByTenantAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Update an existing sync configuration
    /// </summary>
    Task<DirectorySyncConfig> UpdateConfigAsync(DirectorySyncConfig config, CancellationToken ct = default);

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
}

public class DirectoryTestResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int? UserCount { get; set; }
    public string? ServerType { get; set; }
}
