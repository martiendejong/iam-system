using System.Security.Claims;
using System.Text.Json;
using IAM.Core.Entities;
using IAM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/service-accounts")]
public class ServiceAccountsController : ControllerBase
{
    private readonly IServiceAccountService _serviceAccountService;

    public ServiceAccountsController(IServiceAccountService serviceAccountService)
    {
        _serviceAccountService = serviceAccountService;
    }

    /// <summary>
    /// Create a new service account. The client secret is returned ONCE in the response.
    /// </summary>
    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Create([FromBody] CreateServiceAccountRequest request)
    {
        var (account, rawSecret) = await _serviceAccountService.CreateAsync(
            name: request.Name,
            tenantId: request.TenantId,
            type: request.Type,
            permissions: request.Permissions,
            certificateThumbprint: request.CertificateThumbprint,
            description: request.Description,
            ct: HttpContext.RequestAborted);

        return CreatedAtAction(nameof(GetById), new { id = account.Id }, new
        {
            id = account.Id,
            name = account.Name,
            clientId = account.ClientId,
            clientSecret = rawSecret,
            tenantId = account.TenantId,
            type = account.Type.ToString(),
            permissions = JsonSerializer.Deserialize<List<string>>(account.Permissions),
            certificateThumbprint = account.CertificateThumbprint,
            description = account.Description,
            isActive = account.IsActive,
            createdAt = account.CreatedAt,
            warning = "Save the client secret now. It will NOT be shown again."
        });
    }

    /// <summary>
    /// List service accounts with optional filtering.
    /// </summary>
    [HttpGet]
    [Authorize]
    public async Task<IActionResult> List(
        [FromQuery] Guid? tenantId = null,
        [FromQuery] ServiceAccountType? type = null,
        [FromQuery] bool? isActive = null)
    {
        var accounts = await _serviceAccountService.ListAsync(
            tenantId, type, isActive, HttpContext.RequestAborted);

        var response = accounts.Select(a => new
        {
            id = a.Id,
            name = a.Name,
            clientId = a.ClientId,
            tenantId = a.TenantId,
            tenantName = a.Tenant?.Name,
            type = a.Type.ToString(),
            permissions = JsonSerializer.Deserialize<List<string>>(a.Permissions),
            certificateThumbprint = a.CertificateThumbprint,
            description = a.Description,
            isActive = a.IsActive,
            createdAt = a.CreatedAt,
            updatedAt = a.UpdatedAt,
            lastAuthenticatedAt = a.LastAuthenticatedAt
        });

        return Ok(response);
    }

    /// <summary>
    /// Get a specific service account by ID.
    /// </summary>
    [HttpGet("{id:guid}")]
    [Authorize]
    public async Task<IActionResult> GetById(Guid id)
    {
        var account = await _serviceAccountService.GetByIdAsync(id, HttpContext.RequestAborted);
        if (account == null)
            return NotFound(new { error = "Service account not found" });

        return Ok(new
        {
            id = account.Id,
            name = account.Name,
            clientId = account.ClientId,
            tenantId = account.TenantId,
            tenantName = account.Tenant?.Name,
            type = account.Type.ToString(),
            permissions = JsonSerializer.Deserialize<List<string>>(account.Permissions),
            certificateThumbprint = account.CertificateThumbprint,
            description = account.Description,
            isActive = account.IsActive,
            createdAt = account.CreatedAt,
            updatedAt = account.UpdatedAt,
            lastAuthenticatedAt = account.LastAuthenticatedAt
        });
    }

