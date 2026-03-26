using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Group = IAM.Core.Entities.Group;

namespace IAM.Infrastructure.Services;

public class ScimService : IScimService
{
    private readonly IAMDbContext _context;

    public ScimService(IAMDbContext context)
    {
        _context = context;
    }

    // ---- User Operations ----

    public async Task<ScimUserResource> CreateUserAsync(Guid tenantId, ScimUserResource scimUser, CancellationToken ct = default)
    {
        var primaryEmail = scimUser.Emails?.FirstOrDefault(e => e.Primary)?.Value
            ?? scimUser.Emails?.FirstOrDefault()?.Value
            ?? scimUser.UserName;

        // Check if user with this email already exists
        var existingUser = await _context.Users.FirstOrDefaultAsync(u => u.Email == primaryEmail, ct);
        if (existingUser != null)
        {
            throw new InvalidOperationException($"User with email '{primaryEmail}' already exists");
        }

        var user = new User
        {
            Email = primaryEmail,
            FirstName = scimUser.Name?.GivenName ?? string.Empty,
            LastName = scimUser.Name?.FamilyName ?? string.Empty,
            PhoneNumber = scimUser.PhoneNumbers?.FirstOrDefault()?.Value,
            IsActive = scimUser.Active,
            PasswordHash = GenerateRandomPasswordHash(), // SCIM-provisioned users use SSO, not passwords
            EmailConfirmed = true, // Trust the identity provider
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.Users.Add(user);

        await LogProvisioningAsync(tenantId, "Create", "User", scimUser.ExternalId, user.Id, "Success", null, ct);
        await _context.SaveChangesAsync(ct);

        return MapUserToScimResource(user);
    }

    public async Task<ScimUserResource?> GetUserAsync(Guid tenantId, Guid userId, CancellationToken ct = default)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user == null)
            return null;

