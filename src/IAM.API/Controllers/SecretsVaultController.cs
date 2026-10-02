using System.Security.Claims;
using System.Text.Json;
using IAM.API.Authorization;
using IAM.Core.Entities;
using IAM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/secrets")]
[Authorize]
public class SecretsVaultController : ControllerBase
{
    private readonly ISecretsVaultService _secretsService;
    private readonly ISecretsAccessResolver _access;

    public SecretsVaultController(ISecretsVaultService secretsService, ISecretsAccessResolver access)
    {
        _secretsService = secretsService;
        _access = access;
    }

    // Task 4704. The service has no caller checks (SecretRotationWorker and SocialAuthService call it directly), so
    // the controller enforces them: SuperAdmin manages everything, a tenant administrator only secrets of their own
    // tenant (global secrets without a tenant are SuperAdmin only), everyone else gets 403 on every action.
    // Secrets created internally for identity providers (idp-client-secret-*) cannot be changed through this API.

    /// <summary>Name prefix of the vault entries SocialAuthService keeps for identity-provider client secrets.</summary>
    internal const string IdpSecretPrefix = "idp-client-secret-";

    private static bool IsIdpSecret(string? name) =>
        name != null && name.StartsWith(IdpSecretPrefix, StringComparison.OrdinalIgnoreCase);

    private ObjectResult Forbidden() =>
        StatusCode(StatusCodes.Status403Forbidden,
            new { error = "Only SuperAdmin or an administrator of the secret's tenant can manage secrets." });

    private ObjectResult IdpSecretLocked() =>
        StatusCode(StatusCodes.Status403Forbidden,
            new { error = "Identity-provider client secrets are managed by the identity provider settings, not this API." });

    /// <summary>
    /// Loads a secret for an action on it. The privilege check runs BEFORE the lookup, so a caller who manages
    /// nothing gets 403 for any id (no existence oracle); then the stored secret's tenant must be one the caller
    /// manages. Returns the error result, or null with the secret set.
    /// </summary>
    private async Task<(IActionResult? Error, SecretEntry? Secret)> LoadManagedAsync(Guid id, bool change)
    {
        var access = await _access.ResolveAsync(User, HttpContext.RequestAborted);
        if (!access.HasAny)
            return (Forbidden(), null);

        var secret = await _secretsService.GetSecretAsync(id, HttpContext.RequestAborted);
        if (secret == null)
            return (NotFound(new { error = "Secret not found" }), null);

        if (!access.CanManage(secret.TenantId))
            return (Forbidden(), null);

        if (change && IsIdpSecret(secret.Name))
            return (IdpSecretLocked(), null);

        return (null, secret);
    }

    /// <summary>
    /// Create a new secret entry with AES-256 encryption at rest.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> CreateSecret([FromBody] CreateSecretRequest request)
    {
        var access = await _access.ResolveAsync(User, HttpContext.RequestAborted);
        if (!access.HasAny || !access.CanManage(request.TenantId))
            return Forbidden();

        if (IsIdpSecret(request.Name))
            return BadRequest(new { error = $"Secret names starting with '{IdpSecretPrefix}' are reserved." });

        var userId = GetCurrentUserId();
        if (userId == null)
            return Unauthorized(new { error = "User identity not found" });

        var secret = await _secretsService.CreateSecretAsync(
            name: request.Name,
            plainTextValue: request.Value,
            tenantId: request.TenantId,
            secretType: request.SecretType ?? "Generic",
            description: request.Description,
            rotationScheduleJson: request.RotationSchedule != null
                ? JsonSerializer.Serialize(request.RotationSchedule)
                : null,
            tags: request.Tags != null ? JsonSerializer.Serialize(request.Tags) : null,
            createdByUserId: userId,
            ct: HttpContext.RequestAborted
        );

        return CreatedAtAction(nameof(GetSecret), new { id = secret.Id }, new
        {
            id = secret.Id,
            name = secret.Name,
            tenantId = secret.TenantId,
            secretType = secret.SecretType,
            description = secret.Description,
            version = secret.Version,
            isActive = secret.IsActive,
            nextRotationAt = secret.NextRotationAt,
            createdAt = secret.CreatedAt,
            warning = "The secret value is stored encrypted and will not be returned in API responses."
        });
    }

