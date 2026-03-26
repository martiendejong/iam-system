using IAM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

/// <summary>
/// SCIM 2.0 (RFC 7644) provisioning endpoints.
/// Uses per-tenant SCIM bearer token authentication instead of JWT.
/// </summary>
[ApiController]
[Route("scim/v2")]
public class ScimController : ControllerBase
{
    private readonly IScimService _scimService;

    public ScimController(IScimService scimService)
    {
        _scimService = scimService;
    }

    // ---- User Endpoints ----

    /// <summary>
    /// List/search users (SCIM 2.0)
    /// </summary>
    [AllowAnonymous]
    [HttpGet("Users")]
    public async Task<IActionResult> ListUsers(
        [FromQuery] string? filter,
        [FromQuery] int startIndex = 1,
        [FromQuery] int count = 100,
        [FromQuery] string? sortBy = null,
        [FromQuery] string? sortOrder = null,
        [FromQuery] string? attributes = null,
        [FromQuery] string? excludedAttributes = null)
    {
        var (tenantId, error) = await AuthenticateScimRequest();
        if (error != null) return error;

        var options = new ScimQueryOptions
        {
            Filter = filter,
            StartIndex = startIndex,
            Count = count,
            SortBy = sortBy,
            SortOrder = sortOrder,
            Attributes = attributes,
            ExcludedAttributes = excludedAttributes
        };

        var result = await _scimService.ListUsersAsync(tenantId!.Value, options);
        return Ok(result);
    }

