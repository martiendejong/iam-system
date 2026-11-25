using System.Security.Claims;
using System.Text.Json;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Jengo.Mcp;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace IAM.API.Mcp.OAuth;

/// <summary>
/// The read-only tool set offered to OAuth-connected assistants (ChatGPT, Claude.ai). It answers only principals that came
/// in through <see cref="IamMcpOAuthTokenValidator"/>; the API-key tool set (docs/mcp-server.md, with its invitation and
/// role tools) answers only API-key principals, so neither can reach the other's tools.
///
/// There is deliberately no tool that creates, changes, deletes, resets or revokes anything, and no tool that can return a
/// password hash, token, API key, client secret, session token, device fingerprint or two-factor secret: those fields are
/// never selected, so a future bug in a filter cannot leak them. Everything except <c>whoami</c> and the caller's own
/// sessions requires an IAM administrator (SuperAdmin or SystemAdmin), checked against the database on every call.
/// </summary>
public sealed class IamMcpOAuthToolProvider : IMcpToolProvider
{
    private const int MaxRows = 100;
    private static readonly string[] AdminRoles = { "SuperAdmin", "SystemAdmin" };
    private static readonly JsonSerializerOptions ResultJson = new() { WriteIndented = false };

    private static readonly HashSet<string> ToolNames = new(StringComparer.Ordinal)
    {
        "whoami", "list_users", "get_user", "list_clients", "list_sessions", "list_audit_events",
    };

    private readonly IAMDbContext _db;
    private readonly IOpenIddictApplicationManager _applications;
    private readonly ILogger<IamMcpOAuthToolProvider> _logger;

    public IamMcpOAuthToolProvider(IAMDbContext db, IOpenIddictApplicationManager applications, ILogger<IamMcpOAuthToolProvider> logger)
    {
        _db = db;
        _applications = applications;
        _logger = logger;
    }

    public static bool IsOAuthPrincipal(ClaimsPrincipal? principal) =>
        principal?.FindFirst(McpOAuth.OAuthPrincipalClaim)?.Value == "true";

    public ValueTask<IReadOnlyList<Tool>> ListToolsAsync(McpToolContext context, CancellationToken cancellationToken)
    {
        if (!IsOAuthPrincipal(context.User))
            return ValueTask.FromResult<IReadOnlyList<Tool>>(Array.Empty<Tool>());

        IReadOnlyList<Tool> tools = new List<Tool>
        {
            Tool("whoami",
                "Who is the signed-in IAM user behind this connection: id, name, email, account status, whether two-factor is on, and roles (with tenant). Read-only.",
                Schema("""{"type":"object","properties":{},"additionalProperties":false}""")),

            Tool("list_users",
                "ADMIN ONLY. List IAM users (max 100): id, name, email, active/locked/two-factor flags, last login, roles. Never returns password hashes, tokens or secrets. Read-only.",
                Schema("""{"type":"object","properties":{"search":{"type":"string","description":"Case-insensitive substring of email, first name or last name."},"tenantId":{"type":"string","description":"Only users holding a role in this tenant (GUID)."},"includeInactive":{"type":"boolean","description":"Include deactivated users (default false)."},"limit":{"type":"integer","description":"Max rows, 1-100 (default 50)."}},"additionalProperties":false}""")),

            Tool("get_user",
                "ADMIN ONLY. One IAM user by email or id, with role assignments (name, tenant, expiry). Never returns password hashes, tokens or secrets. Read-only.",
                Schema("""{"type":"object","properties":{"email":{"type":"string"},"id":{"type":"string","description":"User id (GUID)."}},"additionalProperties":false}""")),

            Tool("list_clients",
                "ADMIN ONLY. The OAuth applications registered in IAM: client id, name, public/confidential, redirect URIs, allowed grant types and scopes, PKCE requirement, and whether it was dynamically registered (MCP connectors). Never returns client secrets. Read-only.",
                Schema("""{"type":"object","properties":{"search":{"type":"string","description":"Substring of client id or display name."},"limit":{"type":"integer","description":"Max rows, 1-100 (default 100)."}},"additionalProperties":false}""")),

            Tool("list_sessions",
                "Active sign-in sessions (when, IP address, browser/device, expiry) and trusted devices (name, last used, expiry) of a user. Without arguments: your own. Admins can pass userId or email, or neither to get all users' active sessions plus the total. Never returns session tokens or device fingerprints. Read-only.",
                Schema("""{"type":"object","properties":{"userId":{"type":"string","description":"User id (GUID). Admin only for other users."},"email":{"type":"string","description":"User email. Admin only for other users."},"includeRevoked":{"type":"boolean","description":"Also show revoked and expired sessions (default false)."},"limit":{"type":"integer","description":"Max sessions, 1-100 (default 50)."}},"additionalProperties":false}""")),

            Tool("list_audit_events",
                "ADMIN ONLY. Recent audit and login events, newest first: time, action, resource, user, IP address. Does not include event detail payloads. Read-only.",
                Schema("""{"type":"object","properties":{"action":{"type":"string","description":"Substring of the action name, for example 'login'."},"userId":{"type":"string","description":"Only events of this user (GUID)."},"email":{"type":"string","description":"Only events of the user with this email."},"sinceHours":{"type":"integer","description":"Only events newer than this many hours (default 72, max 720)."},"limit":{"type":"integer","description":"Max rows, 1-100 (default 50)."}},"additionalProperties":false}""")),
        };
        return ValueTask.FromResult(tools);
    }

