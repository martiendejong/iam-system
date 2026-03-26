using IAM.Core.Entities;
using IAM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

/// <summary>
/// Manages claims mapping rules and token configuration per OAuth2 client.
/// Provides token preview to see what claims would be emitted for a user.
/// </summary>
[ApiController]
[Route("api/claims-mapping")]
[Authorize]
public class ClaimsMappingController : ControllerBase
{
    private readonly IClaimsMappingService _claimsMappingService;

    public ClaimsMappingController(IClaimsMappingService claimsMappingService)
    {
        _claimsMappingService = claimsMappingService;
    }

    // ---- Claims Mapping Rules ----

    /// <summary>
    /// Get all claims mapping rules for a client.
    /// </summary>
    [HttpGet("rules")]
    public async Task<IActionResult> GetRules([FromQuery] string clientId, [FromQuery] Guid? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            return BadRequest(new { error = "Client ID is required" });

        var rules = await _claimsMappingService.GetRulesAsync(clientId, tenantId);
        return Ok(rules.Select(r => MapRuleToResponse(r)));
    }

    /// <summary>
    /// Get a single claims mapping rule by ID.
    /// </summary>
    [HttpGet("rules/{id:guid}")]
    public async Task<IActionResult> GetRule(Guid id)
    {
        var rule = await _claimsMappingService.GetRuleAsync(id);
        if (rule == null)
            return NotFound(new { error = "Claims mapping rule not found" });

        return Ok(MapRuleToResponse(rule));
    }

