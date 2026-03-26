using IAM.Core.Entities;

namespace IAM.Core.Services;

public interface ISecretsVaultService
{
    /// <summary>
    /// Create a new secret entry with AES-256 encryption at rest.
    /// </summary>
    Task<SecretEntry> CreateSecretAsync(
        string name,
        string plainTextValue,
        Guid? tenantId = null,
        string secretType = "Generic",
        string? description = null,
        string? rotationScheduleJson = null,
        string? tags = null,
        Guid? createdByUserId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieve a secret entry (metadata only, no decrypted value).
    /// </summary>
    Task<SecretEntry?> GetSecretAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Retrieve and decrypt a secret value. Use sparingly - prefer GetSecretAsync for metadata.
    /// </summary>
    Task<string?> GetSecretValueAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// List secrets filtered by tenant and/or type (metadata only).
    /// </summary>
    Task<List<SecretEntry>> GetSecretsAsync(
        Guid? tenantId = null,
        string? secretType = null,
        bool? isActive = null,
        CancellationToken ct = default);

    /// <summary>
    /// Update secret metadata (name, description, rotation schedule, tags, type, active status).
    /// Does NOT update the encrypted value - use RotateSecretAsync for that.
    /// </summary>
    Task<SecretEntry?> UpdateSecretAsync(
        Guid id,
        string? name = null,
        string? description = null,
        string? rotationScheduleJson = null,
        string? tags = null,
        string? secretType = null,
        bool? isActive = null,
        CancellationToken ct = default);

    /// <summary>
    /// Rotate a secret: encrypt a new value, increment version, create version history entry,
    /// and optionally set a grace period during which both old and new values are valid.
    /// </summary>
    Task<SecretEntry> RotateSecretAsync(
        Guid id,
        string newPlainTextValue,
        string rotationReason = "Manual",
        Guid? rotatedByUserId = null,
        TimeSpan? gracePeriod = null,
        CancellationToken ct = default);

    /// <summary>
    /// Get version history for a secret (metadata only, no decrypted values).
    /// </summary>
    Task<List<SecretVersion>> GetSecretHistoryAsync(Guid secretId, CancellationToken ct = default);

    /// <summary>
    /// Delete (soft-delete by deactivation) a secret entry.
    /// </summary>
    Task<bool> DeleteSecretAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Find secrets that are due for rotation (NextRotationAt <= now).
    /// Used by the background rotation worker.
    /// </summary>
    Task<List<SecretEntry>> GetSecretsDueForRotationAsync(CancellationToken ct = default);
}
