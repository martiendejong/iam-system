using IAM.Core.Entities;

namespace IAM.Core.Services;

public interface ISocialAuthService
{
    /// <summary>
    /// Get the authorization URL for the given identity provider
    /// </summary>
    Task<string> GetAuthorizationUrlAsync(Guid providerId, string redirectUri, string state);

    /// <summary>
    /// Handle the OAuth callback, exchange code for tokens, and return JWT auth result
    /// </summary>
    Task<AuthResult> HandleCallbackAsync(Guid providerId, string code, string state);

    /// <summary>
    /// Link an external provider account to an existing IAM user
    /// </summary>
    Task<ExternalLogin> LinkAccountAsync(Guid userId, Guid providerId, string code);

    /// <summary>
    /// Unlink an external provider account from an IAM user
    /// </summary>
    Task<bool> UnlinkAccountAsync(Guid userId, string provider);

    /// <summary>
    /// Get all linked external accounts for a user
    /// </summary>
    Task<List<ExternalLogin>> GetLinkedAccountsAsync(Guid userId);

    /// <summary>
    /// Get all identity providers, optionally filtered by tenant
    /// </summary>
    Task<List<IdentityProvider>> GetIdentityProvidersAsync(Guid? tenantId = null);

    /// <summary>
    /// Create a new identity provider configuration
    /// </summary>
    Task<IdentityProvider> CreateIdentityProviderAsync(IdentityProvider provider);

    /// <summary>
    /// Update an existing identity provider configuration
    /// </summary>
    Task<IdentityProvider> UpdateIdentityProviderAsync(Guid id, IdentityProvider provider);

    /// <summary>
    /// Delete an identity provider configuration
    /// </summary>
    Task<bool> DeleteIdentityProviderAsync(Guid id);
}