    /// <summary>
    /// Get a secret by ID (metadata only - encrypted value is never exposed via API).
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetSecret(Guid id)
    {
        var (error, secret) = await LoadManagedAsync(id, change: false);
        if (error != null)
            return error;

        return Ok(new
        {
            id = secret!.Id,
            name = secret.Name,
            tenantId = secret.TenantId,
            secretType = secret.SecretType,
            description = secret.Description,
            version = secret.Version,
            isActive = secret.IsActive,
            lastRotatedAt = secret.LastRotatedAt,
            nextRotationAt = secret.NextRotationAt,
            rotationSchedule = !string.IsNullOrEmpty(secret.RotationSchedule)
                ? JsonSerializer.Deserialize<object>(secret.RotationSchedule)
                : null,
            tags = !string.IsNullOrEmpty(secret.Tags)
                ? JsonSerializer.Deserialize<List<string>>(secret.Tags)
                : null,
            createdAt = secret.CreatedAt,
            updatedAt = secret.UpdatedAt
        });
    }

    /// <summary>
    /// List secrets filtered by tenant and/or type (metadata only).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetSecrets(
        [FromQuery] Guid? tenantId = null,
        [FromQuery] string? secretType = null,
        [FromQuery] bool? isActive = null)
    {
        var access = await _access.ResolveAsync(User, HttpContext.RequestAborted);
        if (!access.HasAny)
            return Forbidden();

        List<SecretEntry> secrets;
        if (tenantId.HasValue)
        {
            if (!access.CanManage(tenantId))
                return Forbidden();

            secrets = await _secretsService.GetSecretsAsync(tenantId, secretType, isActive, HttpContext.RequestAborted);
        }
        else if (access.IsPlatformAdmin)
        {
            secrets = await _secretsService.GetSecretsAsync(null, secretType, isActive, HttpContext.RequestAborted);
        }
        else
        {
            // A tenant administrator sees only the secrets of the tenants they administer (never global ones).
            secrets = new List<SecretEntry>();
            foreach (var tenant in access.AdministeredTenants)
                secrets.AddRange(await _secretsService.GetSecretsAsync(tenant, secretType, isActive, HttpContext.RequestAborted));
            secrets = secrets.OrderByDescending(s => s.UpdatedAt).ToList();
        }

        var response = secrets.Select(s => new
        {
            id = s.Id,
            name = s.Name,
            tenantId = s.TenantId,
            secretType = s.SecretType,
            description = s.Description,
            version = s.Version,
            isActive = s.IsActive,
            lastRotatedAt = s.LastRotatedAt,
            nextRotationAt = s.NextRotationAt,
            rotationSchedule = !string.IsNullOrEmpty(s.RotationSchedule)
                ? JsonSerializer.Deserialize<object>(s.RotationSchedule)
                : null,
            tags = !string.IsNullOrEmpty(s.Tags)
                ? JsonSerializer.Deserialize<List<string>>(s.Tags)
                : null,
            createdAt = s.CreatedAt,
            updatedAt = s.UpdatedAt
        });

        return Ok(response);
    }

    /// <summary>
    /// Update secret metadata. Does NOT change the encrypted value.
    /// </summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateSecret(Guid id, [FromBody] UpdateSecretRequest request)
    {
        var (error, _) = await LoadManagedAsync(id, change: true);
        if (error != null)
            return error;

        if (IsIdpSecret(request.Name))
            return BadRequest(new { error = $"Secret names starting with '{IdpSecretPrefix}' are reserved." });

        var secret = await _secretsService.UpdateSecretAsync(
            id,
            name: request.Name,
            description: request.Description,
            rotationScheduleJson: request.RotationSchedule != null
                ? JsonSerializer.Serialize(request.RotationSchedule)
                : null,
            tags: request.Tags != null ? JsonSerializer.Serialize(request.Tags) : null,
            secretType: request.SecretType,
            isActive: request.IsActive,
            ct: HttpContext.RequestAborted
        );

        if (secret == null)
            return NotFound(new { error = "Secret not found" });

        return Ok(new
        {
            id = secret.Id,
            name = secret.Name,
            tenantId = secret.TenantId,
            secretType = secret.SecretType,
            description = secret.Description,
            version = secret.Version,
            isActive = secret.IsActive,
            lastRotatedAt = secret.LastRotatedAt,
            nextRotationAt = secret.NextRotationAt,
            updatedAt = secret.UpdatedAt
        });
    }

    /// <summary>
    /// Rotate a secret - encrypts a new value, increments version, creates history entry.
    /// </summary>
    [HttpPost("rotate/{id:guid}")]
    public async Task<IActionResult> RotateSecret(Guid id, [FromBody] RotateSecretRequest request)
    {
        var (error, _) = await LoadManagedAsync(id, change: true);
        if (error != null)
            return error;

        var userId = GetCurrentUserId();

        try
        {
            var gracePeriod = request.GracePeriodHours.HasValue
                ? TimeSpan.FromHours(request.GracePeriodHours.Value)
                : (TimeSpan?)null;

            var secret = await _secretsService.RotateSecretAsync(
                id,
                newPlainTextValue: request.NewValue,
                rotationReason: request.Reason ?? "Manual",
                rotatedByUserId: userId,
                gracePeriod: gracePeriod,
                ct: HttpContext.RequestAborted
            );

            return Ok(new
            {
                id = secret.Id,
                name = secret.Name,
                version = secret.Version,
                lastRotatedAt = secret.LastRotatedAt,
                nextRotationAt = secret.NextRotationAt,
                gracePeriodEndsAt = gracePeriod.HasValue ? DateTime.UtcNow.Add(gracePeriod.Value) : (DateTime?)null,
                message = "Secret rotated successfully. New value is encrypted at rest."
            });
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = "Secret not found" });
        }
    }

    /// <summary>
    /// Get version history for a secret (metadata only - no decrypted values).
    /// </summary>
    [HttpGet("{id:guid}/history")]
    public async Task<IActionResult> GetSecretHistory(Guid id)
    {
        var (error, secret) = await LoadManagedAsync(id, change: false);
        if (error != null)
            return error;

        var history = await _secretsService.GetSecretHistoryAsync(id, HttpContext.RequestAborted);

        var response = history.Select(v => new
        {
            id = v.Id,
            version = v.Version,
            rotationReason = v.RotationReason,
            rotatedByUserId = v.RotatedByUserId,
            gracePeriodEndsAt = v.GracePeriodEndsAt,
            isRevoked = v.IsRevoked,
            createdAt = v.CreatedAt
        });

        return Ok(new
        {
            secretId = id,
            secretName = secret!.Name,
            currentVersion = secret.Version,
            history = response
        });
    }

    /// <summary>
    /// Delete (deactivate) a secret entry.
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteSecret(Guid id)
    {
        var (error, _) = await LoadManagedAsync(id, change: true);
        if (error != null)
            return error;

        var success = await _secretsService.DeleteSecretAsync(id, HttpContext.RequestAborted);

        if (!success)
            return NotFound(new { error = "Secret not found" });

        return Ok(new { message = "Secret deactivated successfully", id });
    }

    private Guid? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                       ?? User.FindFirst("sub")?.Value;

        if (Guid.TryParse(userIdClaim, out var userId))
            return userId;

        return null;
    }
}

public record CreateSecretRequest(
    string Name,
    string Value,
    Guid? TenantId = null,
    string? SecretType = null,
    string? Description = null,
    RotationScheduleInput? RotationSchedule = null,
    List<string>? Tags = null
);

public record UpdateSecretRequest(
    string? Name = null,
    string? Description = null,
    string? SecretType = null,
    bool? IsActive = null,
    RotationScheduleInput? RotationSchedule = null,
    List<string>? Tags = null
);

public record RotateSecretRequest(
    string NewValue,
    string? Reason = null,
    int? GracePeriodHours = null
);

public record RotationScheduleInput(
    int IntervalDays,
    int GracePeriodHours = 24,
    bool AutoGenerate = false,
    int AutoGenerateLength = 64
);
