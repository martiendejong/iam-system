using System.Text.Json;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace IAM.Infrastructure.Services;

public class ClaimsMappingService : IClaimsMappingService
{
    private readonly IAMDbContext _context;

    public ClaimsMappingService(IAMDbContext context)
    {
        _context = context;
    }

    // ---- Claims Mapping Rules ----

    public async Task<List<ClaimsMappingRule>> GetRulesAsync(string clientId, Guid? tenantId = null, CancellationToken ct = default)
    {
        var query = _context.ClaimsMappingRules
            .Where(r => r.ClientId == clientId);

        if (tenantId.HasValue)
            query = query.Where(r => r.TenantId == null || r.TenantId == tenantId.Value);

        return await query.OrderBy(r => r.Priority).ThenBy(r => r.CreatedAt).ToListAsync(ct);
    }

    public async Task<ClaimsMappingRule?> GetRuleAsync(Guid ruleId, CancellationToken ct = default)
    {
        return await _context.ClaimsMappingRules.FirstOrDefaultAsync(r => r.Id == ruleId, ct);
    }

    public async Task<ClaimsMappingRule> CreateRuleAsync(ClaimsMappingRule rule, CancellationToken ct = default)
    {
        rule.Id = Guid.NewGuid();
        rule.CreatedAt = DateTime.UtcNow;

        _context.ClaimsMappingRules.Add(rule);
        await _context.SaveChangesAsync(ct);

        return rule;
    }

    public async Task<ClaimsMappingRule> UpdateRuleAsync(Guid ruleId, ClaimsMappingRule rule, CancellationToken ct = default)
    {
        var existing = await _context.ClaimsMappingRules.FirstOrDefaultAsync(r => r.Id == ruleId, ct)
            ?? throw new InvalidOperationException("Claims mapping rule not found");

        existing.SourceType = rule.SourceType;
        existing.SourcePath = rule.SourcePath;
        existing.TargetClaim = rule.TargetClaim;
        existing.Transform = rule.Transform;
        existing.TransformPattern = rule.TransformPattern;
        existing.Priority = rule.Priority;
        existing.IsActive = rule.IsActive;

        await _context.SaveChangesAsync(ct);
        return existing;
    }

    public async Task<bool> DeleteRuleAsync(Guid ruleId, CancellationToken ct = default)
    {
        var rule = await _context.ClaimsMappingRules.FirstOrDefaultAsync(r => r.Id == ruleId, ct);
        if (rule == null) return false;

        _context.ClaimsMappingRules.Remove(rule);
        await _context.SaveChangesAsync(ct);
        return true;
    }

    // ---- Token Configuration ----

    public async Task<TokenConfiguration?> GetTokenConfigurationAsync(string clientId, Guid? tenantId = null, CancellationToken ct = default)
    {
        // First try tenant-specific config, then fall back to client-level config
        if (tenantId.HasValue)
        {
            var tenantConfig = await _context.TokenConfigurations
                .FirstOrDefaultAsync(c => c.ClientId == clientId && c.TenantId == tenantId.Value, ct);
            if (tenantConfig != null) return tenantConfig;
        }

        return await _context.TokenConfigurations
            .FirstOrDefaultAsync(c => c.ClientId == clientId && c.TenantId == null, ct);
    }

    public async Task<TokenConfiguration> UpsertTokenConfigurationAsync(TokenConfiguration config, CancellationToken ct = default)
    {
        var existing = await _context.TokenConfigurations
            .FirstOrDefaultAsync(c => c.ClientId == config.ClientId && c.TenantId == config.TenantId, ct);

        if (existing != null)
        {
            existing.AccessTokenLifetimeMinutes = config.AccessTokenLifetimeMinutes;
            existing.RefreshTokenLifetimeDays = config.RefreshTokenLifetimeDays;
            existing.IncludeRoles = config.IncludeRoles;
            existing.IncludePermissions = config.IncludePermissions;
            existing.IncludeGroups = config.IncludeGroups;
            existing.CustomNamespace = config.CustomNamespace;
            existing.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync(ct);
            return existing;
        }

        config.Id = Guid.NewGuid();
        config.CreatedAt = DateTime.UtcNow;
        config.UpdatedAt = DateTime.UtcNow;

        _context.TokenConfigurations.Add(config);
        await _context.SaveChangesAsync(ct);
        return config;
    }