        return MapUserToScimResource(user);
    }

    public async Task<ScimUserResource> ReplaceUserAsync(Guid tenantId, Guid userId, ScimUserResource scimUser, CancellationToken ct = default)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user == null)
            throw new InvalidOperationException("User not found");

        var primaryEmail = scimUser.Emails?.FirstOrDefault(e => e.Primary)?.Value
            ?? scimUser.Emails?.FirstOrDefault()?.Value
            ?? scimUser.UserName;

        user.Email = primaryEmail;
        user.FirstName = scimUser.Name?.GivenName ?? string.Empty;
        user.LastName = scimUser.Name?.FamilyName ?? string.Empty;
        user.PhoneNumber = scimUser.PhoneNumbers?.FirstOrDefault()?.Value;
        user.IsActive = scimUser.Active;
        user.UpdatedAt = DateTime.UtcNow;

        await LogProvisioningAsync(tenantId, "Update", "User", scimUser.ExternalId, user.Id, "Success", null, ct);
        await _context.SaveChangesAsync(ct);

        return MapUserToScimResource(user);
    }

    public async Task<ScimUserResource> PatchUserAsync(Guid tenantId, Guid userId, ScimPatchRequest patchRequest, CancellationToken ct = default)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user == null)
            throw new InvalidOperationException("User not found");

        foreach (var op in patchRequest.Operations)
        {
            ApplyUserPatchOperation(user, op);
        }

        user.UpdatedAt = DateTime.UtcNow;

        await LogProvisioningAsync(tenantId, "Update", "User", null, user.Id, "Success", $"PATCH: {patchRequest.Operations.Count} operations", ct);
        await _context.SaveChangesAsync(ct);

        return MapUserToScimResource(user);
    }

    public async Task<bool> DeleteUserAsync(Guid tenantId, Guid userId, CancellationToken ct = default)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user == null)
            return false;

        // Soft delete: deactivate the user rather than removing data
        user.IsActive = false;
        user.UpdatedAt = DateTime.UtcNow;

        await LogProvisioningAsync(tenantId, "Delete", "User", null, user.Id, "Success", "Soft delete (deactivated)", ct);
        await _context.SaveChangesAsync(ct);

        return true;
    }

    public async Task<ScimListResponse<ScimUserResource>> ListUsersAsync(Guid tenantId, ScimQueryOptions options, CancellationToken ct = default)
    {
        var query = _context.Users.AsQueryable();

        // Apply SCIM filter
        if (!string.IsNullOrWhiteSpace(options.Filter))
        {
            query = ApplyUserFilter(query, options.Filter);
        }

        var totalResults = await query.CountAsync(ct);

        // Apply sorting
        query = options.SortBy?.ToLowerInvariant() switch
        {
            "username" or "emails.value" => options.SortOrder?.ToLowerInvariant() == "descending"
                ? query.OrderByDescending(u => u.Email)
                : query.OrderBy(u => u.Email),
            "name.familyname" => options.SortOrder?.ToLowerInvariant() == "descending"
                ? query.OrderByDescending(u => u.LastName)
                : query.OrderBy(u => u.LastName),
            "name.givenname" => options.SortOrder?.ToLowerInvariant() == "descending"
                ? query.OrderByDescending(u => u.FirstName)
                : query.OrderBy(u => u.FirstName),
            _ => query.OrderBy(u => u.Email)
        };

        // Apply pagination (SCIM uses 1-based indexing)
        var startIndex = Math.Max(1, options.StartIndex);
        var count = Math.Clamp(options.Count, 1, 1000);

        var users = await query
            .Skip(startIndex - 1)
            .Take(count)
            .ToListAsync(ct);

        return new ScimListResponse<ScimUserResource>
        {
            TotalResults = totalResults,
            StartIndex = startIndex,
            ItemsPerPage = users.Count,
            Resources = users.Select(MapUserToScimResource).ToList()
        };
    }

    // ---- Group Operations ----

    public async Task<ScimGroupResource> CreateGroupAsync(Guid tenantId, ScimGroupResource scimGroup, CancellationToken ct = default)
    {
        var group = new Group
        {
            Name = scimGroup.DisplayName,
            TenantId = tenantId,
            GroupType = "scim",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.Groups.Add(group);
        await _context.SaveChangesAsync(ct);

        // Add members if provided
        if (scimGroup.Members?.Any() == true)
        {
            foreach (var member in scimGroup.Members)
            {
                if (Guid.TryParse(member.Value, out var memberId))
                {
                    var userExists = await _context.Users.AnyAsync(u => u.Id == memberId, ct);
                    if (userExists)
                    {
                        _context.GroupMemberships.Add(new GroupMembership
                        {
                            GroupId = group.Id,
                            UserId = memberId,
                            Role = "member"
                        });
                    }
                }
            }
            await _context.SaveChangesAsync(ct);
        }

        await LogProvisioningAsync(tenantId, "Create", "Group", scimGroup.ExternalId, group.Id, "Success", null, ct);
        await _context.SaveChangesAsync(ct);

        return await BuildScimGroupResource(group, ct);
    }

    public async Task<ScimGroupResource?> GetGroupAsync(Guid tenantId, Guid groupId, CancellationToken ct = default)
    {
        var group = await _context.Groups
            .Include(g => g.Members.Where(m => m.IsActive))
                .ThenInclude(m => m.User)
            .FirstOrDefaultAsync(g => g.Id == groupId && g.TenantId == tenantId, ct);

        if (group == null)
            return null;

        return BuildScimGroupResourceFromLoaded(group);
    }

    public async Task<ScimGroupResource> ReplaceGroupAsync(Guid tenantId, Guid groupId, ScimGroupResource scimGroup, CancellationToken ct = default)
    {
        var group = await _context.Groups
            .Include(g => g.Members)
            .FirstOrDefaultAsync(g => g.Id == groupId && g.TenantId == tenantId, ct);

        if (group == null)
            throw new InvalidOperationException("Group not found");

        group.Name = scimGroup.DisplayName;
        group.UpdatedAt = DateTime.UtcNow;

        // Replace all members
        _context.GroupMemberships.RemoveRange(group.Members);

        if (scimGroup.Members?.Any() == true)
        {
            foreach (var member in scimGroup.Members)
            {
                if (Guid.TryParse(member.Value, out var memberId))
                {
                    var userExists = await _context.Users.AnyAsync(u => u.Id == memberId, ct);
                    if (userExists)
                    {
                        _context.GroupMemberships.Add(new GroupMembership
                        {
                            GroupId = group.Id,
                            UserId = memberId,
                            Role = "member"
                        });
                    }
                }
            }
        }

        await LogProvisioningAsync(tenantId, "Update", "Group", scimGroup.ExternalId, group.Id, "Success", null, ct);
        await _context.SaveChangesAsync(ct);

        return await BuildScimGroupResource(group, ct);
    }

    public async Task<ScimGroupResource> PatchGroupAsync(Guid tenantId, Guid groupId, ScimPatchRequest patchRequest, CancellationToken ct = default)
    {
        var group = await _context.Groups
            .Include(g => g.Members)
            .FirstOrDefaultAsync(g => g.Id == groupId && g.TenantId == tenantId, ct);

        if (group == null)
            throw new InvalidOperationException("Group not found");

        foreach (var op in patchRequest.Operations)
        {
            await ApplyGroupPatchOperationAsync(group, op, ct);
        }

        group.UpdatedAt = DateTime.UtcNow;

        await LogProvisioningAsync(tenantId, "Update", "Group", null, group.Id, "Success", $"PATCH: {patchRequest.Operations.Count} operations", ct);
        await _context.SaveChangesAsync(ct);

        return await BuildScimGroupResource(group, ct);
    }

    public async Task<bool> DeleteGroupAsync(Guid tenantId, Guid groupId, CancellationToken ct = default)
    {
        var group = await _context.Groups
            .Include(g => g.Members)
            .Include(g => g.GroupRoles)
            .FirstOrDefaultAsync(g => g.Id == groupId && g.TenantId == tenantId, ct);

        if (group == null)
            return false;

        _context.GroupMemberships.RemoveRange(group.Members);
        _context.GroupRoles.RemoveRange(group.GroupRoles);
        _context.Groups.Remove(group);

        await LogProvisioningAsync(tenantId, "Delete", "Group", null, group.Id, "Success", null, ct);
        await _context.SaveChangesAsync(ct);

        return true;
    }

    public async Task<ScimListResponse<ScimGroupResource>> ListGroupsAsync(Guid tenantId, ScimQueryOptions options, CancellationToken ct = default)
    {
        var query = _context.Groups
            .Include(g => g.Members.Where(m => m.IsActive))
                .ThenInclude(m => m.User)
            .Where(g => g.TenantId == tenantId && g.IsActive);

        // Apply SCIM filter
        if (!string.IsNullOrWhiteSpace(options.Filter))
        {
            query = ApplyGroupFilter(query, options.Filter);
        }

        var totalResults = await query.CountAsync(ct);

        // Apply sorting
        query = options.SortBy?.ToLowerInvariant() switch
        {
            "displayname" => options.SortOrder?.ToLowerInvariant() == "descending"
                ? query.OrderByDescending(g => g.Name)
                : query.OrderBy(g => g.Name),
            _ => query.OrderBy(g => g.Name)
        };

        // Apply pagination (SCIM uses 1-based indexing)
        var startIndex = Math.Max(1, options.StartIndex);
        var count = Math.Clamp(options.Count, 1, 1000);

        var groups = await query
            .Skip(startIndex - 1)
            .Take(count)
            .ToListAsync(ct);

        return new ScimListResponse<ScimGroupResource>
        {
            TotalResults = totalResults,
            StartIndex = startIndex,
            ItemsPerPage = groups.Count,
            Resources = groups.Select(BuildScimGroupResourceFromLoaded).ToList()
        };
    }

    // ---- Token Management ----

    public async Task<(ScimToken token, string plainTextValue)> CreateTokenAsync(Guid tenantId, string name, string? description, DateTime? expiresAt, CancellationToken ct = default)
    {
        var plainText = $"scim_{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))}";
        var hash = ComputeHash(plainText);

        var token = new ScimToken
        {
            TenantId = tenantId,
            Name = name,
            TokenHash = hash,
            TokenPrefix = plainText[..Math.Min(12, plainText.Length)],
            Description = description,
            ExpiresAt = expiresAt,
            IsActive = true
        };

        _context.ScimTokens.Add(token);
        await _context.SaveChangesAsync(ct);

        return (token, plainText);
    }

    public async Task<List<ScimToken>> GetTokensAsync(Guid tenantId, CancellationToken ct = default)
    {
        return await _context.ScimTokens
            .Where(t => t.TenantId == tenantId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<bool> RevokeTokenAsync(Guid tokenId, CancellationToken ct = default)
    {
        var token = await _context.ScimTokens.FindAsync(new object[] { tokenId }, ct);
        if (token == null)
            return false;

        token.IsActive = false;
        await _context.SaveChangesAsync(ct);
        return true;
    }

    public async Task<(Guid tenantId, ScimToken token)?> ValidateTokenAsync(string bearerToken, CancellationToken ct = default)
    {
        var hash = ComputeHash(bearerToken);

        var token = await _context.ScimTokens
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.IsActive, ct);

        if (token == null)
            return null;

        if (token.ExpiresAt.HasValue && token.ExpiresAt.Value < DateTime.UtcNow)
            return null;

        // Update last used timestamp
        token.LastUsedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        return (token.TenantId, token);
    }

    // ---- Provisioning Logs ----

    public async Task<List<ScimProvisioningLog>> GetProvisioningLogsAsync(Guid tenantId, int skip = 0, int take = 50, CancellationToken ct = default)
    {
        return await _context.ScimProvisioningLogs
            .Where(l => l.TenantId == tenantId)
            .OrderByDescending(l => l.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);
    }

    // ---- Private Helpers ----

    private ScimUserResource MapUserToScimResource(User user)
    {
        return new ScimUserResource
        {
            Schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:User" },
            Id = user.Id.ToString(),
            UserName = user.Email,
            Name = new ScimName
            {
                GivenName = user.FirstName,
                FamilyName = user.LastName,
                Formatted = $"{user.FirstName} {user.LastName}".Trim()
            },
            DisplayName = $"{user.FirstName} {user.LastName}".Trim(),
            Emails = new List<ScimEmail>
            {
                new ScimEmail { Value = user.Email, Type = "work", Primary = true }
            },
            PhoneNumbers = !string.IsNullOrWhiteSpace(user.PhoneNumber)
                ? new List<ScimPhoneNumber> { new ScimPhoneNumber { Value = user.PhoneNumber, Type = "work" } }
                : null,
            Active = user.IsActive,
            Meta = new ScimMeta
            {
                ResourceType = "User",
                Created = user.CreatedAt,
                LastModified = user.UpdatedAt,
                Location = $"/scim/v2/Users/{user.Id}"
            }
        };
    }

    private ScimGroupResource BuildScimGroupResourceFromLoaded(Group group)
    {
        return new ScimGroupResource
        {
            Schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:Group" },
            Id = group.Id.ToString(),
            DisplayName = group.Name,
            Members = group.Members
                .Where(m => m.IsActive)
                .Select(m => new ScimMember
                {
                    Value = m.UserId.ToString(),
                    Display = m.User != null ? $"{m.User.FirstName} {m.User.LastName}".Trim() : null,
                    Type = "User",
                    Ref = $"/scim/v2/Users/{m.UserId}"
                })
                .ToList(),
            Meta = new ScimMeta
            {
                ResourceType = "Group",
                Created = group.CreatedAt,
                LastModified = group.UpdatedAt,
                Location = $"/scim/v2/Groups/{group.Id}"
            }
        };
    }

    private async Task<ScimGroupResource> BuildScimGroupResource(Group group, CancellationToken ct)
    {
        var fullGroup = await _context.Groups
            .Include(g => g.Members.Where(m => m.IsActive))
                .ThenInclude(m => m.User)
            .FirstOrDefaultAsync(g => g.Id == group.Id, ct);

        return BuildScimGroupResourceFromLoaded(fullGroup ?? group);
    }

    private void ApplyUserPatchOperation(User user, ScimPatchOperation op)
    {
        var path = op.Path?.ToLowerInvariant()?.Trim();
        var valueStr = op.Value?.ToString() ?? string.Empty;

        switch (op.Op.ToLowerInvariant())
        {
            case "replace":
                switch (path)
                {
                    case "active":
                        user.IsActive = bool.TryParse(valueStr, out var active) && active;
                        break;
                    case "username":
                        user.Email = valueStr;
                        break;
                    case "name.givenname":
                        user.FirstName = valueStr;
                        break;
                    case "name.familyname":
                        user.LastName = valueStr;
                        break;
                    case "displayname":
                        // Split display name into first/last
                        var parts = valueStr.Split(' ', 2);
                        user.FirstName = parts[0];
                        user.LastName = parts.Length > 1 ? parts[1] : string.Empty;
                        break;
                    case "emails[type eq \"work\"].value":
                    case "emails":
                        // Try to parse the value as JSON array for emails
                        if (TryParseEmails(op.Value, out var email))
                        {
                            user.Email = email;
                        }
                        else
                        {
                            user.Email = valueStr;
                        }
                        break;
                    case "phonenumbers[type eq \"work\"].value":
                    case "phonenumbers":
                        user.PhoneNumber = valueStr;
                        break;
                }
                break;
            case "add":
                // Same behavior as replace for single-valued attributes
                ApplyUserPatchOperation(user, new ScimPatchOperation { Op = "replace", Path = op.Path, Value = op.Value });
                break;
            case "remove":
                switch (path)
                {
                    case "phonenumbers":
                        user.PhoneNumber = null;
                        break;
                }
                break;
        }
    }

    private async Task ApplyGroupPatchOperationAsync(Group group, ScimPatchOperation op, CancellationToken ct)
    {
        var path = op.Path?.ToLowerInvariant()?.Trim();
        var valueStr = op.Value?.ToString() ?? string.Empty;

        switch (op.Op.ToLowerInvariant())
        {
            case "replace":
                if (path == "displayname")
                {
                    group.Name = valueStr;
                }
                break;

            case "add":
                if (path == "members")
                {
                    var members = TryParseMembers(op.Value);
                    foreach (var memberId in members)
                    {
                        var exists = await _context.GroupMemberships
                            .AnyAsync(m => m.GroupId == group.Id && m.UserId == memberId, ct);
                        if (!exists)
                        {
                            _context.GroupMemberships.Add(new GroupMembership
                            {
                                GroupId = group.Id,
                                UserId = memberId,
                                Role = "member"
                            });
                        }
                    }
                }
                else if (path == "displayname")
                {
                    group.Name = valueStr;
                }
                break;

            case "remove":
                if (path != null && path.StartsWith("members"))
                {
                    // Handle "members[value eq \"<id>\"]"
                    var match = Regex.Match(path, @"members\[value\s+eq\s+""([^""]+)""\]", RegexOptions.IgnoreCase);
                    if (match.Success && Guid.TryParse(match.Groups[1].Value, out var removeId))
                    {
                        var membership = await _context.GroupMemberships
                            .FirstOrDefaultAsync(m => m.GroupId == group.Id && m.UserId == removeId, ct);
                        if (membership != null)
                        {
                            _context.GroupMemberships.Remove(membership);
                        }
                    }
                    else if (op.Value != null)
                    {
                        var memberIds = TryParseMembers(op.Value);
                        foreach (var memberId in memberIds)
                        {
                            var membership = await _context.GroupMemberships
                                .FirstOrDefaultAsync(m => m.GroupId == group.Id && m.UserId == memberId, ct);
                            if (membership != null)
                            {
                                _context.GroupMemberships.Remove(membership);
                            }
                        }
                    }
                }
                break;
        }
    }

    private IQueryable<User> ApplyUserFilter(IQueryable<User> query, string filter)
    {
        // Parse SCIM filter expressions: attribute operator value
        // Supported operators: eq, co, sw, pr, gt, lt
        var match = Regex.Match(filter.Trim(),
            @"^(\w+(?:\.\w+)?)\s+(eq|co|sw|pr|gt|lt|ge|le)\s*(?:""([^""]*)""|(\S+))?$",
            RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            // Try compound filter with "and"
            var andParts = Regex.Split(filter, @"\s+and\s+", RegexOptions.IgnoreCase);
            foreach (var part in andParts)
            {
                query = ApplyUserFilter(query, part.Trim());
            }
            return query;
        }

        var attribute = match.Groups[1].Value.ToLowerInvariant();
        var op = match.Groups[2].Value.ToLowerInvariant();
        var value = match.Groups[3].Success ? match.Groups[3].Value : match.Groups[4].Value;

        return (attribute, op) switch
        {
            ("username", "eq") => query.Where(u => u.Email == value),
            ("username", "co") => query.Where(u => u.Email.Contains(value)),
            ("username", "sw") => query.Where(u => u.Email.StartsWith(value)),
            ("username", "pr") => query.Where(u => u.Email != null && u.Email != ""),

            ("emails.value", "eq") => query.Where(u => u.Email == value),
            ("emails.value", "co") => query.Where(u => u.Email.Contains(value)),
            ("emails.value", "sw") => query.Where(u => u.Email.StartsWith(value)),

            ("name.familyname", "eq") => query.Where(u => u.LastName == value),
            ("name.familyname", "co") => query.Where(u => u.LastName.Contains(value)),
            ("name.familyname", "sw") => query.Where(u => u.LastName.StartsWith(value)),

            ("name.givenname", "eq") => query.Where(u => u.FirstName == value),
            ("name.givenname", "co") => query.Where(u => u.FirstName.Contains(value)),
            ("name.givenname", "sw") => query.Where(u => u.FirstName.StartsWith(value)),

            ("displayname", "eq") => query.Where(u => (u.FirstName + " " + u.LastName) == value),
            ("displayname", "co") => query.Where(u => (u.FirstName + " " + u.LastName).Contains(value)),
            ("displayname", "sw") => query.Where(u => (u.FirstName + " " + u.LastName).StartsWith(value)),

            ("active", "eq") => bool.TryParse(value, out var isActive)
                ? query.Where(u => u.IsActive == isActive)
                : query,

            ("externalid", "eq") => query, // ExternalId not stored on User, return all
            ("id", "eq") => Guid.TryParse(value, out var id) ? query.Where(u => u.Id == id) : query,

            ("meta.created", "gt") => DateTime.TryParse(value, out var gt) ? query.Where(u => u.CreatedAt > gt) : query,
            ("meta.created", "lt") => DateTime.TryParse(value, out var lt) ? query.Where(u => u.CreatedAt < lt) : query,
            ("meta.created", "ge") => DateTime.TryParse(value, out var ge) ? query.Where(u => u.CreatedAt >= ge) : query,
            ("meta.created", "le") => DateTime.TryParse(value, out var le) ? query.Where(u => u.CreatedAt <= le) : query,

            _ => query // Unknown filter, return unfiltered
        };
    }

    private IQueryable<Group> ApplyGroupFilter(IQueryable<Group> query, string filter)
    {
        var match = Regex.Match(filter.Trim(),
            @"^(\w+(?:\.\w+)?)\s+(eq|co|sw|pr)\s*(?:""([^""]*)""|(\S+))?$",
            RegexOptions.IgnoreCase);

        if (!match.Success)
            return query;

        var attribute = match.Groups[1].Value.ToLowerInvariant();
        var op = match.Groups[2].Value.ToLowerInvariant();
        var value = match.Groups[3].Success ? match.Groups[3].Value : match.Groups[4].Value;

        return (attribute, op) switch
        {
            ("displayname", "eq") => query.Where(g => g.Name == value),
            ("displayname", "co") => query.Where(g => g.Name.Contains(value)),
            ("displayname", "sw") => query.Where(g => g.Name.StartsWith(value)),
            ("id", "eq") => Guid.TryParse(value, out var id) ? query.Where(g => g.Id == id) : query,
            _ => query
        };
    }

    private async Task LogProvisioningAsync(Guid tenantId, string operation, string resourceType, string? externalId, Guid? resourceId, string status, string? details, CancellationToken ct)
    {
        _context.ScimProvisioningLogs.Add(new ScimProvisioningLog
        {
            TenantId = tenantId,
            Operation = operation,
            ResourceType = resourceType,
            ExternalId = externalId,
            ResourceId = resourceId,
            Status = status,
            Details = details,
            CreatedAt = DateTime.UtcNow
        });
    }

    private static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string GenerateRandomPasswordHash()
    {
        // Generate a random password hash for SCIM-provisioned users (they use SSO, not direct login)
        var randomBytes = RandomNumberGenerator.GetBytes(32);
        return ComputeHash(Convert.ToBase64String(randomBytes));
    }

    private static bool TryParseEmails(object? value, out string email)
    {
        email = string.Empty;
        if (value == null) return false;

        try
        {
            var json = value is JsonElement je ? je.GetRawText() : value.ToString();
            if (string.IsNullOrEmpty(json)) return false;

            var emails = JsonSerializer.Deserialize<List<ScimEmail>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var primary = emails?.FirstOrDefault(e => e.Primary) ?? emails?.FirstOrDefault();
            if (primary != null)
            {
                email = primary.Value;
                return true;
            }
        }
        catch
        {
            // Not a valid email array
        }

        return false;
    }

    private static List<Guid> TryParseMembers(object? value)
    {
        var result = new List<Guid>();
        if (value == null) return result;

        try
        {
            var json = value is JsonElement je ? je.GetRawText() : value.ToString();
            if (string.IsNullOrEmpty(json)) return result;

            var members = JsonSerializer.Deserialize<List<ScimMember>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (members != null)
            {
                foreach (var m in members)
                {
                    if (Guid.TryParse(m.Value, out var id))
                        result.Add(id);
                }
            }
        }
        catch
        {
            // Try single member
            try
            {
                var json = value is JsonElement je2 ? je2.GetRawText() : value.ToString();
                var member = JsonSerializer.Deserialize<ScimMember>(json!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (member != null && Guid.TryParse(member.Value, out var id))
                    result.Add(id);
            }
            catch
            {
                // Not parseable
            }
        }

        return result;
    }
}