    public async ValueTask<CallToolResult?> TryCallToolAsync(
        McpToolContext context, string toolName,
        IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken cancellationToken)
    {
        if (!IsOAuthPrincipal(context.User) || !ToolNames.Contains(toolName)) return null;

        var subject = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(subject, out var userId)) return Error("Unauthorized.");

        var caller = await _db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .FirstOrDefaultAsync(cancellationToken);
        if (caller is null || !caller.IsActive) return Error("Unauthorized: the signed-in user is not an active IAM user.");

        var isAdmin = await IsAdminAsync(userId, cancellationToken);
        await TryAuditAsync(context, caller, toolName, cancellationToken);

        try
        {
            return toolName switch
            {
                "whoami" => await WhoAmIAsync(caller, isAdmin, context, cancellationToken),
                "list_users" => await RequireAdmin(isAdmin) ?? await ListUsersAsync(arguments, cancellationToken),
                "get_user" => await RequireAdmin(isAdmin) ?? await GetUserAsync(arguments, cancellationToken),
                "list_clients" => await RequireAdmin(isAdmin) ?? await ListClientsAsync(arguments, cancellationToken),
                "list_sessions" => await ListSessionsAsync(caller, isAdmin, arguments, cancellationToken),
                "list_audit_events" => await RequireAdmin(isAdmin) ?? await ListAuditEventsAsync(arguments, cancellationToken),
                _ => null,
            };
        }
        catch (ArgumentException ex)
        {
            return Error(ex.Message);
        }
    }

    private static Task<CallToolResult?> RequireAdmin(bool isAdmin) =>
        Task.FromResult<CallToolResult?>(isAdmin ? null : Error("Forbidden: this tool is only available to IAM administrators (SuperAdmin or SystemAdmin)."));

    private async Task<bool> IsAdminAsync(Guid userId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        return await _db.UserRoles.AsNoTracking()
            .AnyAsync(ur => ur.UserId == userId
                && AdminRoles.Contains(ur.Role.Name)
                && (ur.ExpiresAt == null || ur.ExpiresAt > now), ct);
    }

    // --- tools -----------------------------------------------------------------------------------------------------

    private async Task<CallToolResult> WhoAmIAsync(User caller, bool isAdmin, McpToolContext context, CancellationToken ct)
    {
        var roles = await RolesOfAsync(new[] { caller.Id }, ct);
        return Ok(new
        {
            id = caller.Id,
            email = caller.Email,
            firstName = caller.FirstName,
            lastName = caller.LastName,
            isActive = caller.IsActive,
            emailConfirmed = caller.EmailConfirmed,
            twoFactorEnabled = caller.TwoFactorEnabled,
            lastLoginAt = caller.LastLoginAt,
            isIamAdmin = isAdmin,
            roles = roles.GetValueOrDefault(caller.Id) ?? new List<object>(),
            connectedClientId = context.User.FindFirstValue(McpOAuth.ClientIdClaim),
            access = "read-only",
        });
    }

    private async Task<CallToolResult> ListUsersAsync(IReadOnlyDictionary<string, JsonElement> args, CancellationToken ct)
    {
        var limit = Limit(args, 50);
        var query = _db.Users.AsNoTracking().AsQueryable();
        if (!(OptionalBool(args, "includeInactive") ?? false)) query = query.Where(u => u.IsActive);
        if (OptionalString(args, "search") is { Length: > 0 } search)
        {
            var s = search.Trim().ToLower();
            query = query.Where(u => u.Email.ToLower().Contains(s) || u.FirstName.ToLower().Contains(s) || u.LastName.ToLower().Contains(s));
        }
        if (OptionalGuid(args, "tenantId") is { } tenantId)
            query = query.Where(u => u.UserRoles.Any(ur => ur.TenantId == tenantId));

        var users = await query.OrderBy(u => u.Email).Take(limit).ToListAsync(ct);
        var roles = await RolesOfAsync(users.Select(u => u.Id).ToList(), ct);
        return Ok(new
        {
            count = users.Count,
            users = users.Select(u => UserDto(u, roles.GetValueOrDefault(u.Id))),
        });
    }

    private async Task<CallToolResult> GetUserAsync(IReadOnlyDictionary<string, JsonElement> args, CancellationToken ct)
    {
        var email = OptionalString(args, "email");
        var id = OptionalGuid(args, "id");
        if (string.IsNullOrWhiteSpace(email) && id is null) return Error("Provide either 'email' or 'id'.");

        var query = _db.Users.AsNoTracking();
        var user = id is { } g
            ? await query.FirstOrDefaultAsync(u => u.Id == g, ct)
            : await query.FirstOrDefaultAsync(u => u.Email.ToLower() == email!.Trim().ToLower(), ct);
        if (user is null) return Error("User not found.");

        var roles = await RolesOfAsync(new[] { user.Id }, ct);
        return Ok(UserDto(user, roles.GetValueOrDefault(user.Id)));
    }

    private async Task<CallToolResult> ListClientsAsync(IReadOnlyDictionary<string, JsonElement> args, CancellationToken ct)
    {
        var limit = Limit(args, 100);
        var search = OptionalString(args, "search")?.Trim();
        var rows = new List<object>();
        await foreach (var application in _applications.ListAsync(null, null, ct))
        {
            var clientId = await _applications.GetClientIdAsync(application, ct);
            var displayName = await _applications.GetDisplayNameAsync(application, ct);
            if (!string.IsNullOrEmpty(search)
                && !(clientId?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                && !(displayName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
                continue;

            var permissions = await _applications.GetPermissionsAsync(application, ct);
            var requirements = await _applications.GetRequirementsAsync(application, ct);
            rows.Add(new
            {
                clientId,
                displayName,
                clientType = await _applications.GetClientTypeAsync(application, ct),
                redirectUris = await _applications.GetRedirectUrisAsync(application, ct),
                postLogoutRedirectUris = await _applications.GetPostLogoutRedirectUrisAsync(application, ct),
                grantTypes = permissions.Where(p => p.StartsWith(Permissions.Prefixes.GrantType, StringComparison.Ordinal))
                    .Select(p => p[Permissions.Prefixes.GrantType.Length..]),
                scopes = permissions.Where(p => p.StartsWith(Permissions.Prefixes.Scope, StringComparison.Ordinal))
                    .Select(p => p[Permissions.Prefixes.Scope.Length..]),
                requiresPkce = requirements.Contains(Requirements.Features.ProofKeyForCodeExchange),
                dynamicallyRegistered = await McpOAuth.IsDynamicClientAsync(_applications, application),
            });
            if (rows.Count >= limit) break;
        }
        return Ok(new { count = rows.Count, clients = rows });
    }

    private async Task<CallToolResult> ListSessionsAsync(User caller, bool isAdmin, IReadOnlyDictionary<string, JsonElement> args, CancellationToken ct)
    {
        var limit = Limit(args, 50);
        var includeRevoked = OptionalBool(args, "includeRevoked") ?? false;

        Guid? target = OptionalGuid(args, "userId");
        if (target is null && OptionalString(args, "email") is { Length: > 0 } email)
        {
            target = await _db.Users.AsNoTracking()
                .Where(u => u.Email.ToLower() == email.Trim().ToLower())
                .Select(u => (Guid?)u.Id)
                .FirstOrDefaultAsync(ct);
            if (target is null) return Error("User not found.");
        }

        if (!isAdmin)
        {
            // Non-admins see their own sessions only, never anyone else's.
            if (target is not null && target != caller.Id) return Error("Forbidden: only IAM administrators can see other users' sessions.");
            target = caller.Id;
        }

        var now = DateTime.UtcNow;
        var query = _db.RefreshTokens.AsNoTracking().AsQueryable();
        if (target is { } t) query = query.Where(rt => rt.UserId == t);
        if (!includeRevoked) query = query.Where(rt => rt.RevokedAt == null && rt.ExpiresAt > now);

        var total = await query.CountAsync(ct);
        // Explicit projection: TokenHash is never selected.
        var sessions = await query.OrderByDescending(rt => rt.CreatedAt).Take(limit)
            .Select(rt => new
            {
                id = rt.Id,
                userId = rt.UserId,
                userEmail = isAdmin ? rt.User.Email : null,
                createdAt = rt.CreatedAt,
                expiresAt = rt.ExpiresAt,
                revokedAt = rt.RevokedAt,
                ipAddress = rt.IpAddress,
                userAgent = rt.UserAgent,
                rememberMe = rt.RememberMe,
            })
            .ToListAsync(ct);

        object? devices = null;
        if (target is { } deviceUser)
        {
            // Explicit projection: DeviceFingerprint is never selected.
            devices = await _db.TrustedDevices.AsNoTracking()
                .Where(d => d.UserId == deviceUser && d.ExpiresAt > now)
                .OrderByDescending(d => d.LastUsedAt).Take(limit)
                .Select(d => new { id = d.Id, name = d.Name, trustScore = d.TrustScore, createdAt = d.CreatedAt, lastUsedAt = d.LastUsedAt, expiresAt = d.ExpiresAt })
                .ToListAsync(ct);
        }

        return Ok(new { totalMatching = total, returned = sessions.Count, sessions, trustedDevices = devices });
    }

    private async Task<CallToolResult> ListAuditEventsAsync(IReadOnlyDictionary<string, JsonElement> args, CancellationToken ct)
    {
        var limit = Limit(args, 50);
        var hours = Math.Clamp(OptionalInt(args, "sinceHours") ?? 72, 1, 720);
        var since = DateTime.UtcNow.AddHours(-hours);

        var query = _db.AuditLogs.AsNoTracking().Where(a => a.CreatedAt >= since);
        if (OptionalString(args, "action") is { Length: > 0 } action)
        {
            var a = action.Trim().ToLower();
            query = query.Where(x => x.Action.ToLower().Contains(a));
        }
        var userId = OptionalGuid(args, "userId");
        if (userId is null && OptionalString(args, "email") is { Length: > 0 } email)
        {
            userId = await _db.Users.AsNoTracking()
                .Where(u => u.Email.ToLower() == email.Trim().ToLower())
                .Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
            if (userId is null) return Error("User not found.");
        }
        if (userId is { } uid) query = query.Where(x => x.UserId == uid);

        // Explicit projection: the free-form Details column is never selected.
        var events = await query.OrderByDescending(x => x.CreatedAt).Take(limit)
            .Select(x => new
            {
                id = x.Id,
                at = x.CreatedAt,
                action = x.Action,
                resource = x.Resource,
                userId = x.UserId,
                userEmail = x.User != null ? x.User.Email : null,
                tenantId = x.TenantId,
                ipAddress = x.IpAddress,
                userAgent = x.UserAgent,
            })
            .ToListAsync(ct);
        return Ok(new { sinceHours = hours, count = events.Count, events });
    }

    // --- helpers ---------------------------------------------------------------------------------------------------

    /// <summary>Role names (with tenant and expiry) per user, active assignments only.</summary>
    private async Task<Dictionary<Guid, List<object>>> RolesOfAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var rows = await _db.UserRoles.AsNoTracking()
            .Where(ur => userIds.Contains(ur.UserId) && (ur.ExpiresAt == null || ur.ExpiresAt > now))
            .Select(ur => new { ur.UserId, name = ur.Role.Name, tenantId = ur.TenantId, expiresAt = ur.ExpiresAt })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.UserId).ToDictionary(g => g.Key, g => g.Select(r => (object)new { r.name, r.tenantId, r.expiresAt }).ToList());
    }

    /// <summary>Explicit field list: password hash, verification/reset tokens, two-factor secret and phone number are not in it.</summary>
    private static object UserDto(User u, List<object>? roles) => new
    {
        id = u.Id,
        email = u.Email,
        firstName = u.FirstName,
        lastName = u.LastName,
        isActive = u.IsActive,
        emailConfirmed = u.EmailConfirmed,
        isLockedOut = u.IsLockedOut && u.LockoutEnd > DateTime.UtcNow,
        twoFactorEnabled = u.TwoFactorEnabled,
        createdAt = u.CreatedAt,
        lastLoginAt = u.LastLoginAt,
        roles = roles ?? new List<object>(),
    };

    /// <summary>Every call is recorded, so an administrator can see what a connected assistant looked at. Never fails the call.</summary>
    private async Task TryAuditAsync(McpToolContext context, User caller, string toolName, CancellationToken ct)
    {
        AuditLog? row = null;
        try
        {
            // Details is a jsonb column in PostgreSQL: it must be valid JSON.
            row = new AuditLog
            {
                UserId = caller.Id,
                Action = "mcp.oauth.tool",
                Resource = $"mcp:{toolName}",
                Details = JsonSerializer.Serialize(new { client = context.User.FindFirstValue(McpOAuth.ClientIdClaim) }),
                IpAddress = context.HttpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent = context.HttpContext.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua[..Math.Min(ua.Length, 500)] : null,
            };
            _db.AuditLogs.Add(row);
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Do not leave the failed row tracked: it would be retried by the next SaveChanges of this request.
            if (row != null) _db.Entry(row).State = EntityState.Detached;
            _logger.LogWarning(ex, "Could not write the audit row for MCP tool {Tool}", toolName);
        }
    }

    private static int Limit(IReadOnlyDictionary<string, JsonElement> args, int fallback) =>
        Math.Clamp(OptionalInt(args, "limit") ?? fallback, 1, MaxRows);

    private static string? OptionalString(IReadOnlyDictionary<string, JsonElement> args, string name)
    {
        if (!args.TryGetValue(name, out var v) || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
    }

    private static Guid? OptionalGuid(IReadOnlyDictionary<string, JsonElement> args, string name)
    {
        var raw = OptionalString(args, name);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!Guid.TryParse(raw, out var guid)) throw new ArgumentException($"'{name}' must be a GUID.");
        return guid;
    }

    private static int? OptionalInt(IReadOnlyDictionary<string, JsonElement> args, string name)
    {
        if (!args.TryGetValue(name, out var v) || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        throw new ArgumentException($"'{name}' must be an integer.");
    }

    private static bool? OptionalBool(IReadOnlyDictionary<string, JsonElement> args, string name)
    {
        if (!args.TryGetValue(name, out var v) || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(v.GetString(), out var b) => b,
            _ => throw new ArgumentException($"'{name}' must be true or false."),
        };
    }

    private static CallToolResult Ok(object payload) => new()
    {
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload, ResultJson) }],
    };

    private static CallToolResult Error(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
    };

    private static Tool Tool(string name, string description, JsonElement schema) => new()
    {
        Name = name,
        Description = description,
        InputSchema = schema,
        Annotations = new ToolAnnotations { ReadOnlyHint = true, DestructiveHint = false, IdempotentHint = true, OpenWorldHint = false },
    };

    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