    public async Task<OrganizationTokenLifetime?> ResolveTokenLifetimeForUserAsync(Guid userId, CancellationToken ct = default)
    {
        // A user's organization is the tenant scope of their role assignment(s) - login
        // itself has no OAuth2 client context, so the lookup ignores ClientId and matches
        // on TenantId alone (most-recently-updated row wins if more than one client has
        // configured lifetimes for the same tenant).
        var tenantId = await _context.UserRoles
            .Where(ur => ur.UserId == userId && ur.TenantId != null)
            .Select(ur => ur.TenantId)
            .FirstOrDefaultAsync(ct);

        if (tenantId == null)
            return null;

        var config = await _context.TokenConfigurations
            .Where(c => c.TenantId == tenantId)
            .OrderByDescending(c => c.UpdatedAt)
            .FirstOrDefaultAsync(ct);

        if (config == null)
            return null;

        return new OrganizationTokenLifetime
        {
            AccessTokenLifetimeMinutes = config.AccessTokenLifetimeMinutes,
            RefreshTokenLifetimeDays = config.RefreshTokenLifetimeDays
        };
    }

    // ---- Token Preview / Claim Generation ----

    public async Task<TokenPreviewResult> PreviewTokenAsync(string clientId, Guid userId, Guid? tenantId = null, CancellationToken ct = default)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new InvalidOperationException("User not found");

        var tokenConfig = await GetTokenConfigurationAsync(clientId, tenantId, ct);

        var rules = await _context.ClaimsMappingRules
            .Where(r => r.ClientId == clientId && r.IsActive)
            .Where(r => r.TenantId == null || r.TenantId == tenantId)
            .OrderBy(r => r.Priority)
            .ToListAsync(ct);

        var claims = new List<TokenPreviewClaim>();

        // Add standard claims
        claims.Add(new TokenPreviewClaim { Type = "sub", Value = user.Id.ToString(), Source = "Standard" });
        claims.Add(new TokenPreviewClaim { Type = "email", Value = user.Email, Source = "Standard" });
        claims.Add(new TokenPreviewClaim { Type = "given_name", Value = user.FirstName, Source = "Standard" });
        claims.Add(new TokenPreviewClaim { Type = "family_name", Value = user.LastName, Source = "Standard" });

        // Add role claims if configured
        if (tokenConfig?.IncludeRoles != false)
        {
            var roles = await _context.UserRoles
                .Where(ur => ur.UserId == userId && (tenantId == null || ur.TenantId == tenantId))
                .Include(ur => ur.Role)
                .Select(ur => ur.Role)
                .ToListAsync(ct);

            foreach (var role in roles)
            {
                claims.Add(new TokenPreviewClaim { Type = "role", Value = role.Name, Source = "UserRole" });

                // Add permissions if configured
                if (tokenConfig?.IncludePermissions == true && !string.IsNullOrEmpty(role.Permissions))
                {
                    try
                    {
                        var permissions = JsonSerializer.Deserialize<List<string>>(role.Permissions);
                        if (permissions != null)
                        {
                            foreach (var perm in permissions)
                            {
                                claims.Add(new TokenPreviewClaim { Type = "permission", Value = perm, Source = $"Role:{role.Name}" });
                            }
                        }
                    }
                    catch { /* Skip malformed permissions JSON */ }
                }
            }
        }

        // Add group claims if configured
        if (tokenConfig?.IncludeGroups == true)
        {
            var groups = await _context.GroupMemberships
                .Where(gm => gm.UserId == userId && gm.IsActive)
                .Include(gm => gm.Group)
                .Select(gm => gm.Group)
                .ToListAsync(ct);

            foreach (var group in groups)
            {
                if (group != null)
                    claims.Add(new TokenPreviewClaim { Type = "group", Value = group.Name, Source = "GroupMembership" });
            }
        }

