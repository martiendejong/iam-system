using IAM.Core.Entities;

namespace IAM.Core.Services;

/// <summary>
/// Service for managing user consent records for OAuth2 clients.
/// </summary>
public interface IConsentService
{
    /// <summary>
    /// Grant consent for a user to allow an OAuth2 client access to specific scopes.
    /// </summary>
    Task<ConsentRecord> GrantConsentAsync(Guid userId, string clientId, string scopes, string? ipAddress, string? userAgent, CancellationToken ct = default);

    /// <summary>
    /// Revoke consent for a user's OAuth2 client. Sets RevokedAt timestamp.
    /// </summary>
    Task<bool> RevokeConsentAsync(Guid userId, string clientId, CancellationToken ct = default);

    /// <summary>
    /// Check if a user has active consent for all requested scopes on a client.
    /// </summary>
    Task<bool> HasConsentAsync(Guid userId, string clientId, string scopes, CancellationToken ct = default);

    /// <summary>
    /// Get all active (non-revoked) consent records for a user.
    /// </summary>
    Task<List<ConsentRecord>> GetConsentsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Get the active consent record for a specific user and client combination.
    /// </summary>
    Task<ConsentRecord?> GetConsentForClientAsync(Guid userId, string clientId, CancellationToken ct = default);
}
