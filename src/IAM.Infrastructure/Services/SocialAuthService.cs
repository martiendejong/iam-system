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
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace IAM.Infrastructure.Services;

public class SocialAuthService : ISocialAuthService
{
    // Today's default when an organization has never saved a Token Configuration.
    private const int DefaultRefreshTokenLifetimeDays = 7;

    private readonly IAMDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IClaimsMappingService _claimsMappingService;

    public SocialAuthService(
        IAMDbContext context,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        IClaimsMappingService claimsMappingService)
    {
        _context = context;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _claimsMappingService = claimsMappingService;
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
            // Existing linked account - update last used
            externalLogin.LastUsedAt = DateTime.UtcNow;
            externalLogin.Email = externalUser.Email;
            externalLogin.DisplayName = externalUser.DisplayName;
            user = externalLogin.User!;
        }
        else
        {
            // No linked account - try to find user by email or auto-create
            var existingUser = await _context.Users
                .Include(u => u.UserRoles)
                    .ThenInclude(ur => ur.Role)
                .FirstOrDefaultAsync(u => u.Email == externalUser.Email);

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
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString()),
                    FirstName = externalUser.FirstName ?? "",
                    LastName = externalUser.LastName ?? "",
                    EmailConfirmed = true, // Trust the email from the provider
                    IsActive = true
                };

                _context.Users.Add(user);

                // Assign default role if configured
                if (provider.DefaultRoleId.HasValue)
                {
                    var userRole = new UserRole
                    {
                        UserId = user.Id,
                        RoleId = provider.DefaultRoleId.Value,
                        TenantId = provider.TenantId
                    };
                    _context.UserRoles.Add(userRole);
                }
            }
            else
            {
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

    public async Task<IdentityProvider> CreateIdentityProviderAsync(IdentityProvider provider)
    {
        provider.CreatedAt = DateTime.UtcNow;
        provider.UpdatedAt = DateTime.UtcNow;

        _context.IdentityProviders.Add(provider);
        await _context.SaveChangesAsync();

        return provider;
    }

    public async Task<IdentityProvider> UpdateIdentityProviderAsync(Guid id, IdentityProvider provider)
    {
        var existing = await _context.IdentityProviders.FindAsync(id)
            ?? throw new InvalidOperationException("Identity provider not found");

        existing.Name = provider.Name;
        existing.DisplayName = provider.DisplayName;
        existing.Type = provider.Type;
        existing.TenantId = provider.TenantId;
        existing.ClientId = provider.ClientId;
        existing.ClientSecret = provider.ClientSecret;
        existing.MetadataUrl = provider.MetadataUrl;
        existing.AttributeMapping = provider.AttributeMapping;
        existing.IsActive = provider.IsActive;
        existing.AutoCreateUsers = provider.AutoCreateUsers;
        existing.DefaultRoleId = provider.DefaultRoleId;
        existing.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        return existing;
    }

    public async Task<bool> DeleteIdentityProviderAsync(Guid id)
    {
        var provider = await _context.IdentityProviders.FindAsync(id);
        if (provider == null)
            return false;

        _context.IdentityProviders.Remove(provider);
        await _context.SaveChangesAsync();

        return true;
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

        var requestBody = new Dictionary<string, string>
        {
            ["client_id"] = provider.ClientId,
            ["client_secret"] = provider.ClientSecret,
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
        // For Apple, parse the ID token since there's no userinfo endpoint
        if (provider.Type == IdentityProviderType.Apple && !string.IsNullOrEmpty(tokenResponse.IdToken))
        {
            return ParseAppleIdToken(tokenResponse.IdToken);
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

        return new ExternalUserProfile
        {
            ProviderUserId = GetMappedValue(data, mapping, "sub", "sub") ?? "",
            Email = GetMappedValue(data, mapping, "email", "email"),
            DisplayName = GetMappedValue(data, mapping, "displayName", "name"),
            FirstName = GetMappedValue(data, mapping, "firstName", "given_name"),
            LastName = GetMappedValue(data, mapping, "lastName", "family_name")
        };
    }

    private ExternalUserProfile ParseMicrosoftProfile(JsonElement data, IdentityProvider provider)
    {
        var mapping = GetAttributeMapping(provider);

        return new ExternalUserProfile
        {
            ProviderUserId = GetMappedValue(data, mapping, "sub", "id") ?? "",
            Email = GetMappedValue(data, mapping, "email", "mail")
                    ?? GetMappedValue(data, mapping, "email", "userPrincipalName"),
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

        // GitHub may not return email in the user endpoint; fetch from /user/emails
        var email = GetMappedValue(data, mapping, "email", "email");
        if (string.IsNullOrEmpty(email))
        {
            email = await FetchGitHubPrimaryEmailAsync(accessToken);
        }

        // GitHub doesn't have separate first/last name fields
        var nameParts = (displayName ?? "").Split(' ', 2);

        return new ExternalUserProfile
        {
            ProviderUserId = data.TryGetProperty("id", out var idProp) ? idProp.ToString() : "",
            Email = email,
            DisplayName = displayName,
            FirstName = nameParts.Length > 0 ? nameParts[0] : "",
            LastName = nameParts.Length > 1 ? nameParts[1] : ""
        };
    }

    private async Task<string?> FetchGitHubPrimaryEmailAsync(string accessToken)
    {
        var client = _httpClientFactory.CreateClient("SocialAuth");
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user/emails");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("IAMSystem", "1.0"));

        var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return null;

        var responseJson = await response.Content.ReadAsStringAsync();
        var emails = JsonSerializer.Deserialize<JsonElement>(responseJson);

        if (emails.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var emailEntry in emails.EnumerateArray())
        {
            if (emailEntry.TryGetProperty("primary", out var primaryProp) && primaryProp.GetBoolean())
            {
                return emailEntry.TryGetProperty("email", out var emailProp) ? emailProp.GetString() : null;
            }
        }

        return null;
    }

    private static ExternalUserProfile? ParseAppleIdToken(string idToken)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            var token = handler.ReadJwtToken(idToken);

            return new ExternalUserProfile
            {
                ProviderUserId = token.Claims.FirstOrDefault(c => c.Type == "sub")?.Value ?? "",
                Email = token.Claims.FirstOrDefault(c => c.Type == "email")?.Value,
                DisplayName = null,
                FirstName = null,
                LastName = null
            };
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
}

// Internal helper classes

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
    public string? DisplayName { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
}