    /// <summary>
    /// Update a service account's metadata.
    /// </summary>
    [HttpPut("{id:guid}")]
    [Authorize]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateServiceAccountRequest request)
    {
        var account = await _serviceAccountService.UpdateAsync(
            id,
            name: request.Name,
            permissions: request.Permissions,
            type: request.Type,
            certificateThumbprint: request.CertificateThumbprint,
            description: request.Description,
            isActive: request.IsActive,
            ct: HttpContext.RequestAborted);

        if (account == null)
            return NotFound(new { error = "Service account not found" });

        return Ok(new
        {
            id = account.Id,
            name = account.Name,
            clientId = account.ClientId,
            tenantId = account.TenantId,
            type = account.Type.ToString(),
            permissions = JsonSerializer.Deserialize<List<string>>(account.Permissions),
            certificateThumbprint = account.CertificateThumbprint,
            description = account.Description,
            isActive = account.IsActive,
            updatedAt = account.UpdatedAt
        });
    }

    /// <summary>
    /// Delete a service account.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [Authorize]
    public async Task<IActionResult> Delete(Guid id)
    {
        var success = await _serviceAccountService.DeleteAsync(id, HttpContext.RequestAborted);
        if (!success)
            return NotFound(new { error = "Service account not found" });

        return Ok(new { message = "Service account deleted successfully", id });
    }

    /// <summary>
    /// Rotate the client secret. The new secret is returned ONCE.
    /// </summary>
    [HttpPost("{id:guid}/rotate-secret")]
    [Authorize]
    public async Task<IActionResult> RotateSecret(Guid id)
    {
        var (success, newRawSecret) = await _serviceAccountService.RotateSecretAsync(id, HttpContext.RequestAborted);

        if (!success || newRawSecret == null)
            return NotFound(new { error = "Service account not found" });

        return Ok(new
        {
            id,
            clientSecret = newRawSecret,
            message = "Client secret rotated successfully. Save the new secret now.",
            warning = "The old secret is no longer valid. This new secret will NOT be shown again."
        });
    }

    /// <summary>
    /// Authenticate using client_credentials grant.
    /// This endpoint does NOT require an existing bearer token.
    /// </summary>
    [HttpPost("token")]
    [AllowAnonymous]
    public async Task<IActionResult> Token([FromBody] ClientCredentialsRequest request)
    {
        if (request.GrantType != "client_credentials")
            return BadRequest(new { error = "unsupported_grant_type", error_description = "Only client_credentials grant type is supported" });

        var (success, accessToken, expiresAt) = await _serviceAccountService.AuthenticateAsync(
            request.ClientId, request.ClientSecret, HttpContext.RequestAborted);

        if (!success || accessToken == null)
            return Unauthorized(new { error = "invalid_client", error_description = "Invalid client_id or client_secret" });

        return Ok(new
        {
            access_token = accessToken,
            token_type = "Bearer",
            expires_at = expiresAt,
            expires_in = expiresAt.HasValue ? (int)(expiresAt.Value - DateTime.UtcNow).TotalSeconds : 0
        });
    }

    /// <summary>
    /// Exchange a token for one scoped to a different service (RFC 8693).
    /// Requires a valid bearer token as the subject_token.
    /// </summary>
    [HttpPost("exchange")]
    [Authorize]
    public async Task<IActionResult> Exchange([FromBody] TokenExchangeRequest request)
    {
        if (request.GrantType != "urn:ietf:params:oauth:grant-type:token-exchange")
            return BadRequest(new { error = "unsupported_grant_type", error_description = "Only urn:ietf:params:oauth:grant-type:token-exchange is supported" });

        if (string.IsNullOrEmpty(request.SubjectToken))
            return BadRequest(new { error = "invalid_request", error_description = "subject_token is required" });

        if (string.IsNullOrEmpty(request.Resource))
            return BadRequest(new { error = "invalid_request", error_description = "resource (target service) is required" });

        var (success, accessToken, expiresAt) = await _serviceAccountService.ExchangeTokenAsync(
            request.SubjectToken, request.Resource, request.Scope, HttpContext.RequestAborted);

        if (!success || accessToken == null)
            return Unauthorized(new { error = "invalid_grant", error_description = "Token exchange failed. The subject token may be invalid or expired." });

        return Ok(new
        {
            access_token = accessToken,
            issued_token_type = "urn:ietf:params:oauth:token-type:access_token",
            token_type = "Bearer",
            expires_at = expiresAt,
            expires_in = expiresAt.HasValue ? (int)(expiresAt.Value - DateTime.UtcNow).TotalSeconds : 0,
            scope = request.Scope
        });
    }
}

// ---- Request DTOs ----

public record CreateServiceAccountRequest(
    string Name,
    Guid? TenantId = null,
    ServiceAccountType Type = ServiceAccountType.Api,
    List<string>? Permissions = null,
    string? CertificateThumbprint = null,
    string? Description = null
);

public record UpdateServiceAccountRequest(
    string? Name = null,
    List<string>? Permissions = null,
    ServiceAccountType? Type = null,
    string? CertificateThumbprint = null,
    string? Description = null,
    bool? IsActive = null
);

public record ClientCredentialsRequest(
    string GrantType,
    string ClientId,
    string ClientSecret
);

public record TokenExchangeRequest(
    string GrantType,
    string SubjectToken,
    string? SubjectTokenType = null,
    string? Resource = null,
    string? Scope = null
);
