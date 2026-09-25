using Hazina.Security.ApiKeys;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace IAM.Infrastructure.Services;

/// <summary>
/// Key lifecycle for IAM's controllers. Generation, hashing, Vault storage, rotation and cache
/// invalidation are Hazina.Security.ApiKeys' job (<see cref="IApiKeyManager"/>); this class keeps
/// IAM's entity-shaped API on top of it. Key VALIDATION is no longer here: it is the shared
/// middleware's, so IAM authenticates keys exactly like every other Jengo app.
/// </summary>
public class ApiKeyService : IApiKeyService
{
    private readonly IAMDbContext _context;
    private readonly IApiKeyManager _manager;

    public ApiKeyService(IAMDbContext context, IApiKeyManager manager)
    {
        _context = context;
        _manager = manager;
    }

    public async Task<(ApiKey Key, string RawKey)> CreateApiKeyAsync(
        string name,
        Guid? userId,
        Guid? tenantId,
        List<string>? permissions = null,
        DateTime? expiresAt = null,
        int? rateLimitPerMinute = null,
        string? description = null,
        string scope = "read",
        CancellationToken ct = default)
    {
        if (!ApiKeyScopes.TryParse(scope, out var parsedScope))
            throw new ArgumentException("Scope must be one of: read, write, admin.", nameof(scope));

        var issued = await _manager.CreateAsync(new ApiKeyCreateRequest
        {
            Id = Guid.NewGuid().ToString("D"),
            Name = name,
            Description = description,
            Scope = parsedScope,
            TenantId = tenantId?.ToString("D"),
            UserId = userId?.ToString("D"),
            Permissions = permissions ?? new List<string>(),
            RateLimitPerMinute = rateLimitPerMinute,
            ExpiresAtUtc = expiresAt is { } exp ? ToUtc(exp) : null,
        }, ct);

        // Return the entity AND the raw key (shown to the user ONCE; the database only ever holds the hash).
        var entity = await _context.ApiKeys.AsNoTracking().FirstAsync(k => k.Id == Guid.Parse(issued.Record.Id), ct);
        return (entity, issued.RawKey);
    }

    public async Task<List<ApiKey>> GetApiKeysAsync(
        Guid? userId = null,
        Guid? tenantId = null,
        CancellationToken ct = default)
    {
        var query = _context.ApiKeys.AsNoTracking().AsQueryable();

        if (userId.HasValue)
            query = query.Where(k => k.UserId == userId.Value);

        if (tenantId.HasValue)
            query = query.Where(k => k.TenantId == tenantId.Value);

        return await query
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync(ct);
    }

    public Task<bool> RevokeApiKeyAsync(Guid keyId, CancellationToken ct = default) =>
        _manager.RevokeAsync(keyId.ToString("D"), ct);

    public async Task<(bool Success, string? NewRawKey)> RotateApiKeyAsync(Guid keyId, CancellationToken ct = default)
    {
        var rotated = await _manager.RotateAsync(keyId.ToString("D"), ct);
        return rotated is null ? (false, null) : (true, rotated.RawKey);
    }

    private static DateTimeOffset ToUtc(DateTime value) =>
        new(value.Kind == DateTimeKind.Utc ? value : value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
