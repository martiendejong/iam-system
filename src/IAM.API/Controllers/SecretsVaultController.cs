using System.Security.Claims;
using System.Text.Json;
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

    public SecretsVaultController(ISecretsVaultService secretsService)
    {
        _secretsService = secretsService;
    }

    /// <summary>
    /// Create a new secret entry with AES-256 encryption at rest.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> CreateSecret([FromBody] CreateSecretRequest request)
    {
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
        var secret = await _secretsService.GetSecretAsync(id, HttpContext.RequestAborted);
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
        var secrets = await _secretsService.GetSecretsAsync(tenantId, secretType, isActive, HttpContext.RequestAborted);

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
        // Verify secret exists
        var secret = await _secretsService.GetSecretAsync(id, HttpContext.RequestAborted);
        if (secret == null)
            return NotFound(new { error = "Secret not found" });

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
            secretName = secret.Name,
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
