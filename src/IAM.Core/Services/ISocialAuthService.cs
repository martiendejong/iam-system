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
    /// Admin listing (task 4739): full configuration, never the client secret. A global admin sees every
    /// provider (optionally one tenant's plus platform-wide ones); a tenant admin sees their administered
    /// tenants' providers plus platform-wide ones, and asking for another tenant is refused.
    /// </summary>
    /// <exception cref="IdentityProviderAccessDeniedException">The actor is not an admin, or asked for a tenant they do not administer.</exception>
    Task<List<IdentityProvider>> GetIdentityProvidersForAdminAsync(Guid? tenantId, IdentityProviderActor actor, CancellationToken ct = default);

    /// <summary>
    /// Admin view of one provider (task 4739); queries one row. Authorization runs before the lookup for
    /// callers with no admin authority at all, and after it for the row's own tenant.
    /// </summary>
    /// <returns>Null when the provider does not exist.</returns>
    /// <exception cref="IdentityProviderAccessDeniedException">The actor may not read this provider.</exception>
    Task<IdentityProvider?> GetIdentityProviderForAdminAsync(Guid id, IdentityProviderActor actor, CancellationToken ct = default);

    /// <summary>
    /// Create a new identity provider configuration. Only a global admin (SuperAdmin/SystemAdmin)
    /// may create a platform-wide provider; a tenant admin may create one for their own tenant.
    /// </summary>
    /// <exception cref="IdentityProviderAccessDeniedException">The actor may not manage providers for the target tenant.</exception>
    /// <exception cref="IdentityProviderValidationException">Unknown tenant, or the default role is unknown or privileged.</exception>
    Task<IdentityProvider> CreateIdentityProviderAsync(IdentityProvider provider, IdentityProviderActor actor, CancellationToken ct = default);

    /// <summary>
    /// Update an existing identity provider configuration. The actor must administer the stored
    /// provider's tenant; only a global admin may move a provider to another tenant (or make it platform-wide).
    /// </summary>
    /// <exception cref="InvalidOperationException">The provider does not exist.</exception>
    /// <exception cref="IdentityProviderAccessDeniedException">The actor may not manage this provider.</exception>
    /// <exception cref="IdentityProviderValidationException">Unknown tenant, or the default role is unknown or privileged.</exception>
    Task<IdentityProvider> UpdateIdentityProviderAsync(Guid id, IdentityProvider provider, IdentityProviderActor actor, CancellationToken ct = default);

    /// <summary>
    /// Delete an identity provider configuration. The actor must administer the stored provider's tenant.
    /// </summary>
    /// <returns>False when the provider does not exist.</returns>
    /// <exception cref="IdentityProviderAccessDeniedException">The actor may not manage this provider.</exception>
    Task<bool> DeleteIdentityProviderAsync(Guid id, IdentityProviderActor actor, CancellationToken ct = default);
}
