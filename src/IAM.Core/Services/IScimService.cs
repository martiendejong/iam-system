using IAM.Core.Entities;

namespace IAM.Core.Services;

/// <summary>
/// Service for SCIM 2.0 (RFC 7644) user and group provisioning operations.
/// Handles mapping between SCIM schema and internal IAM entities.
/// </summary>
public interface IScimService
{
    // User operations
    Task<ScimUserResource> CreateUserAsync(Guid tenantId, ScimUserResource scimUser, CancellationToken ct = default);
    Task<ScimUserResource?> GetUserAsync(Guid tenantId, Guid userId, CancellationToken ct = default);
    Task<ScimUserResource> ReplaceUserAsync(Guid tenantId, Guid userId, ScimUserResource scimUser, CancellationToken ct = default);
    Task<ScimUserResource> PatchUserAsync(Guid tenantId, Guid userId, ScimPatchRequest patchRequest, CancellationToken ct = default);
    Task<bool> DeleteUserAsync(Guid tenantId, Guid userId, CancellationToken ct = default);
    Task<ScimListResponse<ScimUserResource>> ListUsersAsync(Guid tenantId, ScimQueryOptions options, CancellationToken ct = default);

    // Group operations
    Task<ScimGroupResource> CreateGroupAsync(Guid tenantId, ScimGroupResource scimGroup, CancellationToken ct = default);
    Task<ScimGroupResource?> GetGroupAsync(Guid tenantId, Guid groupId, CancellationToken ct = default);
    Task<ScimGroupResource> ReplaceGroupAsync(Guid tenantId, Guid groupId, ScimGroupResource scimGroup, CancellationToken ct = default);
    Task<ScimGroupResource> PatchGroupAsync(Guid tenantId, Guid groupId, ScimPatchRequest patchRequest, CancellationToken ct = default);
    Task<bool> DeleteGroupAsync(Guid tenantId, Guid groupId, CancellationToken ct = default);
    Task<ScimListResponse<ScimGroupResource>> ListGroupsAsync(Guid tenantId, ScimQueryOptions options, CancellationToken ct = default);

    // Token management
    Task<(ScimToken token, string plainTextValue)> CreateTokenAsync(Guid tenantId, string name, string? description, DateTime? expiresAt, CancellationToken ct = default);
    Task<List<ScimToken>> GetTokensAsync(Guid tenantId, CancellationToken ct = default);
    Task<bool> RevokeTokenAsync(Guid tokenId, CancellationToken ct = default);
    Task<(Guid tenantId, ScimToken token)?> ValidateTokenAsync(string bearerToken, CancellationToken ct = default);

    // Provisioning log
    Task<List<ScimProvisioningLog>> GetProvisioningLogsAsync(Guid tenantId, int skip = 0, int take = 50, CancellationToken ct = default);
}

// ---- SCIM DTO models ----

public class ScimUserResource
{
    public string[] Schemas { get; set; } = new[] { "urn:ietf:params:scim:schemas:core:2.0:User" };
    public string? Id { get; set; }
    public string? ExternalId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public ScimName? Name { get; set; }
    public string? DisplayName { get; set; }
    public List<ScimEmail>? Emails { get; set; }
    public List<ScimPhoneNumber>? PhoneNumbers { get; set; }
    public bool Active { get; set; } = true;
    public ScimMeta? Meta { get; set; }
}

public class ScimName
{
    public string? GivenName { get; set; }
    public string? FamilyName { get; set; }
    public string? Formatted { get; set; }
}

public class ScimEmail
{
    public string Value { get; set; } = string.Empty;
    public string? Type { get; set; }
    public bool Primary { get; set; }
}

public class ScimPhoneNumber
{
    public string Value { get; set; } = string.Empty;
    public string? Type { get; set; }
}

public class ScimGroupResource
{
    public string[] Schemas { get; set; } = new[] { "urn:ietf:params:scim:schemas:core:2.0:Group" };
    public string? Id { get; set; }
    public string? ExternalId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public List<ScimMember>? Members { get; set; }
    public ScimMeta? Meta { get; set; }
}

public class ScimMember
{
    public string Value { get; set; } = string.Empty;  // User ID
    public string? Display { get; set; }
    public string? Type { get; set; }
    public string? Ref { get; set; }  // $ref in JSON, renamed here
}

public class ScimMeta
{
    public string? ResourceType { get; set; }
    public DateTime? Created { get; set; }
    public DateTime? LastModified { get; set; }
    public string? Location { get; set; }
    public string? Version { get; set; }
}

public class ScimPatchRequest
{
    public string[] Schemas { get; set; } = new[] { "urn:ietf:params:scim:api:messages:2.0:PatchOp" };
    public List<ScimPatchOperation> Operations { get; set; } = new();
}

public class ScimPatchOperation
{
    public string Op { get; set; } = string.Empty;  // add, replace, remove
    public string? Path { get; set; }
    public object? Value { get; set; }
}

public class ScimListResponse<T>
{
    public string[] Schemas { get; set; } = new[] { "urn:ietf:params:scim:api:messages:2.0:ListResponse" };
    public int TotalResults { get; set; }
    public int StartIndex { get; set; } = 1;
    public int ItemsPerPage { get; set; }
    public List<T> Resources { get; set; } = new();
}

public class ScimQueryOptions
{
    public string? Filter { get; set; }
    public int StartIndex { get; set; } = 1;
    public int Count { get; set; } = 100;
    public string? SortBy { get; set; }
    public string? SortOrder { get; set; }
    public string? Attributes { get; set; }
    public string? ExcludedAttributes { get; set; }
}

public class ScimError
{
    public string[] Schemas { get; set; } = new[] { "urn:ietf:params:scim:api:messages:2.0:Error" };
    public string? Detail { get; set; }
    public int Status { get; set; }
    public string? ScimType { get; set; }
}
