using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace IAM.Infrastructure.Services;

public class SocialAuthService : ISocialAuthService
{
    // Today's default when an organization has never saved a Token Configuration.
    private const int DefaultRefreshTokenLifetimeDays = 7;

    // Prefix used to detect an encrypted client secret stored in the DB.
    // Non-prefixed values are treated as plaintext (migration compatibility).
    private const string EncryptedPrefix = "enc:";

    // State entries expire after 10 minutes (one full OAuth round-trip budget).
    private static readonly TimeSpan StateEntryTtl = TimeSpan.FromMinutes(10);

    private readonly IAMDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IClaimsMappingService _claimsMappingService;
    private readonly ISecretsVaultService _secretsVault;
    private readonly IMemoryCache _cache;
    private readonly ILogger<SocialAuthService>? _logger;

    public SocialAuthService(
        IAMDbContext context,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        IClaimsMappingService claimsMappingService,
        ISecretsVaultService secretsVault,
        IMemoryCache cache,
        ILogger<SocialAuthService>? logger = null)
    {
        _context = context;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _claimsMappingService = claimsMappingService;
        _secretsVault = secretsVault;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Encrypts a client secret for storage.  The result is prefixed with "enc:" so
    /// Decrypt can distinguish new entries from pre-existing plaintext ones.
    /// </summary>
    private async Task<string> EncryptClientSecretAsync(string plainSecret)
    {
        var entry = await _secretsVault.CreateSecretAsync(
            name: $"idp-client-secret-{Guid.NewGuid():N}",
            plainTextValue: plainSecret,
            secretType: "IdentityProviderClientSecret");
        // Store the SecretEntry ID so we can retrieve the value later.
        return EncryptedPrefix + entry.Id.ToString();
    }

    /// <summary>
    /// Decrypts a stored client secret.  If the value is not prefixed with "enc:",
    /// it is an unencrypted legacy value and is returned as-is (migration compatibility).
    /// </summary>
    private async Task<string> DecryptClientSecretAsync(string storedValue)
    {
        if (!storedValue.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
            return storedValue; // Legacy plaintext — pass through until re-saved.

        var idStr = storedValue[EncryptedPrefix.Length..];
        if (!Guid.TryParse(idStr, out var secretId))
            return storedValue;

        return await _secretsVault.GetSecretValueAsync(secretId) ?? storedValue;
    }

    /// <summary>
    /// Resolves the access/refresh token lifetime for a login, from the user's
    /// organization Token Configuration when one exists, otherwise today's defaults
    /// (Jwt:AccessTokenExpirationMinutes config, hardcoded 7-day refresh). Mirrors
    /// AuthService.ResolveTokenLifetimeAsync so both login paths agree.
    /// </summary>
    private async Task<(int AccessTokenLifetimeMinutes, int RefreshTokenLifetimeDays)> ResolveTokenLifetimeAsync(Guid userId)
    {
        var defaultAccessMinutes = int.Parse(_configuration["Jwt:AccessTokenExpirationMinutes"] ?? "5");
        var orgLifetime = await _claimsMappingService.ResolveTokenLifetimeForUserAsync(userId);

        return orgLifetime != null
            ? (orgLifetime.AccessTokenLifetimeMinutes, orgLifetime.RefreshTokenLifetimeDays)
            : (defaultAccessMinutes, DefaultRefreshTokenLifetimeDays);
    }

    public async Task<string> GetAuthorizationUrlAsync(Guid providerId, string redirectUri, string state)
    {
        var provider = await _context.IdentityProviders.FindAsync(providerId)
            ?? throw new InvalidOperationException("Identity provider not found");

        if (!provider.IsActive)
            throw new InvalidOperationException("Identity provider is not active");

        ValidateRedirectUri(provider, redirectUri);

        // Store state server-side so the callback can validate it (replay + open-redirect protection).
        var cacheKey = StateEntryKey(state);
        _cache.Set(cacheKey, new SocialStateEntry(redirectUri, providerId), StateEntryTtl);

        var (authorizationEndpoint, scopes) = GetProviderEndpoints(provider.Type);

        var queryParams = new Dictionary<string, string>
        {
            ["client_id"] = provider.ClientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = scopes,
            ["state"] = state
        };

        var queryString = string.Join("&", queryParams.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        return $"{authorizationEndpoint}?{queryString}";
    }

    public async Task<AuthResult> HandleCallbackAsync(Guid providerId, string code, string state)
    {
        // Consume the state entry exactly once: prevents replays and validates the origin.
        var cacheKey = StateEntryKey(state);
        if (!_cache.TryGetValue(cacheKey, out SocialStateEntry? stateEntry) || stateEntry == null)
        {
            return new AuthResult
            {
                Success = false,
                Error = "Invalid or expired state parameter"
            };
        }
        _cache.Remove(cacheKey); // one-time use

        if (stateEntry.ProviderId != providerId)
        {
            return new AuthResult
            {
                Success = false,
                Error = "State provider mismatch"
            };
        }

        var provider = await _context.IdentityProviders
            .Include(p => p.DefaultRole)
            .FirstOrDefaultAsync(p => p.Id == providerId);

        if (provider == null || !provider.IsActive)
        {
            return new AuthResult
            {
                Success = false,
                Error = "Identity provider not found or not active"
            };
        }

        // Exchange code for tokens
        var tokenResponse = await ExchangeCodeForTokensAsync(provider, code);
        if (tokenResponse == null)
        {
            return new AuthResult
            {
                Success = false,
                Error = "Failed to exchange authorization code for tokens"
            };
        }

        // Fetch user profile from provider
        var externalUser = await GetExternalUserProfileAsync(provider, tokenResponse);
        if (externalUser == null)
        {
            return new AuthResult
            {
                Success = false,
                Error = "Failed to retrieve user profile from provider"
            };
        }

        // Find existing external login
        var externalLogin = await _context.ExternalLogins
            .Include(el => el.User)
                .ThenInclude(u => u!.UserRoles)
                    .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(el =>
                el.Provider == provider.Type.ToString() &&
                el.ProviderUserId == externalUser.ProviderUserId);

        User user;

        if (externalLogin != null)
        {
            user = externalLogin.User!;

            // A deactivated user must not sign in through a previously linked social identity.
            // Checked before anything is written so a refused attempt leaves the link untouched.
            if (!user.IsActive)
                return RefuseInactiveUser(user, provider);

            // Existing linked account - update last used
            externalLogin.LastUsedAt = DateTime.UtcNow;
            externalLogin.Email = externalUser.Email;
            externalLogin.DisplayName = externalUser.DisplayName;
        }
        else
        {
            // No linked account - try to find user by email or auto-create
            User? existingUser = null;
            if (!string.IsNullOrEmpty(externalUser.Email))
            {
                existingUser = await _context.Users
                    .Include(u => u.UserRoles)
                        .ThenInclude(ur => ur.Role)
                    .FirstOrDefaultAsync(u => u.Email == externalUser.Email);

                // Task 4707: an e-mail address the provider does not vouch for proves nothing (a Microsoft
                // mail/userPrincipalName can be set freely in an attacker's own Entra tenant), so it must never
                // link to or sign in as an existing account. Refused before anything is written.
                if (existingUser != null && !externalUser.EmailVerified)
                    return RefuseUnverifiedEmailMatch(provider);
            }

            if (existingUser == null)
            {
                if (!provider.AutoCreateUsers)
                {
                    return new AuthResult
                    {
                        Success = false,
                        Error = "No account found and automatic account creation is disabled for this provider"
                    };
                }

                // Auto-create user
                user = new User
                {
                    Email = externalUser.Email ?? $"{externalUser.ProviderUserId}@{provider.Type.ToString().ToLower()}.external",
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString(), workFactor: 12),
                    FirstName = externalUser.FirstName ?? "",
                    LastName = externalUser.LastName ?? "",
                    EmailConfirmed = externalUser.EmailVerified, // only when the provider verified the address
                    IsActive = true
                };

                _context.Users.Add(user);

                // Assign default role if configured - never a privileged one. The management API no
                // longer stores such a role, so this only triggers for a row written before that rule
                // (or edited out-of-band): the user is created WITHOUT the role rather than with it.
                if (provider.DefaultRoleId.HasValue)
                {
                    if (provider.DefaultRole != null && !PrivilegedRoles.IsPrivileged(provider.DefaultRole))
                    {
                        var userRole = new UserRole
                        {
                            UserId = user.Id,
                            RoleId = provider.DefaultRoleId.Value,
                            TenantId = provider.TenantId
                        };
                        _context.UserRoles.Add(userRole);
                    }
                    else
                    {
                        _logger?.LogCritical(
                            "Identity provider {ProviderId} ({ProviderName}) has default role {RoleId} ({RoleName}), which is missing or privileged; " +
                            "auto-created user {UserId} was created without a role. Fix the provider's default role.",
                            provider.Id, provider.Name, provider.DefaultRoleId, provider.DefaultRole?.Name, user.Id);
                    }
                }
            }
            else
            {
                // Same rule for an account matched by email: refuse before the new
                // ExternalLogin is added, so no social link is created for a deactivated user.
                if (!existingUser.IsActive)
                    return RefuseInactiveUser(existingUser, provider);

                user = existingUser;
            }

            // Create external login link
            var newExternalLogin = new ExternalLogin
            {
                UserId = user.Id,
                Provider = provider.Type.ToString(),
                ProviderUserId = externalUser.ProviderUserId,
                Email = externalUser.Email,
                DisplayName = externalUser.DisplayName,
                LastUsedAt = DateTime.UtcNow
            };
            _context.ExternalLogins.Add(newExternalLogin);
        }

        // Update last login
        user.LastLoginAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        // Reload user with roles for token generation
        user = await _context.Users
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
            .FirstAsync(u => u.Id == user.Id);

        // Resolve this organization's configured token lifetime (falls back to today's
        // defaults when the user has no tenant or the tenant has no Token Configuration)
        var (accessMinutes, refreshDays) = await ResolveTokenLifetimeAsync(user.Id);

        // Generate JWT tokens
        var refreshToken = GenerateRefreshToken();
        var refreshTokenId = Guid.NewGuid();

        var refreshTokenEntity = new RefreshToken
        {
            Id = refreshTokenId,
            UserId = user.Id,
            TokenHash = HashToken(refreshToken),
            ExpiresAt = DateTime.UtcNow.AddDays(refreshDays)
        };

        _context.RefreshTokens.Add(refreshTokenEntity);
        await _context.SaveChangesAsync();

        var accessToken = GenerateAccessToken(user, accessMinutes, refreshTokenId);

        return new AuthResult
        {
            Success = true,
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            User = user,
            AccessTokenLifetimeMinutes = accessMinutes,
            RefreshTokenLifetimeDays = refreshDays
        };
    }

    /// <summary>
    /// Refusal for a social login whose e-mail matches an existing account but is not verified by the provider
    /// (always the case for Microsoft, which gives no verified signal). The user can still sign in with their
    /// existing method and link the provider explicitly from their account settings (LinkAccountAsync).
    /// </summary>
    private AuthResult RefuseUnverifiedEmailMatch(IdentityProvider provider)
    {
        _logger?.LogWarning(
            "Social login via identity provider {ProviderId} ({ProviderName}) refused: the e-mail matches an existing " +
            "account but the provider does not verify it",
            provider.Id, provider.Name);

        var name = provider.Type.ToString();
        return new AuthResult
        {
            Success = false,
            Error = provider.Type == IdentityProviderType.Microsoft
                ? "An account with this e-mail address already exists. Microsoft sign-in cannot be matched to an existing " +
                  "account by e-mail address: sign in with your existing method and link your Microsoft account from your account settings."
                : $"An account with this e-mail address already exists, but {name} has not verified the address. Verify it with " +
                  $"{name}, or sign in with your existing method and link your {name} account from your account settings."
        };
    }

    /// <summary>
    /// Same refusal AuthService returns to password, OTP, magic-link and passkey sign-in for a
    /// deactivated user. Callers must return it before any mutation or SaveChanges.
    /// </summary>
    private AuthResult RefuseInactiveUser(User user, IdentityProvider provider)
    {
        _logger?.LogWarning(
            "Social login refused for deactivated user {UserId} via identity provider {ProviderId} ({ProviderName})",
            user.Id, provider.Id, provider.Name);

        return new AuthResult
        {
            Success = false,
            Error = "Account is inactive"
        };
    }

    public async Task<ExternalLogin> LinkAccountAsync(Guid userId, Guid providerId, string code)
    {
        var provider = await _context.IdentityProviders.FindAsync(providerId)
            ?? throw new InvalidOperationException("Identity provider not found");

        var user = await _context.Users.FindAsync(userId)
            ?? throw new InvalidOperationException("User not found");

        // Exchange code for tokens
        var tokenResponse = await ExchangeCodeForTokensAsync(provider, code)
            ?? throw new InvalidOperationException("Failed to exchange authorization code for tokens");

        // Fetch user profile
        var externalUser = await GetExternalUserProfileAsync(provider, tokenResponse)
            ?? throw new InvalidOperationException("Failed to retrieve user profile from provider");

        // Check if this external account is already linked to another user
        var existingLink = await _context.ExternalLogins
            .FirstOrDefaultAsync(el =>
                el.Provider == provider.Type.ToString() &&
                el.ProviderUserId == externalUser.ProviderUserId);

        if (existingLink != null)
        {
            if (existingLink.UserId == userId)
                throw new InvalidOperationException("This external account is already linked to your account");
            else
                throw new InvalidOperationException("This external account is already linked to another user");
        }

        var externalLogin = new ExternalLogin
        {
            UserId = userId,
            Provider = provider.Type.ToString(),
            ProviderUserId = externalUser.ProviderUserId,
            Email = externalUser.Email,
            DisplayName = externalUser.DisplayName,
            LastUsedAt = DateTime.UtcNow
        };

        _context.ExternalLogins.Add(externalLogin);
        await _context.SaveChangesAsync();

        return externalLogin;
    }

    public async Task<bool> UnlinkAccountAsync(Guid userId, string provider)
    {
        var externalLogin = await _context.ExternalLogins
            .FirstOrDefaultAsync(el => el.UserId == userId && el.Provider == provider);

        if (externalLogin == null)
            return false;

        _context.ExternalLogins.Remove(externalLogin);
        await _context.SaveChangesAsync();

        return true;
    }

    public async Task<List<ExternalLogin>> GetLinkedAccountsAsync(Guid userId)
    {
        return await _context.ExternalLogins
            .Where(el => el.UserId == userId)
            .OrderBy(el => el.Provider)
            .ToListAsync();
    }

    public async Task<List<IdentityProvider>> GetIdentityProvidersAsync(Guid? tenantId = null)
    {
        var query = _context.IdentityProviders
            .Include(p => p.Tenant)
            .Include(p => p.DefaultRole)
            .AsQueryable();

        if (tenantId.HasValue)
        {
            query = query.Where(p => p.TenantId == tenantId.Value || p.TenantId == null);
        }

        return await query.OrderBy(p => p.Name).ToListAsync();
    }

    public async Task<List<IdentityProvider>> GetIdentityProvidersForAdminAsync(
        Guid? tenantId, IdentityProviderActor actor, CancellationToken ct = default)
    {
        await RequireProviderAdminAnywhereAsync(actor, ct);

        var query = _context.IdentityProviders
            .AsNoTracking()
            .Include(p => p.Tenant)
            .Include(p => p.DefaultRole)
            .AsQueryable();

        if (actor.IsGlobalAdmin)
        {
            if (tenantId.HasValue)
                query = query.Where(p => p.TenantId == tenantId.Value || p.TenantId == null);
        }
        else if (tenantId.HasValue)
        {
            // The tenant comes from the caller's UserRoles, never from the query string alone.
            await RequireTenantAuthorityAsync(tenantId, actor, ct);
            query = query.Where(p => p.TenantId == tenantId.Value || p.TenantId == null);
        }
        else
        {
            var administered = await GetAdministeredTenantIdsAsync(actor, ct);
            query = query.Where(p => p.TenantId == null || administered.Contains(p.TenantId.Value));
        }

        return await query.OrderBy(p => p.Name).ToListAsync(ct);
    }

    public async Task<IdentityProvider?> GetIdentityProviderForAdminAsync(
        Guid id, IdentityProviderActor actor, CancellationToken ct = default)
    {
        // Authorize before looking the provider up, so a caller with no admin authority gets 403
        // whether or not the id exists.
        await RequireProviderAdminAnywhereAsync(actor, ct);

        var provider = await _context.IdentityProviders
            .AsNoTracking()
            .Include(p => p.Tenant)
            .Include(p => p.DefaultRole)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        // Platform-wide providers are readable (not manageable) by any tenant admin.
        if (provider is { TenantId: not null })
            await RequireTenantAuthorityAsync(provider.TenantId, actor, ct);

        return provider;
    }

    public async Task<IdentityProvider> CreateIdentityProviderAsync(
        IdentityProvider provider, IdentityProviderActor actor, CancellationToken ct = default)
    {
        await RequireProviderAdminAnywhereAsync(actor, ct);
        await RequireTenantAuthorityAsync(provider.TenantId, actor, ct);
        await ValidateTenantAndDefaultRoleAsync(provider.TenantId, provider.DefaultRoleId, ct);

        provider.CreatedAt = DateTime.UtcNow;
        provider.UpdatedAt = DateTime.UtcNow;

        // Encrypt client secret at rest before persisting.
        if (!string.IsNullOrEmpty(provider.ClientSecret))
            provider.ClientSecret = await EncryptClientSecretAsync(provider.ClientSecret);

        _context.IdentityProviders.Add(provider);
        await _context.SaveChangesAsync(ct);

        return provider;
    }

    public async Task<IdentityProvider> UpdateIdentityProviderAsync(
        Guid id, IdentityProvider provider, IdentityProviderActor actor, CancellationToken ct = default)
    {
        // Authorize before looking the provider up, so a caller with no provider-admin authority
        // gets 403 whether or not the id exists.
        await RequireProviderAdminAnywhereAsync(actor, ct);

        var existing = await _context.IdentityProviders.FindAsync(new object[] { id }, ct)
            ?? throw new InvalidOperationException("Identity provider not found");

        // Authority is judged on the STORED tenant, never on the tenant the request claims.
        await RequireTenantAuthorityAsync(existing.TenantId, actor, ct);

        // Moving a provider to another tenant (or making it platform-wide) is a global-admin act.
        if (existing.TenantId != provider.TenantId && !actor.IsGlobalAdmin)
        {
            throw new IdentityProviderAccessDeniedException(
                "Only SuperAdmin or SystemAdmin can move an identity provider to another tenant");
        }

        await ValidateTenantAndDefaultRoleAsync(provider.TenantId, provider.DefaultRoleId, ct);

        existing.Name = provider.Name;
        existing.DisplayName = provider.DisplayName;
        existing.Type = provider.Type;
        existing.TenantId = provider.TenantId;
        existing.ClientId = provider.ClientId;

        // Only re-encrypt if the caller sent a new plaintext secret (not an already-stored enc: value).
        if (!string.IsNullOrEmpty(provider.ClientSecret) &&
            !provider.ClientSecret.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
        {
            existing.ClientSecret = await EncryptClientSecretAsync(provider.ClientSecret);
        }
        else if (!string.IsNullOrEmpty(provider.ClientSecret))
        {
            existing.ClientSecret = provider.ClientSecret;
        }

        existing.MetadataUrl = provider.MetadataUrl;
        existing.AttributeMapping = provider.AttributeMapping;
        existing.IsActive = provider.IsActive;
        existing.AutoCreateUsers = provider.AutoCreateUsers;
        existing.DefaultRoleId = provider.DefaultRoleId;
        existing.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);

        return existing;
    }

    public async Task<bool> DeleteIdentityProviderAsync(Guid id, IdentityProviderActor actor, CancellationToken ct = default)
    {
        await RequireProviderAdminAnywhereAsync(actor, ct);

        var provider = await _context.IdentityProviders.FindAsync(new object[] { id }, ct);
        if (provider == null)
            return false;

        await RequireTenantAuthorityAsync(provider.TenantId, actor, ct);

        _context.IdentityProviders.Remove(provider);
        await _context.SaveChangesAsync(ct);

        return true;
    }

    // --- Identity-provider management authorization (task 4697) ---

    /// <summary>
    /// Coarse gate that runs first on every write: the actor must be a global admin or hold an
    /// active TenantAdmin role in at least one tenant (only the token's own tenant, when the token
    /// carries a tenant_id claim). Keeps the answer for plain users at 403 regardless of the payload or id.
    /// </summary>
    private async Task RequireProviderAdminAnywhereAsync(IdentityProviderActor actor, CancellationToken ct)
    {
        if (actor.IsGlobalAdmin)
            return;

        if (!await TenantAdminAuthority.IsAdminOfAnyTenantAsync(_context, actor.UserId, actor.TenantId, ct))
        {
            throw new IdentityProviderAccessDeniedException(
                "Only SuperAdmin, SystemAdmin or a tenant admin can manage identity providers");
        }
    }

    /// <summary>
    /// The tenants the actor administers: active TenantAdmin rows scoped to a tenant, narrowed to the
    /// token's tenant_id claim when it carries one.
    /// </summary>
    private async Task<List<Guid>> GetAdministeredTenantIdsAsync(IdentityProviderActor actor, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var query = _context.UserRoles.AsNoTracking().Where(ur =>
            ur.UserId == actor.UserId
            && ur.TenantId != null
            && ur.Role.Name == TenantAdminAuthority.RoleName
            && (ur.ExpiresAt == null || ur.ExpiresAt > now));

        if (actor.TenantId.HasValue)
            query = query.Where(ur => ur.TenantId == actor.TenantId.Value);

        return await query.Select(ur => ur.TenantId!.Value).Distinct().ToListAsync(ct);
    }

    /// <summary>
    /// Requires authority over providers of <paramref name="tenantId"/>: a global admin, or an
    /// active TenantAdmin role scoped to exactly that tenant (a role row without a tenant does not
    /// count). Platform-wide providers (null tenant) are global-admin only.
    /// </summary>
    private async Task RequireTenantAuthorityAsync(Guid? tenantId, IdentityProviderActor actor, CancellationToken ct)
    {
        if (actor.IsGlobalAdmin)
            return;

        if (tenantId == null)
        {
            throw new IdentityProviderAccessDeniedException(
                "Only SuperAdmin or SystemAdmin can manage platform-wide identity providers");
        }

        if (actor.TenantId.HasValue && actor.TenantId.Value != tenantId.Value)
        {
            throw new IdentityProviderAccessDeniedException("Token is scoped to a different tenant");
        }

        if (!await TenantAdminAuthority.IsAdminOfTenantAsync(_context, actor.UserId, tenantId.Value, ct))
        {
            throw new IdentityProviderAccessDeniedException(
                "Only SuperAdmin, SystemAdmin or an admin of the provider's tenant can manage it");
        }
    }

    /// <summary>
    /// The tenant must exist, and the default role handed to auto-created users must exist, be
    /// non-privileged, and be usable in the provider's tenant (global, or owned by that tenant).
    /// </summary>
    private async Task ValidateTenantAndDefaultRoleAsync(Guid? tenantId, Guid? defaultRoleId, CancellationToken ct)
    {
        if (tenantId.HasValue && !await _context.Tenants.AnyAsync(t => t.Id == tenantId.Value, ct))
            throw new IdentityProviderValidationException("Tenant not found");

        if (!defaultRoleId.HasValue)
            return;

        var role = await _context.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == defaultRoleId.Value, ct);

        switch (DefaultRoleRules.Check(role, tenantId))
        {
            case DefaultRoleProblem.NotFound:
                throw new IdentityProviderValidationException("Default role not found");
            case DefaultRoleProblem.Privileged:
                throw new IdentityProviderValidationException(
                    $"Default role '{role!.Name}' is a privileged role and cannot be assigned to auto-created users");
            case DefaultRoleProblem.OtherTenant:
                throw new IdentityProviderValidationException(
                    "Default role belongs to a different tenant than the identity provider");
        }
    }

    // --- Private helper methods ---

    private static (string AuthorizationEndpoint, string Scopes) GetProviderEndpoints(IdentityProviderType type)
    {
        return type switch
        {
            IdentityProviderType.Google => (
                "https://accounts.google.com/o/oauth2/v2/auth",
                "openid email profile"),
            IdentityProviderType.Microsoft => (
                "https://login.microsoftonline.com/common/oauth2/v2.0/authorize",
                "openid email profile"),
            IdentityProviderType.GitHub => (
                "https://github.com/login/oauth/authorize",
                "read:user user:email"),
            IdentityProviderType.Apple => (
                "https://appleid.apple.com/auth/authorize",
                "name email"),
            IdentityProviderType.OIDC => throw new InvalidOperationException("OIDC providers require a metadata URL for endpoint discovery"),
            IdentityProviderType.SAML => throw new InvalidOperationException("SAML providers do not use OAuth2 authorization endpoints"),
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
    }

    private static string GetTokenEndpoint(IdentityProviderType type)
    {
        return type switch
        {
            IdentityProviderType.Google => "https://oauth2.googleapis.com/token",
            IdentityProviderType.Microsoft => "https://login.microsoftonline.com/common/oauth2/v2.0/token",
            IdentityProviderType.GitHub => "https://github.com/login/oauth/access_token",
            IdentityProviderType.Apple => "https://appleid.apple.com/auth/token",
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
    }

    private static string GetUserInfoEndpoint(IdentityProviderType type)
    {
        return type switch
        {
            IdentityProviderType.Google => "https://www.googleapis.com/oauth2/v3/userinfo",
            IdentityProviderType.Microsoft => "https://graph.microsoft.com/v1.0/me",
            IdentityProviderType.GitHub => "https://api.github.com/user",
            IdentityProviderType.Apple => "", // Apple returns user info in the ID token
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
    }

    private async Task<OAuthTokenResponse?> ExchangeCodeForTokensAsync(IdentityProvider provider, string code)
    {
        var client = _httpClientFactory.CreateClient("SocialAuth");

        if (provider.Type == IdentityProviderType.SAML)
        {
            // SAML doesn't use OAuth token exchange
            return null;
        }

        var tokenEndpoint = GetTokenEndpoint(provider.Type);

        var plainClientSecret = await DecryptClientSecretAsync(provider.ClientSecret ?? "");
        var requestBody = new Dictionary<string, string>
        {
            ["client_id"] = provider.ClientId,
            ["client_secret"] = plainClientSecret,
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = _configuration["SocialAuth:RedirectUri"] ?? ""
        };

        var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(requestBody)
        };

        // GitHub requires Accept: application/json header
        if (provider.Type == IdentityProviderType.GitHub)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return null;

        var responseJson = await response.Content.ReadAsStringAsync();
        var tokenData = JsonSerializer.Deserialize<JsonElement>(responseJson);

        return new OAuthTokenResponse
        {
            AccessToken = tokenData.TryGetProperty("access_token", out var at) ? at.GetString() ?? "" : "",
            IdToken = tokenData.TryGetProperty("id_token", out var it) ? it.GetString() : null,
            RefreshToken = tokenData.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
            TokenType = tokenData.TryGetProperty("token_type", out var tt) ? tt.GetString() ?? "Bearer" : "Bearer"
        };
    }

    private async Task<ExternalUserProfile?> GetExternalUserProfileAsync(IdentityProvider provider, OAuthTokenResponse tokenResponse)
    {
        // For Apple, validate and parse the ID token (signature-verified via JWKS).
        if (provider.Type == IdentityProviderType.Apple && !string.IsNullOrEmpty(tokenResponse.IdToken))
        {
            return await ParseAppleIdTokenAsync(tokenResponse.IdToken);
        }

        // For SAML, parse the assertion
        if (provider.Type == IdentityProviderType.SAML)
        {
            return null; // SAML assertions would be parsed from the callback, not here
        }

        var client = _httpClientFactory.CreateClient("SocialAuth");
        var userInfoEndpoint = GetUserInfoEndpoint(provider.Type);

        if (string.IsNullOrEmpty(userInfoEndpoint))
            return null;

        var request = new HttpRequestMessage(HttpMethod.Get, userInfoEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenResponse.AccessToken);

        // GitHub requires User-Agent header
        if (provider.Type == IdentityProviderType.GitHub)
        {
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("IAMSystem", "1.0"));
        }

        var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return null;

        var responseJson = await response.Content.ReadAsStringAsync();
        var userData = JsonSerializer.Deserialize<JsonElement>(responseJson);

        return provider.Type switch
        {
            IdentityProviderType.Google => ParseGoogleProfile(userData, provider),
            IdentityProviderType.Microsoft => ParseMicrosoftProfile(userData, provider),
            IdentityProviderType.GitHub => await ParseGitHubProfile(userData, tokenResponse.AccessToken, provider),
            _ => null
        };
    }

    private ExternalUserProfile ParseGoogleProfile(JsonElement data, IdentityProvider provider)
    {
        var mapping = GetAttributeMapping(provider);
        var email = GetMappedValue(data, mapping, "email", "email");

        return new ExternalUserProfile
        {
            ProviderUserId = GetMappedValue(data, mapping, "sub", "sub") ?? "",
            Email = email,
            // Read from the provider response, never defaulted: missing or false means unverified.
            EmailVerified = !string.IsNullOrEmpty(email) && !HasCustomEmailMapping(mapping) && ReadBool(data, "email_verified"),
            DisplayName = GetMappedValue(data, mapping, "displayName", "name"),
            FirstName = GetMappedValue(data, mapping, "firstName", "given_name"),
            LastName = GetMappedValue(data, mapping, "lastName", "family_name")
        };
    }

    /// <summary>
    /// A JSON boolean, or the string "true"/"false" some providers use. Missing or anything else is false.
    /// </summary>
    private static bool ReadBool(JsonElement data, string name)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(name, out var value))
            return false;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => string.Equals(value.GetString(), "true", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    /// <summary>
    /// The provider's verified flag describes its standard e-mail claim; if an admin mapped the e-mail
    /// attribute to some other claim, nothing vouches for that value.
    /// </summary>
    private static bool HasCustomEmailMapping(Dictionary<string, string> mapping) =>
        mapping.TryGetValue("email", out var mapped) && !string.IsNullOrWhiteSpace(mapped) &&
        !string.Equals(mapped, "email", StringComparison.Ordinal);

    private ExternalUserProfile ParseMicrosoftProfile(JsonElement data, IdentityProvider provider)
    {
        var mapping = GetAttributeMapping(provider);

        return new ExternalUserProfile
        {
            ProviderUserId = GetMappedValue(data, mapping, "sub", "id") ?? "",
            Email = GetMappedValue(data, mapping, "email", "mail")
                    ?? GetMappedValue(data, mapping, "email", "userPrincipalName"),
            // Microsoft Graph gives no verified-e-mail signal, and mail/userPrincipalName can be set freely in an
            // attacker's own Entra tenant (IAM uses the multi-tenant "common" endpoint): never verified.
            EmailVerified = false,
            DisplayName = GetMappedValue(data, mapping, "displayName", "displayName"),
            FirstName = GetMappedValue(data, mapping, "firstName", "givenName"),
            LastName = GetMappedValue(data, mapping, "lastName", "surname")
        };
    }

    private async Task<ExternalUserProfile> ParseGitHubProfile(JsonElement data, string accessToken, IdentityProvider provider)
    {
        var mapping = GetAttributeMapping(provider);

        var displayName = GetMappedValue(data, mapping, "displayName", "name")
                          ?? GetMappedValue(data, mapping, "displayName", "login");

        // The public profile e-mail carries no verified flag; /user/emails does. The primary address and its
        // verified flag decide; without a primary entry the profile e-mail is used but counts as unverified.
        var email = GetMappedValue(data, mapping, "email", "email");
        var emailVerified = false;
        var primary = await FetchGitHubPrimaryEmailAsync(accessToken);
        if (!string.IsNullOrEmpty(primary.Email))
        {
            email = primary.Email;
            emailVerified = primary.Verified;
        }

        if (HasCustomEmailMapping(mapping))
            emailVerified = false;

        // GitHub doesn't have separate first/last name fields
        var nameParts = (displayName ?? "").Split(' ', 2);

        return new ExternalUserProfile
        {
            ProviderUserId = data.TryGetProperty("id", out var idProp) ? idProp.ToString() : "",
            Email = email,
            EmailVerified = emailVerified && !string.IsNullOrEmpty(email),
            DisplayName = displayName,
            FirstName = nameParts.Length > 0 ? nameParts[0] : "",
            LastName = nameParts.Length > 1 ? nameParts[1] : ""
        };
    }

    private async Task<(string? Email, bool Verified)> FetchGitHubPrimaryEmailAsync(string accessToken)
    {
        var client = _httpClientFactory.CreateClient("SocialAuth");
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user/emails");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("IAMSystem", "1.0"));

        var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return (null, false);

        var responseJson = await response.Content.ReadAsStringAsync();
        var emails = JsonSerializer.Deserialize<JsonElement>(responseJson);

        if (emails.ValueKind != JsonValueKind.Array)
            return (null, false);

        foreach (var emailEntry in emails.EnumerateArray())
        {
            if (emailEntry.ValueKind == JsonValueKind.Object && ReadBool(emailEntry, "primary"))
            {
                var address = emailEntry.TryGetProperty("email", out var emailProp) && emailProp.ValueKind == JsonValueKind.String
                    ? emailProp.GetString()
                    : null;
                return (address, ReadBool(emailEntry, "verified"));
            }
        }

        return (null, false);
    }

    // Apple JWKS key cache (static so it survives across DI-scoped instances).
    private static IList<SecurityKey>? _appleSigningKeys;
    private static DateTime _appleKeysFetchedAt = DateTime.MinValue;
    private static readonly SemaphoreSlim _appleKeyLock = new(1, 1);

    private async Task<IList<SecurityKey>> GetAppleSigningKeysAsync()
    {
        // Return cached keys if still fresh (< 1 hour old).
        if (_appleSigningKeys != null && _appleKeysFetchedAt > DateTime.UtcNow.AddHours(-1))
            return _appleSigningKeys;

        await _appleKeyLock.WaitAsync();
        try
        {
            // Double-checked locking: another thread may have refreshed while we waited.
            if (_appleSigningKeys != null && _appleKeysFetchedAt > DateTime.UtcNow.AddHours(-1))
                return _appleSigningKeys;

            using var http = _httpClientFactory.CreateClient();
            var jwks = await http.GetStringAsync("https://appleid.apple.com/auth/keys");
            var keySet = new JsonWebKeySet(jwks);
            _appleSigningKeys = keySet.GetSigningKeys();
            _appleKeysFetchedAt = DateTime.UtcNow;
            return _appleSigningKeys;
        }
        finally
        {
            _appleKeyLock.Release();
        }
    }

    private async Task<ExternalUserProfile?> ParseAppleIdTokenAsync(string idToken)
    {
        try
        {
            var signingKeys = await GetAppleSigningKeysAsync();
            var tokenHandler = new JwtSecurityTokenHandler();
            var validationParams = new TokenValidationParameters
            {
                ValidIssuer = "https://appleid.apple.com",
                ValidAudience = _configuration["Apple:ClientId"],
                IssuerSigningKeys = signingKeys,
                ValidateLifetime = true,
                ValidateIssuer = true,
                ValidateAudience = _configuration["Apple:ClientId"] != null,
            };

            var principal = tokenHandler.ValidateToken(idToken, validationParams, out _);

            // JwtSecurityTokenHandler maps sub/email to the long ClaimTypes names unless the process-wide inbound
            // map was cleared, so accept both spellings.
            string? ClaimValue(params string[] types) =>
                principal.Claims.FirstOrDefault(c => types.Contains(c.Type))?.Value;

            var email = ClaimValue("email", System.Security.Claims.ClaimTypes.Email);

            return new ExternalUserProfile
            {
                ProviderUserId = ClaimValue("sub", System.Security.Claims.ClaimTypes.NameIdentifier) ?? "",
                Email = email,
                // Apple puts email_verified in the (signature-verified) id_token, as a bool or the string "true".
                EmailVerified = !string.IsNullOrEmpty(email) &&
                    string.Equals(ClaimValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase),
                DisplayName = null,
                FirstName = null,
                LastName = null
            };
        }
        catch (SecurityTokenException ex)
        {
            // Log at Warning — invalid tokens are expected (attacks, clock skew, revoked keys).
            // Do not swallow as a silent null; let the caller decide.
            throw new UnauthorizedAccessException("Apple ID token validation failed.", ex);
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, string> GetAttributeMapping(IdentityProvider provider)
    {
        if (string.IsNullOrEmpty(provider.AttributeMapping))
            return new Dictionary<string, string>();

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(provider.AttributeMapping)
                   ?? new Dictionary<string, string>();
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    private static string? GetMappedValue(JsonElement data, Dictionary<string, string> mapping, string fieldName, string defaultProperty)
    {
        var property = mapping.TryGetValue(fieldName, out var mapped) ? mapped : defaultProperty;

        if (data.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    private string GenerateAccessToken(User user, int expirationMinutes, Guid? refreshTokenId = null)
    {
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Name, $"{user.FirstName} {user.LastName}")
        };

        if (refreshTokenId.HasValue)
        {
            claims.Add(new Claim("refresh_token_id", refreshTokenId.Value.ToString()));
        }

        foreach (var userRole in user.UserRoles)
        {
            claims.Add(new Claim(ClaimTypes.Role, userRole.Role.Name));
        }

        var secretKey = _configuration["Jwt:SecretKey"] ?? throw new InvalidOperationException("JWT secret key not configured");
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _configuration["Jwt:Issuer"],
            audience: _configuration["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(expirationMinutes),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string GenerateRefreshToken()
    {
        var randomBytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomBytes);
        return Convert.ToBase64String(randomBytes);
    }

    private static string HashToken(string token)
    {
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(token));
        return Convert.ToBase64String(hashBytes);
    }

    private static string StateEntryKey(string state) => $"SocialAuthState:{state}";

    private void ValidateRedirectUri(IdentityProvider provider, string redirectUri)
    {
        if (string.IsNullOrEmpty(redirectUri))
            throw new InvalidOperationException("redirectUri is required");

        // Build allowlist: per-provider JSON array takes precedence over global config fallback.
        List<string> allowed = [];

        if (!string.IsNullOrEmpty(provider.AllowedRedirectUris))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<List<string>>(provider.AllowedRedirectUris);
                if (parsed != null)
                    allowed = parsed;
            }
            catch
            {
                // Malformed JSON — treat as empty; global fallback applies.
            }
        }

        if (allowed.Count == 0)
        {
            var globalFallback = _configuration["SocialAuth:RedirectUri"];
            if (!string.IsNullOrEmpty(globalFallback))
                allowed = [globalFallback];
        }

        if (allowed.Count == 0)
            throw new InvalidOperationException("No allowed redirect URIs are configured for this provider");

        if (!allowed.Contains(redirectUri, StringComparer.Ordinal))
            throw new InvalidOperationException("redirectUri is not in the provider's allowed list");
    }
}

// Internal helper classes

internal record SocialStateEntry(string RedirectUri, Guid ProviderId);

internal class OAuthTokenResponse
{
    public string AccessToken { get; set; } = "";
    public string? IdToken { get; set; }
    public string? RefreshToken { get; set; }
    public string TokenType { get; set; } = "Bearer";
}

internal class ExternalUserProfile
{
    public string ProviderUserId { get; set; } = "";
    public string? Email { get; set; }

    /// <summary>True only when the provider itself says the e-mail address is verified (task 4707).</summary>
    public bool EmailVerified { get; set; }
    public string? DisplayName { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
}
