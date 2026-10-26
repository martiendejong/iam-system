using IAM.Core.Entities;
using IAM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

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
    /// The caller on whose behalf provider mutations run (task 4697). Global admin = SuperAdmin or
    /// SystemAdmin. Password-login tokens carry no tenant_id; when a tenant_id claim IS present the
    /// service only honours tenant-admin authority inside that tenant. Same shape as
    /// GroupsController.TryGetActor.
    /// </summary>
    private IdentityProviderActor? TryGetActor()
    {
        var subject = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (!Guid.TryParse(subject, out var userId))
        {
            return null;
        }

        Guid? tenantId = null;
        var tenantClaim = User.FindFirst("tenant_id")?.Value;
        if (!string.IsNullOrWhiteSpace(tenantClaim))
        {
            if (!Guid.TryParse(tenantClaim, out var parsedTenantId))
            {
                return null;
            }
            tenantId = parsedTenantId;
        }

        return new IdentityProviderActor(
            userId,
            User.IsInRole("SuperAdmin") || User.IsInRole("SystemAdmin"),
            tenantId);
    }

    private ObjectResult Forbidden(IdentityProviderAccessDeniedException ex) =>
        StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });

    /// <summary>
    /// List identity providers, optionally filtered by tenant. SuperAdmin/SystemAdmin see all; a tenant
    /// admin sees their tenants' providers plus platform-wide ones (read-only); everyone else gets 403.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] Guid? tenantId = null, CancellationToken ct = default)
    {
        var actor = TryGetActor();
        if (actor == null)
            return Unauthorized();

        List<IdentityProvider> providers;
        try
        {
            providers = await _socialAuthService.GetIdentityProvidersForAdminAsync(tenantId, actor, ct);
        }
        catch (IdentityProviderAccessDeniedException ex)
        {
            return Forbidden(ex);
        }

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
    /// Public list of active identity providers for the login page.
    /// Anonymous by design: an unauthenticated visitor must be able to see which
    /// social login buttons to render. Returns only active providers and only the
    /// whitelisted fields in <see cref="PublicIdentityProviderDto"/> · never the
    /// entity itself, so no client credentials or tenant-internal metadata leak.
    /// </summary>
    [HttpGet("public")]
    [AllowAnonymous]
    public async Task<IActionResult> GetPublic([FromQuery] Guid? tenantId = null)
    {
        var providers = await _socialAuthService.GetIdentityProvidersAsync(tenantId);

        return Ok(providers
            .Where(p => p.IsActive)
            .Select(p => new PublicIdentityProviderDto
            {
                Id = p.Id,
                Name = p.Name,
                DisplayName = p.DisplayName,
                Type = p.Type.ToString()
            }));
    }

    /// <summary>
    /// Get an identity provider by ID. Same audience as the list: admins, or a tenant admin for their
    /// own tenant's or a platform-wide provider.
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct = default)
    {
        var actor = TryGetActor();
        if (actor == null)
            return Unauthorized();

        IdentityProvider? provider;
        try
        {
            provider = await _socialAuthService.GetIdentityProviderForAdminAsync(id, actor, ct);
        }
        catch (IdentityProviderAccessDeniedException ex)
        {
            return Forbidden(ex);
        }

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
    /// Create a new identity provider. SuperAdmin/SystemAdmin, or an admin of the target tenant.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateIdentityProviderRequest request, CancellationToken ct)
    {
        var actor = TryGetActor();
        if (actor == null)
            return Unauthorized();

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
            // Off unless explicitly enabled: auto-creating accounts from a social login is opt-in.
            AutoCreateUsers = request.AutoCreateUsers ?? false,
            DefaultRoleId = request.DefaultRoleId
        };

        try
        {
            var created = await _socialAuthService.CreateIdentityProviderAsync(provider, actor, ct);

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
        catch (IdentityProviderAccessDeniedException ex)
        {
            return Forbidden(ex);
        }
        catch (IdentityProviderValidationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Update an existing identity provider. SuperAdmin/SystemAdmin, or an admin of the provider's
    /// own tenant (who cannot move it to another tenant).
    /// </summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateIdentityProviderRequest request, CancellationToken ct)
    {
        var actor = TryGetActor();
        if (actor == null)
            return Unauthorized();

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
                // PUT replaces the whole provider, so an omitted flag falls back to the safe default.
                AutoCreateUsers = request.AutoCreateUsers ?? false,
                DefaultRoleId = request.DefaultRoleId
            };

            var updated = await _socialAuthService.UpdateIdentityProviderAsync(id, provider, actor, ct);

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
        catch (IdentityProviderAccessDeniedException ex)
        {
            return Forbidden(ex);
        }
        catch (IdentityProviderValidationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Delete an identity provider. SuperAdmin/SystemAdmin, or an admin of the provider's own tenant.
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var actor = TryGetActor();
        if (actor == null)
            return Unauthorized();

        try
        {
            var success = await _socialAuthService.DeleteIdentityProviderAsync(id, actor, ct);

            if (!success)
            {
                return NotFound(new { error = "Identity provider not found" });
            }

            return Ok(new { message = "Identity provider deleted successfully" });
        }
        catch (IdentityProviderAccessDeniedException ex)
        {
            return Forbidden(ex);
        }
    }
}

/// <summary>
/// Whitelisted identity provider fields that are safe to expose to anonymous
/// visitors on the login page. Intentionally excludes ClientId, ClientSecret,
/// MetadataUrl, AttributeMapping, tenant details and role mapping.
/// </summary>
public class PublicIdentityProviderDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Type { get; set; } = "";
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