    /// <summary>
    /// Create a new claims mapping rule.
    /// </summary>
    [HttpPost("rules")]
    public async Task<IActionResult> CreateRule([FromBody] CreateClaimsMappingRuleRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ClientId))
            return BadRequest(new { error = "Client ID is required" });
        if (string.IsNullOrWhiteSpace(request.SourcePath))
            return BadRequest(new { error = "Source path is required" });
        if (string.IsNullOrWhiteSpace(request.TargetClaim))
            return BadRequest(new { error = "Target claim is required" });

        var rule = new ClaimsMappingRule
        {
            ClientId = request.ClientId,
            TenantId = request.TenantId,
            SourceType = request.SourceType,
            SourcePath = request.SourcePath,
            TargetClaim = request.TargetClaim,
            Transform = request.Transform,
            TransformPattern = request.TransformPattern,
            Priority = request.Priority,
            IsActive = request.IsActive
        };

        var created = await _claimsMappingService.CreateRuleAsync(rule);
        return Created($"/api/claims-mapping/rules/{created.Id}", MapRuleToResponse(created));
    }

    /// <summary>
    /// Update an existing claims mapping rule.
    /// </summary>
    [HttpPut("rules/{id:guid}")]
    public async Task<IActionResult> UpdateRule(Guid id, [FromBody] UpdateClaimsMappingRuleRequest request)
    {
        try
        {
            var rule = new ClaimsMappingRule
            {
                SourceType = request.SourceType,
                SourcePath = request.SourcePath,
                TargetClaim = request.TargetClaim,
                Transform = request.Transform,
                TransformPattern = request.TransformPattern,
                Priority = request.Priority,
                IsActive = request.IsActive
            };

            var updated = await _claimsMappingService.UpdateRuleAsync(id, rule);
            return Ok(MapRuleToResponse(updated));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Delete a claims mapping rule.
    /// </summary>
    [HttpDelete("rules/{id:guid}")]
    public async Task<IActionResult> DeleteRule(Guid id)
    {
        var result = await _claimsMappingService.DeleteRuleAsync(id);
        if (!result)
            return NotFound(new { error = "Claims mapping rule not found" });

        return Ok(new { message = "Claims mapping rule deleted successfully" });
    }

    // ---- Token Configuration ----

    /// <summary>
    /// Get token configuration for a client.
    /// </summary>
    [HttpGet("token-config")]
    public async Task<IActionResult> GetTokenConfiguration([FromQuery] string clientId, [FromQuery] Guid? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            return BadRequest(new { error = "Client ID is required" });

        var config = await _claimsMappingService.GetTokenConfigurationAsync(clientId, tenantId);
        if (config == null)
        {
            // Return defaults when no custom configuration exists
            return Ok(new
            {
                clientId,
                tenantId,
                accessTokenLifetimeMinutes = 15,
                refreshTokenLifetimeDays = 7,
                includeRoles = true,
                includePermissions = false,
                includeGroups = false,
                customNamespace = (string?)null,
                isDefault = true
            });
        }

        return Ok(MapTokenConfigToResponse(config));
    }

    /// <summary>
    /// Create or update token configuration for a client.
    /// </summary>
    [HttpPut("token-config")]
    public async Task<IActionResult> UpsertTokenConfiguration([FromBody] UpsertTokenConfigurationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ClientId))
            return BadRequest(new { error = "Client ID is required" });

        if (request.AccessTokenLifetimeMinutes < 1 || request.AccessTokenLifetimeMinutes > 1440)
            return BadRequest(new { error = "Access token lifetime must be between 1 and 1440 minutes" });

        if (request.RefreshTokenLifetimeDays < 1 || request.RefreshTokenLifetimeDays > 365)
            return BadRequest(new { error = "Refresh token lifetime must be between 1 and 365 days" });

        var config = new TokenConfiguration
        {
            ClientId = request.ClientId,
            TenantId = request.TenantId,
            AccessTokenLifetimeMinutes = request.AccessTokenLifetimeMinutes,
            RefreshTokenLifetimeDays = request.RefreshTokenLifetimeDays,
            IncludeRoles = request.IncludeRoles,
            IncludePermissions = request.IncludePermissions,
            IncludeGroups = request.IncludeGroups,
            CustomNamespace = request.CustomNamespace
        };

        var result = await _claimsMappingService.UpsertTokenConfigurationAsync(config);
        return Ok(MapTokenConfigToResponse(result));
    }

    // ---- Token Preview ----

    /// <summary>
    /// Preview what claims would be included in a token for a specific user and client.
    /// </summary>
    [HttpGet("preview")]
    public async Task<IActionResult> PreviewToken(
        [FromQuery] string clientId,
        [FromQuery] Guid userId,
        [FromQuery] Guid? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            return BadRequest(new { error = "Client ID is required" });
        if (userId == Guid.Empty)
            return BadRequest(new { error = "User ID is required" });

        try
        {
            var preview = await _claimsMappingService.PreviewTokenAsync(clientId, userId, tenantId);
            return Ok(preview);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    // ---- Private Helpers ----

    private static object MapRuleToResponse(ClaimsMappingRule rule)
    {
        return new
        {
            id = rule.Id,
            clientId = rule.ClientId,
            tenantId = rule.TenantId,
            sourceType = rule.SourceType.ToString(),
            sourcePath = rule.SourcePath,
            targetClaim = rule.TargetClaim,
            transform = rule.Transform.ToString(),
            transformPattern = rule.TransformPattern,
            priority = rule.Priority,
            isActive = rule.IsActive,
            createdAt = rule.CreatedAt
        };
    }

    private static object MapTokenConfigToResponse(TokenConfiguration config)
    {
        return new
        {
            id = config.Id,
            clientId = config.ClientId,
            tenantId = config.TenantId,
            accessTokenLifetimeMinutes = config.AccessTokenLifetimeMinutes,
            refreshTokenLifetimeDays = config.RefreshTokenLifetimeDays,
            includeRoles = config.IncludeRoles,
            includePermissions = config.IncludePermissions,
            includeGroups = config.IncludeGroups,
            customNamespace = config.CustomNamespace,
            createdAt = config.CreatedAt,
            updatedAt = config.UpdatedAt,
            isDefault = false
        };
    }
}

// ---- Request DTOs ----

public record CreateClaimsMappingRuleRequest(
    string ClientId,
    Guid? TenantId,
    ClaimSourceType SourceType,
    string SourcePath,
    string TargetClaim,
    ClaimTransform Transform = ClaimTransform.None,
    string? TransformPattern = null,
    int Priority = 100,
    bool IsActive = true
);

public record UpdateClaimsMappingRuleRequest(
    ClaimSourceType SourceType,
    string SourcePath,
    string TargetClaim,
    ClaimTransform Transform = ClaimTransform.None,
    string? TransformPattern = null,
    int Priority = 100,
    bool IsActive = true
);

public record UpsertTokenConfigurationRequest(
    string ClientId,
    Guid? TenantId,
    int AccessTokenLifetimeMinutes = 15,
    int RefreshTokenLifetimeDays = 7,
    bool IncludeRoles = true,
    bool IncludePermissions = false,
    bool IncludeGroups = false,
    string? CustomNamespace = null
);
