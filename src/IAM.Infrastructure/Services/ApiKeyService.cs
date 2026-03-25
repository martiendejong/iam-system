using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace IAM.Infrastructure.Services;

public class ApiKeyService : IApiKeyService
{
    private readonly IAMDbContext _context;
    private const string KeyPrefixBase = "iam_";

    public ApiKeyService(IAMDbContext context)
    {
        _context = context;
    }

    public async Task<(ApiKey Key, string RawKey)> CreateApiKeyAsync(
        string name,
        Guid? userId,
        Guid? tenantId,
        List<string>? permissions = null,
        DateTime? expiresAt = null,
        int? rateLimitPerMinute = null,
        string? description = null,
        CancellationToken ct = default)
    {
        // Generate a cryptographically secure random key
        var randomBytes = new byte[32];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(randomBytes);
        }

        // Create a 4-char random suffix for the prefix (alphanumeric lowercase)
        var prefixSuffix = GenerateRandomAlphanumeric(4);
        var prefix = $"{KeyPrefixBase}{prefixSuffix}_";

        // The raw key is prefix + base64url-encoded random bytes
        var rawKey = prefix + Base64UrlEncode(randomBytes);

        // Only store the SHA256 hash - never the raw key
        var keyHash = ComputeSha256Hash(rawKey);

        var apiKey = new ApiKey
        {
            Id = Guid.NewGuid(),
            Name = name,
            KeyHash = keyHash,
            KeyPrefix = prefix,
            UserId = userId,
            TenantId = tenantId,
            Permissions = permissions != null ? JsonSerializer.Serialize(permissions) : "[]",
            ExpiresAt = expiresAt,
            RateLimitPerMinute = rateLimitPerMinute,
            Description = description,
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };

        _context.ApiKeys.Add(apiKey);
        await _context.SaveChangesAsync(ct);

        // Return the entity AND the raw key (raw key is shown to user ONCE, then discarded)
        return (apiKey, rawKey);
    }

    public async Task<ApiKey?> ValidateApiKeyAsync(string rawKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawKey))
            return null;

        var keyHash = ComputeSha256Hash(rawKey);

        var apiKey = await _context.ApiKeys
            .Include(k => k.User)
            .Include(k => k.Tenant)
            .FirstOrDefaultAsync(k => k.KeyHash == keyHash, ct);

        if (apiKey == null)
            return null;

        // Check if active
        if (!apiKey.IsActive)
            return null;

        // Check if expired
        if (apiKey.ExpiresAt.HasValue && apiKey.ExpiresAt.Value <= DateTime.UtcNow)
            return null;

        // Update last used timestamp (fire and forget - don't block validation)
        apiKey.LastUsedAt = DateTime.UtcNow;
        try
        {
            await _context.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Non-critical: LastUsedAt update is best-effort
        }

        return apiKey;
    }

    public async Task<List<ApiKey>> GetApiKeysAsync(
        Guid? userId = null,
        Guid? tenantId = null,
        CancellationToken ct = default)
    {
        var query = _context.ApiKeys.AsQueryable();

        if (userId.HasValue)
            query = query.Where(k => k.UserId == userId.Value);

        if (tenantId.HasValue)
            query = query.Where(k => k.TenantId == tenantId.Value);

        return await query
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<bool> RevokeApiKeyAsync(Guid keyId, CancellationToken ct = default)
    {
        var apiKey = await _context.ApiKeys.FindAsync(new object[] { keyId }, ct);

        if (apiKey == null)
            return false;

        apiKey.IsActive = false;
        await _context.SaveChangesAsync(ct);

        return true;
    }

    public async Task<(bool Success, string? NewRawKey)> RotateApiKeyAsync(
        Guid keyId,
        CancellationToken ct = default)
    {
        var apiKey = await _context.ApiKeys.FindAsync(new object[] { keyId }, ct);

        if (apiKey == null)
            return (false, null);

        // Generate new key material
        var randomBytes = new byte[32];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(randomBytes);
        }

        // Reuse the existing prefix for continuity
        var newRawKey = apiKey.KeyPrefix + Base64UrlEncode(randomBytes);
        var newKeyHash = ComputeSha256Hash(newRawKey);

        // Update the hash
        apiKey.KeyHash = newKeyHash;
        apiKey.IsActive = true; // Re-activate if it was deactivated

        await _context.SaveChangesAsync(ct);

        return (true, newRawKey);
    }

    /// <summary>
    /// Compute SHA256 hash of a string, returning lowercase hex.
    /// </summary>
    private static string ComputeSha256Hash(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Generate a random alphanumeric lowercase string of the given length.
    /// </summary>
    private static string GenerateRandomAlphanumeric(int length)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        var result = new char[length];
        using var rng = RandomNumberGenerator.Create();
        var buffer = new byte[length];
        rng.GetBytes(buffer);
        for (int i = 0; i < length; i++)
        {
            result[i] = chars[buffer[i] % chars.Length];
        }
        return new string(result);
    }

    /// <summary>
    /// Base64url encode (RFC 4648) without padding.
    /// </summary>
    private static string Base64UrlEncode(byte[] input)
    {
        return Convert.ToBase64String(input)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
