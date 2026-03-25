using IAM.Core.Entities;

namespace IAM.Core.Services;

public interface IApiKeyService
{
    Task<(ApiKey Key, string RawKey)> CreateApiKeyAsync(string name, Guid? userId, Guid? tenantId, List<string>? permissions = null, DateTime? expiresAt = null, int? rateLimitPerMinute = null, string? description = null, CancellationToken ct = default);
    Task<ApiKey?> ValidateApiKeyAsync(string rawKey, CancellationToken ct = default);
    Task<List<ApiKey>> GetApiKeysAsync(Guid? userId = null, Guid? tenantId = null, CancellationToken ct = default);
    Task<bool> RevokeApiKeyAsync(Guid keyId, CancellationToken ct = default);
    Task<(bool Success, string? NewRawKey)> RotateApiKeyAsync(Guid keyId, CancellationToken ct = default);
}
