using IAM.Core.Entities;
using IAM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/identity-providers")]
[Authorize]
public class IdentityProvidersController : ControllerBase
{
    private readonly ISocialAuthService _socialAuthService;

    public IdentityProvidersController(ISocialAuthService socialAuthService)
    {
        _socialAuthService = socialAuthService;
    }

    /// <summary>
    /// List all identity providers, optionally filtered by tenant
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] Guid? tenantId = null)
    {
        var providers = await _socialAuthService.GetIdentityProvidersAsync(tenantId);

        return Ok(providers.Select(p => new
        {
            id = p.Id,
            name = p.Name,
            displayName = p.DisplayName,
            type = p.Type.ToString(),
            tenantId = p.TenantId,
            tenantName = p.Tenant?.Name,
            clientId = p.ClientId,
            metadataUrl = p.MetadataUrl,
            isActive = p.IsActive,
            autoCreateUsers = p.AutoCreateUsers,
            defaultRoleId = p.DefaultRoleId,
            defaultRoleName = p.DefaultRole?.Name,
            createdAt = p.CreatedAt,
            updatedAt = p.UpdatedAt
        }));
    }

    /// <summary>
    /// Get an identity provider by ID
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var providers = await _socialAuthService.GetIdentityProvidersAsync();
        var provider = providers.FirstOrDefault(p => p.Id == id);

        if (provider == null)
        {
            return NotFound(new { error = "Identity provider not found" });
        }

        return Ok(new
        {
            id = provider.Id,
            name = provider.Name,
            displayName = provider.DisplayName,
            type = provider.Type.ToString(),
            tenantId = provider.TenantId,
            tenantName = provider.Tenant?.Name,
            clientId = provider.ClientId,
            metadataUrl = provider.MetadataUrl,
            attributeMapping = provider.AttributeMapping,
            isActive = provider.IsActive,
            autoCreateUsers = provider.AutoCreateUsers,
            defaultRoleId = provider.DefaultRoleId,
            defaultRoleName = provider.DefaultRole?.Name,
            createdAt = provider.CreatedAt,
            updatedAt = provider.UpdatedAt
        });
    }

    /// <summary>
    /// Create a new identity provider
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateIdentityProviderRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Name is required" });

        if (string.IsNullOrWhiteSpace(request.ClientId))
            return BadRequest(new { error = "Client ID is required" });

        if (!Enum.TryParse<IdentityProviderType>(request.Type, true, out var providerType))
            return BadRequest(new { error = "Invalid provider type" });

        var provider = new IdentityProvider
        {
            Name = request.Name,
            DisplayName = request.DisplayName ?? request.Name,
            Type = providerType,
            TenantId = request.TenantId,
            ClientId = request.ClientId,
            ClientSecret = request.ClientSecret ?? "",
            MetadataUrl = request.MetadataUrl,
            AttributeMapping = request.AttributeMapping,
            IsActive = request.IsActive ?? true,
            AutoCreateUsers = request.AutoCreateUsers ?? true,
            DefaultRoleId = request.DefaultRoleId
        };

        var created = await _socialAuthService.CreateIdentityProviderAsync(provider);

        return CreatedAtAction(nameof(GetById), new { id = created.Id }, new
        {
            id = created.Id,
            name = created.Name,
            displayName = created.DisplayName,
            type = created.Type.ToString(),
            tenantId = created.TenantId,
            clientId = created.ClientId,
            isActive = created.IsActive,
            autoCreateUsers = created.AutoCreateUsers,
            defaultRoleId = created.DefaultRoleId,
            createdAt = created.CreatedAt
        });
    }

    /// <summary>
    /// Update an existing identity provider
    /// </summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateIdentityProviderRequest request)
    {
        if (!Enum.TryParse<IdentityProviderType>(request.Type, true, out var providerType))
            return BadRequest(new { error = "Invalid provider type" });

        try
        {
            var provider = new IdentityProvider
            {
                Name = request.Name ?? "",
                DisplayName = request.DisplayName ?? "",
                Type = providerType,
                TenantId = request.TenantId,
                ClientId = request.ClientId ?? "",
                ClientSecret = request.ClientSecret ?? "",
                MetadataUrl = request.MetadataUrl,
                AttributeMapping = request.AttributeMapping,
                IsActive = request.IsActive ?? true,
                AutoCreateUsers = request.AutoCreateUsers ?? true,
                DefaultRoleId = request.DefaultRoleId
            };

            var updated = await _socialAuthService.UpdateIdentityProviderAsync(id, provider);

            return Ok(new
            {
                id = updated.Id,
                name = updated.Name,
                displayName = updated.DisplayName,
                type = updated.Type.ToString(),
                tenantId = updated.TenantId,
                clientId = updated.ClientId,
                isActive = updated.IsActive,
                autoCreateUsers = updated.AutoCreateUsers,
                defaultRoleId = updated.DefaultRoleId,
                updatedAt = updated.UpdatedAt
            });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Delete an identity provider
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var success = await _socialAuthService.DeleteIdentityProviderAsync(id);

        if (!success)
        {
            return NotFound(new { error = "Identity provider not found" });
        }

        return Ok(new { message = "Identity provider deleted successfully" });
    }
}

public class CreateIdentityProviderRequest
{
    public string Name { get; set; } = "";
    public string? DisplayName { get; set; }
    public string Type { get; set; } = "";
    public Guid? TenantId { get; set; }
    public string ClientId { get; set; } = "";
    public string? ClientSecret { get; set; }
    public string? MetadataUrl { get; set; }
    public string? AttributeMapping { get; set; }
    public bool? IsActive { get; set; }
    public bool? AutoCreateUsers { get; set; }
    public Guid? DefaultRoleId { get; set; }
}

public class UpdateIdentityProviderRequest
{
    public string? Name { get; set; }
    public string? DisplayName { get; set; }
    public string Type { get; set; } = "";
    public Guid? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? MetadataUrl { get; set; }
    public string? AttributeMapping { get; set; }
    public bool? IsActive { get; set; }
    public bool? AutoCreateUsers { get; set; }
    public Guid? DefaultRoleId { get; set; }
}
