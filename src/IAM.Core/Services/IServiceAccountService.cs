using IAM.Core.Entities;

namespace IAM.Core.Services;

public interface IServiceAccountService
{
    /// <summary>
    /// Create a new service account. Returns the entity and the raw client secret (shown once).
    /// </summary>
    Task<(ServiceAccount Account, string RawClientSecret)> CreateAsync(
        string name,
        Guid? tenantId,
        ServiceAccountType type,
        List<string>? permissions = null,
        string? certificateThumbprint = null,
        string? description = null,
        CancellationToken ct = default);

    /// <summary>
    /// Get a service account by ID.
    /// </summary>
    Task<ServiceAccount?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Get a service account by client ID.
    /// </summary>
    Task<ServiceAccount?> GetByClientIdAsync(string clientId, CancellationToken ct = default);

    /// <summary>
    /// List service accounts with optional filters.
    /// </summary>
    Task<List<ServiceAccount>> ListAsync(
        Guid? tenantId = null,
        ServiceAccountType? type = null,
        bool? isActive = null,
        CancellationToken ct = default);

    /// <summary>
    /// Update a service account's metadata (name, permissions, type, description, certificate).
    /// </summary>
    Task<ServiceAccount?> UpdateAsync(
        Guid id,
        string? name = null,
        List<string>? permissions = null,
        ServiceAccountType? type = null,
        string? certificateThumbprint = null,
        string? description = null,
        bool? isActive = null,
        CancellationToken ct = default);

    /// <summary>
    /// Delete (hard delete) a service account.
    /// </summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Rotate the client secret. Returns the new raw secret (shown once).
    /// </summary>
    Task<(bool Success, string? NewRawSecret)> RotateSecretAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Authenticate a service account using client_credentials (client_id + client_secret).
    /// Returns a JWT access token on success.
    /// </summary>
    Task<(bool Success, string? AccessToken, DateTime? ExpiresAt)> AuthenticateAsync(
        string clientId,
        string clientSecret,
        CancellationToken ct = default);

    /// <summary>
    /// Validate mTLS authentication using a certificate thumbprint.
    /// </summary>
    Task<ServiceAccount?> ValidateMtlsAsync(string certificateThumbprint, CancellationToken ct = default);

    /// <summary>
    /// Perform a token exchange per RFC 8693.
    /// Exchanges a subject token for a new token scoped to a target service.
    /// Every check runs before a token is issued and any failure gives the same (false, null, null):
    /// the subject token must be the caller's own (<paramref name="callerId"/>), a user or service-account
    /// token (never an earlier exchanged or device token), its account must still be active, the requested
    /// scopes must be no wider than what the subject holds, and the target must not be IAM's own audience.
    /// The new token never outlives the subject token.
    /// </summary>
    Task<(bool Success, string? AccessToken, DateTime? ExpiresAt)> ExchangeTokenAsync(
        string subjectToken,
        string targetService,
        Guid callerId,
        string? scopes = null,
        CancellationToken ct = default);
}