        // Apply custom mapping rules
        foreach (var rule in rules)
        {
            var claimValue = await ResolveClaimValueAsync(rule, user, tenantId, ct);
            if (claimValue != null)
            {
                var transformedValue = ApplyTransform(claimValue, rule.Transform, rule.TransformPattern);
                var ns = tokenConfig?.CustomNamespace ?? "";
                var targetClaim = rule.TargetClaim.StartsWith("http") || string.IsNullOrEmpty(ns)
                    ? rule.TargetClaim
                    : $"{ns}{rule.TargetClaim}";

                claims.Add(new TokenPreviewClaim
                {
                    Type = targetClaim,
                    Value = transformedValue,
                    Source = $"Rule:{rule.SourceType}:{rule.SourcePath}"
                });
            }
        }

        return new TokenPreviewResult
        {
            UserId = userId,
            ClientId = clientId,
            TenantId = tenantId,
            Claims = claims,
            Configuration = new TokenConfigurationPreview
            {
                AccessTokenLifetimeMinutes = tokenConfig?.AccessTokenLifetimeMinutes ?? 15,
                RefreshTokenLifetimeDays = tokenConfig?.RefreshTokenLifetimeDays ?? 7,
                IncludeRoles = tokenConfig?.IncludeRoles ?? true,
                IncludePermissions = tokenConfig?.IncludePermissions ?? false,
                IncludeGroups = tokenConfig?.IncludeGroups ?? false,
                CustomNamespace = tokenConfig?.CustomNamespace
            }
        };
    }

    // ---- Private Helpers ----

    private async Task<string?> ResolveClaimValueAsync(ClaimsMappingRule rule, User user, Guid? tenantId, CancellationToken ct)
    {
        switch (rule.SourceType)
        {
            case ClaimSourceType.UserAttribute:
                return ResolveUserAttribute(user, rule.SourcePath);

            case ClaimSourceType.GroupMembership:
                var isMember = await _context.GroupMemberships
                    .AnyAsync(gm => gm.UserId == user.Id && gm.IsActive && gm.Group != null && gm.Group.Name == rule.SourcePath, ct);
                return isMember ? "true" : null; // Only emit claim if user is a member

            case ClaimSourceType.RolePermission:
                var hasRole = await _context.UserRoles
                    .AnyAsync(ur => ur.UserId == user.Id && ur.Role.Name == rule.SourcePath
                        && (tenantId == null || ur.TenantId == tenantId), ct);
                return hasRole ? "true" : null; // Only emit claim if user has the role

            case ClaimSourceType.Static:
                return rule.SourcePath; // Static value, always emit

            case ClaimSourceType.External:
                // External claims would come from an external source (e.g. LDAP attribute, IdP)
                // For now return null; real implementation would query external systems
                return null;

            default:
                return null;
        }
    }

    private static string? ResolveUserAttribute(User user, string attributePath)
    {
        return attributePath.ToLowerInvariant() switch
        {
            "email" => user.Email,
            "firstname" or "first_name" or "givenname" => user.FirstName,
            "lastname" or "last_name" or "familyname" => user.LastName,
            "phonenumber" or "phone_number" or "phone" => user.PhoneNumber,
            "id" => user.Id.ToString(),
            "isactive" or "is_active" or "active" => user.IsActive.ToString().ToLower(),
            "emailconfirmed" or "email_confirmed" => user.EmailConfirmed.ToString().ToLower(),
            "twofactorenabled" or "two_factor_enabled" or "mfa" => user.TwoFactorEnabled.ToString().ToLower(),
            "createdat" or "created_at" => user.CreatedAt.ToString("O"),
            _ => null
        };
    }

    private static string ApplyTransform(string value, ClaimTransform transform, string? pattern)
    {
        return transform switch
        {
            ClaimTransform.ToUpper => value.ToUpperInvariant(),
            ClaimTransform.ToLower => value.ToLowerInvariant(),
            ClaimTransform.Join => string.Join(pattern ?? ",", value.Split(',').Select(v => v.Trim())),
            ClaimTransform.Split => value.Split(pattern ?? ",").FirstOrDefault() ?? value,
            ClaimTransform.Format => !string.IsNullOrEmpty(pattern) ? string.Format(pattern, value) : value,
            _ => value
        };
    }
}
