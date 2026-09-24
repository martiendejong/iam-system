using System.Security.Claims;
using System.Text.Json;
using Hazina.Security.ApiKeys;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace IAM.Infrastructure.Services;

/// <summary>
/// IAM's key table behind Hazina.Security.ApiKeys: the source of truth for every key IAM issues.
/// Serves IAM's own middleware (through the module's cache) and the introspection endpoint other
/// apps validate against. Holds only key HASHES; raw keys live in Vault.
/// </summary>
public sealed class EfApiKeyStore : IApiKeyStore, IApiKeyUsageRecorder
{
    private readonly IAMDbContext _context;

    public EfApiKeyStore(IAMDbContext context) => _context = context;

    public async Task<ApiKeyRecord?> FindByHashAsync(string keyHash, CancellationToken ct = default)
    {
        var entity = await _context.ApiKeys
            .AsNoTracking()
            .Include(k => k.User)
            .FirstOrDefaultAsync(k => k.KeyHash == keyHash, ct);

        return entity is null ? null : ToRecord(entity);
    }

    public async Task<ApiKeyRecord?> FindByIdAsync(string id, CancellationToken ct = default)
    {
        if (!Guid.TryParse(id, out var guid)) return null;

        var entity = await _context.ApiKeys
            .AsNoTracking()
            .Include(k => k.User)
            .FirstOrDefaultAsync(k => k.Id == guid, ct);

        return entity is null ? null : ToRecord(entity);
    }

    public async Task AddAsync(ApiKeyRecord record, CancellationToken ct = default)
    {
        _context.ApiKeys.Add(new ApiKey
        {
            Id = Guid.Parse(record.Id),
            Name = record.Name,
            KeyHash = record.KeyHash,
            KeyPrefix = record.KeyPrefix,
            Scope = record.Scope.ToClaimValue(),
            UserId = ParseGuid(record.UserId),
            TenantId = ParseGuid(record.TenantId),
            Permissions = JsonSerializer.Serialize(record.Permissions),
            AllowedIps = record.AllowedIps.Count > 0 ? JsonSerializer.Serialize(record.AllowedIps) : null,
            RateLimitPerMinute = record.RateLimitPerMinute,
            CreatedAt = record.CreatedAtUtc.UtcDateTime,
            ExpiresAt = record.ExpiresAtUtc?.UtcDateTime,
            IsActive = record.IsActive,
            VaultReference = record.VaultReference,
            Description = record.Description,
        });

        await _context.SaveChangesAsync(ct);
    }

    public async Task<bool> ReplaceHashAsync(string id, string newKeyHash, string? vaultReference, CancellationToken ct = default)
    {
        if (!Guid.TryParse(id, out var guid)) return false;
        var entity = await _context.ApiKeys.FirstOrDefaultAsync(k => k.Id == guid, ct);
        if (entity is null) return false;

        entity.KeyHash = newKeyHash;
        entity.VaultReference = vaultReference ?? entity.VaultReference;
        await _context.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RevokeAsync(string id, CancellationToken ct = default)
    {
        if (!Guid.TryParse(id, out var guid)) return false;
        var entity = await _context.ApiKeys.FirstOrDefaultAsync(k => k.Id == guid, ct);
        if (entity is null) return false;

        entity.IsActive = false;
        await _context.SaveChangesAsync(ct);
        return true;
    }

    public async Task RecordUseAsync(ApiKeyRecord key, DateTimeOffset usedAtUtc, CancellationToken ct = default)
    {
        if (!Guid.TryParse(key.Id, out var guid)) return;
        var entity = await _context.ApiKeys.FirstOrDefaultAsync(k => k.Id == guid, ct);
        if (entity is null) return;

        entity.LastUsedAt = usedAtUtc.UtcDateTime;
        try
        {
            await _context.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Best effort: a concurrent rotate/revoke wins over the last-used stamp.
        }
    }

    private static ApiKeyRecord ToRecord(ApiKey e)
    {
        // The claims IAM has always added for a key that belongs to a user.
        var extra = new List<Claim>();
        if (e.User is not null)
        {
            extra.Add(new Claim(ClaimTypes.Email, e.User.Email));
            extra.Add(new Claim(ClaimTypes.Name, $"{e.User.FirstName} {e.User.LastName}"));
        }

        return new ApiKeyRecord
        {
            Id = e.Id.ToString("D"),
            KeyHash = e.KeyHash,
            KeyPrefix = e.KeyPrefix,
            Name = e.Name,
            Description = e.Description,
            // Unknown text in the column must never widen access.
            Scope = ApiKeyScopes.TryParse(e.Scope, out var scope) ? scope : ApiKeyScope.Read,
            TenantId = e.TenantId?.ToString("D"),
            UserId = e.UserId?.ToString("D"),
            Permissions = ParseList(e.Permissions),
            AllowedIps = ParseList(e.AllowedIps),
            RateLimitPerMinute = e.RateLimitPerMinute,
            CreatedAtUtc = AsUtc(e.CreatedAt),
            ExpiresAtUtc = e.ExpiresAt is { } exp ? AsUtc(exp) : null,
            IsActive = e.IsActive,
            VaultReference = e.VaultReference,
            ExtraClaims = extra,
        };
    }

    private static Guid? ParseGuid(string? value) => Guid.TryParse(value, out var g) ? g : null;

    private static DateTimeOffset AsUtc(DateTime value) =>
        new(value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static List<string> ParseList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>(); }
        catch (JsonException) { return new List<string>(); }
    }
}
