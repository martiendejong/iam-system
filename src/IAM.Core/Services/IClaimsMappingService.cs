using IAM.Core.Entities;

namespace IAM.Core.Services;

/// <summary>
/// Service for managing claims mapping rules, token configurations,
/// and generating custom token claims based on configured rules.
/// </summary>
public interface IClaimsMappingService
{
    // ---- Claims Mapping Rules ----

    /// <summary>
    /// Get all claims mapping rules for a specific client.
    /// </summary>
    Task<List<ClaimsMappingRule>> GetRulesAsync(string clientId, Guid? tenantId = null, CancellationToken ct = default);

    /// <summary>
    /// Get a single claims mapping rule by ID.
    /// </summary>
    Task<ClaimsMappingRule?> GetRuleAsync(Guid ruleId, CancellationToken ct = default);

    /// <summary>
    /// Create a new claims mapping rule.
    /// </summary>
    Task<ClaimsMappingRule> CreateRuleAsync(ClaimsMappingRule rule, CancellationToken ct = default);

    /// <summary>
    /// Update an existing claims mapping rule.
    /// </summary>
    Task<ClaimsMappingRule> UpdateRuleAsync(Guid ruleId, ClaimsMappingRule rule, CancellationToken ct = default);

    /// <summary>
    /// Delete a claims mapping rule.
    /// </summary>
    Task<bool> DeleteRuleAsync(Guid ruleId, CancellationToken ct = default);

    // ---- Token Configuration ----

    /// <summary>
    /// Get token configuration for a client (optionally scoped to a tenant).
    /// </summary>
    Task<TokenConfiguration?> GetTokenConfigurationAsync(string clientId, Guid? tenantId = null, CancellationToken ct = default);

    /// <summary>
    /// Create or update token configuration for a client.
    /// </summary>
    Task<TokenConfiguration> UpsertTokenConfigurationAsync(TokenConfiguration config, CancellationToken ct = default);

    /// <summary>
    /// Resolve the access/refresh token lifetime that should apply to a login for this
    /// user's organization, based on the Token Configuration saved for the tenant they
    /// belong to (via any of their tenant-scoped roles), regardless of which OAuth2
    /// client the configuration was originally saved against.
    /// Returns null when the user has no tenant-scoped role, or their organization has
    /// never saved a Token Configuration - callers should fall back to today's defaults.
    /// </summary>
    Task<OrganizationTokenLifetime?> ResolveTokenLifetimeForUserAsync(Guid userId, CancellationToken ct = default);

    // ---- Token Preview / Claim Generation ----

    /// <summary>
    /// Generate a preview of what claims would be included in a token
    /// for a specific user and client combination.
    /// </summary>
    Task<TokenPreviewResult> PreviewTokenAsync(string clientId, Guid userId, Guid? tenantId = null, CancellationToken ct = default);
}

/// <summary>
/// Result of a token preview operation, showing what claims would be emitted.
/// </summary>
public class TokenPreviewResult
{
    public Guid UserId { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public Guid? TenantId { get; set; }
    public List<TokenPreviewClaim> Claims { get; set; } = new();
    public TokenConfigurationPreview Configuration { get; set; } = new();
}

public class TokenPreviewClaim
{
    public string Type { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty; // Which rule/source produced this claim
}

public class TokenConfigurationPreview
{
    public int AccessTokenLifetimeMinutes { get; set; }
    public int RefreshTokenLifetimeDays { get; set; }
    public bool IncludeRoles { get; set; }
    public bool IncludePermissions { get; set; }
    public bool IncludeGroups { get; set; }
    public string? CustomNamespace { get; set; }
}

/// <summary>
/// Resolved token lifetime for a login, sourced from an organization's saved
/// Token Configuration (see <see cref="IClaimsMappingService.ResolveTokenLifetimeForUserAsync"/>).
/// </summary>
public class OrganizationTokenLifetime
{
    public int AccessTokenLifetimeMinutes { get; set; }
    public int RefreshTokenLifetimeDays { get; set; }
}