    /// <summary>
    /// Create user (SCIM 2.0)
    /// </summary>
    [AllowAnonymous]
    [HttpPost("Users")]
    public async Task<IActionResult> CreateUser([FromBody] ScimUserResource scimUser)
    {
        var (tenantId, error) = await AuthenticateScimRequest();
        if (error != null) return error;

        try
        {
            var created = await _scimService.CreateUserAsync(tenantId!.Value, scimUser);
            return Created($"/scim/v2/Users/{created.Id}", created);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new ScimError
            {
                Status = 409,
                Detail = ex.Message,
                ScimType = "uniqueness"
            });
        }
    }

    /// <summary>
    /// Get user by ID (SCIM 2.0)
    /// </summary>
    [AllowAnonymous]
    [HttpGet("Users/{id:guid}")]
    public async Task<IActionResult> GetUser(Guid id)
    {
        var (tenantId, error) = await AuthenticateScimRequest();
        if (error != null) return error;

        var user = await _scimService.GetUserAsync(tenantId!.Value, id);
        if (user == null)
        {
            return NotFound(new ScimError
            {
                Status = 404,
                Detail = "User not found"
            });
        }

        return Ok(user);
    }

    /// <summary>
    /// Replace user (full update) (SCIM 2.0)
    /// </summary>
    [AllowAnonymous]
    [HttpPut("Users/{id:guid}")]
    public async Task<IActionResult> ReplaceUser(Guid id, [FromBody] ScimUserResource scimUser)
    {
        var (tenantId, error) = await AuthenticateScimRequest();
        if (error != null) return error;

        try
        {
            var updated = await _scimService.ReplaceUserAsync(tenantId!.Value, id, scimUser);
            return Ok(updated);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new ScimError
            {
                Status = 404,
                Detail = ex.Message
            });
        }
    }

    /// <summary>
    /// Patch user (partial update) (SCIM 2.0)
    /// </summary>
    [AllowAnonymous]
    [HttpPatch("Users/{id:guid}")]
    public async Task<IActionResult> PatchUser(Guid id, [FromBody] ScimPatchRequest patchRequest)
    {
        var (tenantId, error) = await AuthenticateScimRequest();
        if (error != null) return error;

        try
        {
            var updated = await _scimService.PatchUserAsync(tenantId!.Value, id, patchRequest);
            return Ok(updated);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new ScimError
            {
                Status = 404,
                Detail = ex.Message
            });
        }
    }

    /// <summary>
    /// Delete user (SCIM 2.0)
    /// </summary>
    [AllowAnonymous]
    [HttpDelete("Users/{id:guid}")]
    public async Task<IActionResult> DeleteUser(Guid id)
    {
        var (tenantId, error) = await AuthenticateScimRequest();
        if (error != null) return error;

        var result = await _scimService.DeleteUserAsync(tenantId!.Value, id);
        if (!result)
        {
            return NotFound(new ScimError
            {
                Status = 404,
                Detail = "User not found"
            });
        }

        return NoContent();
    }

    // ---- Group Endpoints ----

    /// <summary>
    /// List/search groups (SCIM 2.0)
    /// </summary>
    [AllowAnonymous]
    [HttpGet("Groups")]
    public async Task<IActionResult> ListGroups(
        [FromQuery] string? filter,
        [FromQuery] int startIndex = 1,
        [FromQuery] int count = 100,
        [FromQuery] string? sortBy = null,
        [FromQuery] string? sortOrder = null)
    {
        var (tenantId, error) = await AuthenticateScimRequest();
        if (error != null) return error;

        var options = new ScimQueryOptions
        {
            Filter = filter,
            StartIndex = startIndex,
            Count = count,
            SortBy = sortBy,
            SortOrder = sortOrder
        };

        var result = await _scimService.ListGroupsAsync(tenantId!.Value, options);
        return Ok(result);
    }

    /// <summary>
    /// Create group (SCIM 2.0)
    /// </summary>
    [AllowAnonymous]
    [HttpPost("Groups")]
    public async Task<IActionResult> CreateGroup([FromBody] ScimGroupResource scimGroup)
    {
        var (tenantId, error) = await AuthenticateScimRequest();
        if (error != null) return error;

        try
        {
            var created = await _scimService.CreateGroupAsync(tenantId!.Value, scimGroup);
            return Created($"/scim/v2/Groups/{created.Id}", created);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new ScimError
            {
                Status = 409,
                Detail = ex.Message,
                ScimType = "uniqueness"
            });
        }
    }

    /// <summary>
    /// Get group by ID (SCIM 2.0)
    /// </summary>
    [AllowAnonymous]
    [HttpGet("Groups/{id:guid}")]
    public async Task<IActionResult> GetGroup(Guid id)
    {
        var (tenantId, error) = await AuthenticateScimRequest();
        if (error != null) return error;

        var group = await _scimService.GetGroupAsync(tenantId!.Value, id);
        if (group == null)
        {
            return NotFound(new ScimError
            {
                Status = 404,
                Detail = "Group not found"
            });
        }

        return Ok(group);
    }

    /// <summary>
    /// Replace group (full update) (SCIM 2.0)
    /// </summary>
    [AllowAnonymous]
    [HttpPut("Groups/{id:guid}")]
    public async Task<IActionResult> ReplaceGroup(Guid id, [FromBody] ScimGroupResource scimGroup)
    {
        var (tenantId, error) = await AuthenticateScimRequest();
        if (error != null) return error;

        try
        {
            var updated = await _scimService.ReplaceGroupAsync(tenantId!.Value, id, scimGroup);
            return Ok(updated);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new ScimError
            {
                Status = 404,
                Detail = ex.Message
            });
        }
    }

    /// <summary>
    /// Patch group (partial update) (SCIM 2.0)
    /// </summary>
    [AllowAnonymous]
    [HttpPatch("Groups/{id:guid}")]
    public async Task<IActionResult> PatchGroup(Guid id, [FromBody] ScimPatchRequest patchRequest)
    {
        var (tenantId, error) = await AuthenticateScimRequest();
        if (error != null) return error;

        try
        {
            var updated = await _scimService.PatchGroupAsync(tenantId!.Value, id, patchRequest);
            return Ok(updated);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new ScimError
            {
                Status = 404,
                Detail = ex.Message
            });
        }
    }

    /// <summary>
    /// Delete group (SCIM 2.0)
    /// </summary>
    [AllowAnonymous]
    [HttpDelete("Groups/{id:guid}")]
    public async Task<IActionResult> DeleteGroup(Guid id)
    {
        var (tenantId, error) = await AuthenticateScimRequest();
        if (error != null) return error;

        var result = await _scimService.DeleteGroupAsync(tenantId!.Value, id);
        if (!result)
        {
            return NotFound(new ScimError
            {
                Status = 404,
                Detail = "Group not found"
            });
        }

        return NoContent();
    }

    // ---- Discovery Endpoints ----

    /// <summary>
    /// SCIM Service Provider Configuration (RFC 7643 Section 5)
    /// </summary>
    [AllowAnonymous]
    [HttpGet("ServiceProviderConfig")]
    public IActionResult GetServiceProviderConfig()
    {
        return Ok(new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:ServiceProviderConfig" },
            documentationUri = "https://tools.ietf.org/html/rfc7644",
            patch = new { supported = true },
            bulk = new { supported = false, maxOperations = 0, maxPayloadSize = 0 },
            filter = new { supported = true, maxResults = 1000 },
            changePassword = new { supported = false },
            sort = new { supported = true },
            etag = new { supported = false },
            authenticationSchemes = new[]
            {
                new
                {
                    type = "oauthbearertoken",
                    name = "OAuth Bearer Token",
                    description = "Authentication scheme using per-tenant SCIM bearer tokens",
                    specUri = "https://tools.ietf.org/html/rfc6750",
                    primary = true
                }
            },
            meta = new
            {
                resourceType = "ServiceProviderConfig",
                location = "/scim/v2/ServiceProviderConfig"
            }
        });
    }

    /// <summary>
    /// SCIM Schemas endpoint (RFC 7643 Section 7)
    /// </summary>
    [AllowAnonymous]
    [HttpGet("Schemas")]
    public IActionResult GetSchemas()
    {
        return Ok(new
        {
            schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:ListResponse" },
            totalResults = 2,
            itemsPerPage = 2,
            startIndex = 1,
            Resources = new object[]
            {
                GetUserSchema(),
                GetGroupSchema()
            }
        });
    }

    /// <summary>
    /// SCIM ResourceTypes endpoint (RFC 7643 Section 6)
    /// </summary>
    [AllowAnonymous]
    [HttpGet("ResourceTypes")]
    public IActionResult GetResourceTypes()
    {
        return Ok(new
        {
            schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:ListResponse" },
            totalResults = 2,
            itemsPerPage = 2,
            startIndex = 1,
            Resources = new object[]
            {
                new
                {
                    schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:ResourceType" },
                    id = "User",
                    name = "User",
                    endpoint = "/scim/v2/Users",
                    schema = "urn:ietf:params:scim:schemas:core:2.0:User",
                    meta = new { resourceType = "ResourceType", location = "/scim/v2/ResourceTypes/User" }
                },
                new
                {
                    schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:ResourceType" },
                    id = "Group",
                    name = "Group",
                    endpoint = "/scim/v2/Groups",
                    schema = "urn:ietf:params:scim:schemas:core:2.0:Group",
                    meta = new { resourceType = "ResourceType", location = "/scim/v2/ResourceTypes/Group" }
                }
            }
        });
    }

    // ---- Admin Endpoints (JWT-authenticated, for the admin UI) ----

    /// <summary>
    /// Create a SCIM token for a tenant (admin UI)
    /// </summary>
    [HttpPost("/api/scim/tokens")]
    [Authorize]
    public async Task<IActionResult> CreateToken([FromBody] CreateScimTokenRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Token name is required" });

        if (request.TenantId == Guid.Empty)
            return BadRequest(new { error = "Tenant ID is required" });

        var (token, plainText) = await _scimService.CreateTokenAsync(
            request.TenantId, request.Name, request.Description, request.ExpiresAt);

        return Ok(new
        {
            id = token.Id,
            name = token.Name,
            tokenPrefix = token.TokenPrefix,
            token = plainText, // Only returned once at creation time
            createdAt = token.CreatedAt,
            expiresAt = token.ExpiresAt,
            description = token.Description
        });
    }

    /// <summary>
    /// List SCIM tokens for a tenant (admin UI)
    /// </summary>
    [HttpGet("/api/scim/tokens")]
    [Authorize]
    public async Task<IActionResult> ListTokens([FromQuery] Guid tenantId)
    {
        if (tenantId == Guid.Empty)
            return BadRequest(new { error = "Tenant ID is required" });

        var tokens = await _scimService.GetTokensAsync(tenantId);
        return Ok(tokens.Select(t => new
        {
            id = t.Id,
            name = t.Name,
            tokenPrefix = t.TokenPrefix,
            createdAt = t.CreatedAt,
            expiresAt = t.ExpiresAt,
            lastUsedAt = t.LastUsedAt,
            isActive = t.IsActive,
            description = t.Description
        }));
    }

    /// <summary>
    /// Revoke a SCIM token (admin UI)
    /// </summary>
    [HttpDelete("/api/scim/tokens/{id:guid}")]
    [Authorize]
    public async Task<IActionResult> RevokeToken(Guid id)
    {
        var result = await _scimService.RevokeTokenAsync(id);
        if (!result)
            return NotFound(new { error = "Token not found" });

        return Ok(new { message = "Token revoked successfully" });
    }

    /// <summary>
    /// Get provisioning logs for a tenant (admin UI)
    /// </summary>
    [HttpGet("/api/scim/logs")]
    [Authorize]
    public async Task<IActionResult> GetProvisioningLogs([FromQuery] Guid tenantId, [FromQuery] int skip = 0, [FromQuery] int take = 50)
    {
        if (tenantId == Guid.Empty)
            return BadRequest(new { error = "Tenant ID is required" });

        var logs = await _scimService.GetProvisioningLogsAsync(tenantId, skip, take);
        return Ok(logs.Select(l => new
        {
            id = l.Id,
            operation = l.Operation,
            resourceType = l.ResourceType,
            externalId = l.ExternalId,
            resourceId = l.ResourceId,
            status = l.Status,
            details = l.Details,
            createdAt = l.CreatedAt
        }));
    }

    // ---- Private Helpers ----

    private async Task<(Guid? tenantId, IActionResult? error)> AuthenticateScimRequest()
    {
        var authHeader = Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return (null, Unauthorized(new ScimError
            {
                Status = 401,
                Detail = "Bearer token is required"
            }));
        }

        var bearerToken = authHeader["Bearer ".Length..].Trim();
        var result = await _scimService.ValidateTokenAsync(bearerToken);
        if (result == null)
        {
            return (null, Unauthorized(new ScimError
            {
                Status = 401,
                Detail = "Invalid or expired SCIM token"
            }));
        }

        return (result.Value.tenantId, null);
    }

    private static object GetUserSchema()
    {
        return new
        {
            id = "urn:ietf:params:scim:schemas:core:2.0:User",
            name = "User",
            description = "User Account",
            attributes = new object[]
            {
                new { name = "userName", type = "string", multiValued = false, required = true, mutability = "readWrite", returned = "default", uniqueness = "server" },
                new { name = "name", type = "complex", multiValued = false, required = false, mutability = "readWrite", returned = "default",
                    subAttributes = new object[]
                    {
                        new { name = "givenName", type = "string", multiValued = false, required = false, mutability = "readWrite", returned = "default" },
                        new { name = "familyName", type = "string", multiValued = false, required = false, mutability = "readWrite", returned = "default" },
                        new { name = "formatted", type = "string", multiValued = false, required = false, mutability = "readWrite", returned = "default" }
                    }
                },
                new { name = "displayName", type = "string", multiValued = false, required = false, mutability = "readWrite", returned = "default" },
                new { name = "emails", type = "complex", multiValued = true, required = false, mutability = "readWrite", returned = "default" },
                new { name = "phoneNumbers", type = "complex", multiValued = true, required = false, mutability = "readWrite", returned = "default" },
                new { name = "active", type = "boolean", multiValued = false, required = false, mutability = "readWrite", returned = "default" }
            },
            meta = new { resourceType = "Schema", location = "/scim/v2/Schemas/urn:ietf:params:scim:schemas:core:2.0:User" }
        };
    }

    private static object GetGroupSchema()
    {
        return new
        {
            id = "urn:ietf:params:scim:schemas:core:2.0:Group",
            name = "Group",
            description = "Group",
            attributes = new object[]
            {
                new { name = "displayName", type = "string", multiValued = false, required = true, mutability = "readWrite", returned = "default" },
                new { name = "members", type = "complex", multiValued = true, required = false, mutability = "readWrite", returned = "default",
                    subAttributes = new object[]
                    {
                        new { name = "value", type = "string", multiValued = false, required = true, mutability = "immutable", returned = "default" },
                        new { name = "display", type = "string", multiValued = false, required = false, mutability = "readOnly", returned = "default" },
                        new { name = "type", type = "string", multiValued = false, required = false, mutability = "immutable", returned = "default" }
                    }
                }
            },
            meta = new { resourceType = "Schema", location = "/scim/v2/Schemas/urn:ietf:params:scim:schemas:core:2.0:Group" }
        };
    }
}

public record CreateScimTokenRequest(
    Guid TenantId,
    string Name,
    string? Description,
    DateTime? ExpiresAt
);
