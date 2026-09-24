using System.Security.Claims;
using System.Text.Json;
using Hazina.Security.ApiKeys;
using IAM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/api-keys")]
[Authorize]
public class ApiKeysController : ControllerBase
{
    private readonly IApiKeyService _apiKeyService;

    public ApiKeysController(IApiKeyService apiKeyService)
    {
        _apiKeyService = apiKeyService;
    }

    /// <summary>
    /// Create a new API key. The raw key is returned ONCE in the response; the database keeps only its hash
    /// (the raw key is archived in Vault). <c>scope</c> is read (default) | write | admin.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> CreateApiKey([FromBody] CreateApiKeyRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
            return Unauthorized(new { error = "User identity not found" });

        if (!ApiKeyScopes.TryParse(request.Scope ?? ApiKeyScopes.Read, out var scope))
            return BadRequest(new { error = "scope must be one of: read, write, admin." });

        var tenantId = request.TenantId;
        var denied = CheckMayIssue(scope, ref tenantId);
        if (denied != null)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = denied });

        var (apiKey, rawKey) = await _apiKeyService.CreateApiKeyAsync(
            name: request.Name,
            userId: userId,
            tenantId: tenantId,
            permissions: request.Permissions,
            expiresAt: request.ExpiresAt,
            rateLimitPerMinute: request.RateLimitPerMinute,
            description: request.Description,
            scope: scope.ToClaimValue(),
            ct: HttpContext.RequestAborted
        );

        return CreatedAtAction(nameof(GetApiKeys), new
        {
            id = apiKey.Id,
            name = apiKey.Name,
            keyPrefix = apiKey.KeyPrefix,
            scope = apiKey.Scope,
            key = rawKey, // Returned ONCE - user must save this
            permissions = JsonSerializer.Deserialize<List<string>>(apiKey.Permissions),
            tenantId = apiKey.TenantId,
            rateLimitPerMinute = apiKey.RateLimitPerMinute,
            expiresAt = apiKey.ExpiresAt,
            description = apiKey.Description,
            createdAt = apiKey.CreatedAt,
            warning = "Save this key now. It will NOT be shown again."
        });
    }

    /// <summary>
    /// List the current user's API keys. Keys are masked - only prefix is shown.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetApiKeys([FromQuery] Guid? tenantId = null)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
            return Unauthorized(new { error = "User identity not found" });

        var keys = await _apiKeyService.GetApiKeysAsync(userId, tenantId, HttpContext.RequestAborted);

        var response = keys.Select(k => new
        {
            id = k.Id,
            name = k.Name,
            keyPrefix = k.KeyPrefix,
            keyHint = $"{k.KeyPrefix}****", // Masked display
            scope = k.Scope,
            permissions = JsonSerializer.Deserialize<List<string>>(k.Permissions),
            tenantId = k.TenantId,
            rateLimitPerMinute = k.RateLimitPerMinute,
            isActive = k.IsActive,
            expiresAt = k.ExpiresAt,
            lastUsedAt = k.LastUsedAt,
            description = k.Description,
            createdAt = k.CreatedAt
        });

        return Ok(response);
    }

    /// <summary>
    /// Revoke (deactivate) an API key. This action is irreversible.
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> RevokeApiKey(Guid id)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
            return Unauthorized(new { error = "User identity not found" });

        // Verify ownership before revoking
        var keys = await _apiKeyService.GetApiKeysAsync(userId, ct: HttpContext.RequestAborted);
        var keyToRevoke = keys.FirstOrDefault(k => k.Id == id);

        if (keyToRevoke == null)
            return NotFound(new { error = "API key not found or does not belong to you" });

        var success = await _apiKeyService.RevokeApiKeyAsync(id, HttpContext.RequestAborted);

        if (!success)
            return NotFound(new { error = "API key not found" });

        return Ok(new { message = "API key revoked successfully", id });
    }

    /// <summary>
    /// Rotate an API key - generates a new key while keeping the same ID and metadata.
    /// The new raw key is returned ONCE in the response.
    /// </summary>
    [HttpPost("{id:guid}/rotate")]
    public async Task<IActionResult> RotateApiKey(Guid id)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
            return Unauthorized(new { error = "User identity not found" });

        // Verify ownership before rotating
        var keys = await _apiKeyService.GetApiKeysAsync(userId, ct: HttpContext.RequestAborted);
        var keyToRotate = keys.FirstOrDefault(k => k.Id == id);

        if (keyToRotate == null)
            return NotFound(new { error = "API key not found or does not belong to you" });

        var (success, newRawKey) = await _apiKeyService.RotateApiKeyAsync(id, HttpContext.RequestAborted);

        if (!success || newRawKey == null)
            return NotFound(new { error = "API key not found" });

        return Ok(new
        {
            id,
            key = newRawKey, // Returned ONCE - user must save this
            message = "API key rotated successfully. Save the new key now.",
            warning = "The old key is no longer valid. This new key will NOT be shown again."
        });
    }

    /// <summary>
    /// Get usage statistics for a specific API key.
    /// </summary>
    [HttpGet("{id:guid}/usage")]
    public async Task<IActionResult> GetApiKeyUsage(Guid id)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
            return Unauthorized(new { error = "User identity not found" });

        // Verify ownership
        var keys = await _apiKeyService.GetApiKeysAsync(userId, ct: HttpContext.RequestAborted);
        var key = keys.FirstOrDefault(k => k.Id == id);

        if (key == null)
            return NotFound(new { error = "API key not found or does not belong to you" });

        return Ok(new
        {
            id = key.Id,
            name = key.Name,
            keyPrefix = key.KeyPrefix,
            isActive = key.IsActive,
            createdAt = key.CreatedAt,
            lastUsedAt = key.LastUsedAt,
            expiresAt = key.ExpiresAt,
            rateLimitPerMinute = key.RateLimitPerMinute,
            isExpired = key.ExpiresAt.HasValue && key.ExpiresAt.Value <= DateTime.UtcNow,
            daysSinceLastUse = key.LastUsedAt.HasValue
                ? (int)(DateTime.UtcNow - key.LastUsedAt.Value).TotalDays
                : (int?)null,
            ageInDays = (int)(DateTime.UtcNow - key.CreatedAt).TotalDays
        });
    }

    /// <summary>
    /// A key can never be minted with more power than the caller has:
    /// an API-key caller may only issue keys up to its own scope, inside its own tenant; an admin-scope key
    /// needs an admin user. Returns null when allowed.
    /// </summary>
    private string? CheckMayIssue(ApiKeyScope scope, ref Guid? tenantId)
    {
        if (User.IsApiKey())
        {
            if (User.GetApiKeyScope() is not { } callerScope || !callerScope.Satisfies(scope))
                return "An API key cannot issue a key with a higher scope than its own.";

            var ownTenant = User.GetApiKeyTenantId();
            if (!string.IsNullOrEmpty(ownTenant))
            {
                // A tenant key stays in its tenant: an omitted tenant means "mine", never "platform-wide".
                tenantId ??= Guid.TryParse(ownTenant, out var own) ? own : null;
                if (tenantId is null || !User.CanAccessTenant(tenantId.Value.ToString("D")))
                    return "An API key can only issue keys for its own tenant.";
            }
            else if (tenantId is { } requested && !User.CanAccessTenant(requested.ToString("D")))
            {
                return "This API key has no access to the requested tenant.";
            }

            return null;
        }

        // Admin scope is the one power that did not exist before scopes: it takes an admin user to hand it out.
        if (scope == ApiKeyScope.Admin && !User.IsInRole("SuperAdmin") && !User.IsInRole("SystemAdmin"))
            return "Only SuperAdmin/SystemAdmin users can issue admin-scope API keys.";

        return null;
    }

    /// <summary>
    /// Extract the current user's ID from the JWT or API key claims.
    /// </summary>
    private Guid? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                       ?? User.FindFirst("sub")?.Value;

        if (Guid.TryParse(userIdClaim, out var userId))
            return userId;

        return null;
    }
}

public record CreateApiKeyRequest(
    string Name,
    Guid? TenantId = null,
    List<string>? Permissions = null,
    DateTime? ExpiresAt = null,
    int? RateLimitPerMinute = null,
    string? Description = null,
    string? Scope = null
);
