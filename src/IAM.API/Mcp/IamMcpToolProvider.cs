using System.Security.Claims;
using System.Text.Json;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Jengo.Mcp;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;

namespace IAM.API.Mcp;

/// <summary>
/// The MCP tool set of the IAM system (task 5800), exposed through the shared Jengo MCP template
/// (<c>IMcpToolProvider</c>). Every call first resolves the authenticated key's subject to an
/// existing, active IAM user (the "acting user"); mutations run in that user's name
/// (invitations record <c>InvitedByUserId</c>, role grants record <c>GrantedBy</c>) and are
/// audit-logged with the acting subject, the tool name and the parameters.
///
/// POLICY — agent-safe onboarding only. The surface deliberately contains NO tool that creates a
/// user with a password, sets / changes / resets a password, or mints tokens for a user. Agents
/// onboard people via <c>send_invitation</c>: the invited person proves control of their mailbox
/// and chooses their own password on the accept page, so no credential ever flows through an
/// agent. <c>tests/IAM.API.Tests/Mcp/IamMcpToolProviderTests.cs</c> pins this exclusion list.
/// </summary>
public sealed class IamMcpToolProvider : IMcpToolProvider
{
    /// <summary>Role name whose assignment/invitation an agent may only perform when the acting
    /// user holds it themselves (same rule as InvitationService's bulk-invite SuperAdmin gate).</summary>
    private const string SuperAdminRole = "SuperAdmin";

    private readonly IAMDbContext _db;
    private readonly IInvitationService _invitations;
    private readonly ILogger<IamMcpToolProvider> _logger;
    private readonly string? _emailBaseUrl;

    public IamMcpToolProvider(
        IAMDbContext db,
        IInvitationService invitations,
        IConfiguration configuration,
        ILogger<IamMcpToolProvider> logger)
    {
        _db = db;
        _invitations = invitations;
        _logger = logger;
        _emailBaseUrl = configuration.GetValue<string>("Email:BaseUrl");
    }

    // --- tool catalogue (tools/list) ---

    public ValueTask<IReadOnlyList<Tool>> ListToolsAsync(McpToolContext context, CancellationToken cancellationToken)
    {
        IReadOnlyList<Tool> tools = new List<Tool>
        {
            Tool("list_users",
                "List IAM users (max 100 rows), optionally filtered by a search string matched against email, first name and last name. Read-only.",
                Schema("""{"type":"object","properties":{"search":{"type":"string","description":"Optional filter; case-insensitive substring match on email / first name / last name."}},"additionalProperties":false}"""),
                readOnly: true),

            Tool("get_user",
                "Get one IAM user by email or id, including the user's role assignments [{name, tenantId}]. Provide either email or id. Read-only.",
                Schema("""{"type":"object","properties":{"email":{"type":"string","description":"The user's email address."},"id":{"type":"string","description":"The user's id (GUID)."}},"additionalProperties":false}"""),
                readOnly: true),

            Tool("list_roles",
                "List all roles as {id, name}. Use the id for send_invitation / assign_role. Read-only.",
                Schema("""{"type":"object","properties":{},"additionalProperties":false}"""),
                readOnly: true),

            Tool("list_tenants",
                "List all tenants as {id, name}. Use the id for send_invitation / list_invitations / assign_role. Read-only.",
                Schema("""{"type":"object","properties":{},"additionalProperties":false}"""),
                readOnly: true),

            Tool("list_invitations",
                "List invitations (max 100, newest first), optionally only those of one tenant. Read-only.",
                Schema("""{"type":"object","properties":{"tenantId":{"type":"string","description":"Optional tenant id (GUID) to filter on."}},"additionalProperties":false}"""),
                readOnly: true),

            Tool("send_invitation",
                "ONBOARDS A REAL PERSON. Create an invitation for an email address into a tenant with a role, and try to email the accept link. The invited person sets their own password on the accept page — this is the only way this MCP surface onboards users. Returns emailSent (the invitation exists even when the email failed) and the acceptUrl so it can be handed over manually.",
                Schema("""{"type":"object","properties":{"email":{"type":"string","description":"Email address to invite."},"tenantId":{"type":"string","description":"Tenant id (GUID) from list_tenants."},"roleId":{"type":"string","description":"Role id (GUID) from list_roles."},"expiryDays":{"type":"integer","description":"Optional validity in days (default from configuration, normally 7)."}},"required":["email","tenantId","roleId"],"additionalProperties":false}"""),
                destructive: true),

            Tool("revoke_invitation",
                "Revoke a pending invitation by id, so its accept link stops working.",
                Schema("""{"type":"object","properties":{"id":{"type":"string","description":"Invitation id (GUID) from list_invitations."}},"required":["id"],"additionalProperties":false}"""),
                destructive: true),

            Tool("assign_role",
                "Grant a role to an existing user, optionally scoped to a tenant (omit tenantId for a global assignment). The grant is recorded as given by the acting user.",
                Schema("""{"type":"object","properties":{"userId":{"type":"string","description":"User id (GUID) from list_users / get_user."},"roleId":{"type":"string","description":"Role id (GUID) from list_roles."},"tenantId":{"type":"string","description":"Optional tenant id (GUID); omit for a global role assignment."}},"required":["userId","roleId"],"additionalProperties":false}"""),
                destructive: true),

            Tool("remove_role",
                "Remove a user's assignments of a role (all tenant scopes of that role).",
                Schema("""{"type":"object","properties":{"userId":{"type":"string","description":"User id (GUID)."},"roleId":{"type":"string","description":"Role id (GUID)."}},"required":["userId","roleId"],"additionalProperties":false}"""),
                destructive: true),
        };
        return ValueTask.FromResult(tools);
    }

    // --- tool dispatch (tools/call) ---

    public async ValueTask<CallToolResult?> TryCallToolAsync(
        McpToolContext context, string toolName,
        IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken cancellationToken)
    {
        if (!OwnsTool(toolName)) return null;

        var actor = await ResolveActingUserAsync(context.User, cancellationToken);
        if (actor == null)
            return Error("Unauthorized: this MCP key's subject is not an active IAM user.");

        try
        {
            return toolName switch
            {
                "list_users" => await ListUsersAsync(arguments, cancellationToken),
                "get_user" => await GetUserAsync(arguments, cancellationToken),
                "list_roles" => await ListRolesAsync(cancellationToken),
                "list_tenants" => await ListTenantsAsync(cancellationToken),
                "list_invitations" => await ListInvitationsAsync(arguments, cancellationToken),
                "send_invitation" => await SendInvitationAsync(actor, arguments, cancellationToken),
                "revoke_invitation" => await RevokeInvitationAsync(actor, arguments, cancellationToken),
                "assign_role" => await AssignRoleAsync(actor, arguments, cancellationToken),
                "remove_role" => await RemoveRoleAsync(actor, arguments, cancellationToken),
                _ => null,
            };
        }
        catch (McpArgumentException ex)
        {
            return Error(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            // InvitationService reports its validation failures (unknown tenant/role, domain not
            // allowed, member limit, duplicate pending invite, ...) as InvalidOperationException.
            return Error(ex.Message);
        }
    }

    private static bool OwnsTool(string name) => name is
        "list_users" or "get_user" or "list_roles" or "list_tenants" or "list_invitations"
        or "send_invitation" or "revoke_invitation" or "assign_role" or "remove_role";

    /// <summary>The acting user: the IAM user whose id equals the authenticated key's subject.
    /// A key bound to an unknown or deactivated user can call nothing.</summary>
    private async Task<User?> ResolveActingUserAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var subject = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        if (!Guid.TryParse(subject, out var userId)) return null;
        return await _db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.IsActive, ct);
    }

    // --- read-only tools ---

    private async Task<CallToolResult> ListUsersAsync(IReadOnlyDictionary<string, JsonElement> args, CancellationToken ct)
    {
        var query = _db.Users.AsNoTracking().AsQueryable();
        if (OptionalString(args, "search") is { Length: > 0 } search)
        {
            var term = search.ToLowerInvariant();
            query = query.Where(u =>
                u.Email.ToLower().Contains(term) ||
                u.FirstName.ToLower().Contains(term) ||
                u.LastName.ToLower().Contains(term));
        }

        var users = await query.OrderBy(u => u.Email).Take(100).ToListAsync(ct);
        return Ok(new { users = users.Select(UserDto).ToList() });
    }

    private async Task<CallToolResult> GetUserAsync(IReadOnlyDictionary<string, JsonElement> args, CancellationToken ct)
    {
        var email = OptionalString(args, "email");
        var id = OptionalGuid(args, "id");
        if (email == null && id == null) return Error("Provide 'email' or 'id'.");

        var query = _db.Users.AsNoTracking()
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .AsQueryable();
        var user = id != null
            ? await query.FirstOrDefaultAsync(u => u.Id == id, ct)
            : await query.FirstOrDefaultAsync(u => u.Email.ToLower() == email!.ToLowerInvariant(), ct);
        if (user == null) return Error("User not found.");

        return Ok(new
        {
            id = user.Id,
            email = user.Email,
            firstName = user.FirstName,
            lastName = user.LastName,
            isActive = user.IsActive,
            emailConfirmed = user.EmailConfirmed,
            roles = user.UserRoles.Select(ur => new { name = ur.Role.Name, tenantId = ur.TenantId }).ToList(),
        });
    }

    private async Task<CallToolResult> ListRolesAsync(CancellationToken ct)
    {
        var roles = await _db.Roles.AsNoTracking().OrderBy(r => r.Name)
            .Select(r => new { id = r.Id, name = r.Name }).ToListAsync(ct);
        return Ok(new { roles });
    }

    private async Task<CallToolResult> ListTenantsAsync(CancellationToken ct)
    {
        var tenants = await _db.Tenants.AsNoTracking().OrderBy(t => t.Name)
            .Select(t => new { id = t.Id, name = t.Name }).ToListAsync(ct);
        return Ok(new { tenants });
    }

    private async Task<CallToolResult> ListInvitationsAsync(IReadOnlyDictionary<string, JsonElement> args, CancellationToken ct)
    {
        List<Invitation> invitations;
        if (OptionalGuid(args, "tenantId") is { } tenantId)
        {
            invitations = await _invitations.GetInvitationsByTenantAsync(tenantId, ct);
            invitations = invitations.OrderByDescending(i => i.CreatedAt).Take(100).ToList();
        }
        else
        {
            invitations = await _db.Invitations.AsNoTracking()
                .OrderByDescending(i => i.CreatedAt).Take(100).ToListAsync(ct);
        }

        return Ok(new
        {
            invitations = invitations.Select(i => new
            {
                id = i.Id,
                email = i.Email,
                tenantId = i.TenantId,
                roleId = i.RoleId,
                status = i.Status,
                invitedByUserId = i.InvitedByUserId,
                createdAt = i.CreatedAt,
                expiresAt = i.ExpiresAt,
            }).ToList(),
        });
    }

    // --- mutating tools (every one audit-logged with actor + tool + parameters) ---

    private async Task<CallToolResult> SendInvitationAsync(User actor, IReadOnlyDictionary<string, JsonElement> args, CancellationToken ct)
    {
        var email = RequiredString(args, "email");
        var tenantId = RequiredGuid(args, "tenantId");
        var roleId = RequiredGuid(args, "roleId");
        var expiryDays = OptionalInt(args, "expiryDays");
        if (expiryDays is <= 0) return Error("expiryDays must be positive.");

        if (await IsSuperAdminRoleAsync(roleId, ct) && !await ActorIsSuperAdminAsync(actor, ct))
            return Error($"Only a {SuperAdminRole} may invite someone as {SuperAdminRole}.");

        Audit(actor, "send_invitation", new { email, tenantId, roleId, expiryDays });

        // InvitedByUserId = the acting user, so the invitation audit trail matches a human
        // sending the same invite from the admin UI.
        var result = await _invitations.SendInvitationAsync(email, tenantId, roleId, actor.Id, expiryDays, ct);
        var invitation = result.Invitation;
        return Ok(new
        {
            id = invitation.Id,
            email = invitation.Email,
            status = invitation.Status,
            expiresAt = invitation.ExpiresAt,
            emailSent = result.EmailSent,
            // Same construction as InvitationsController.AcceptUrl, so manual hand-over of the
            // link (e.g. when emailSent is false) lands on the same accept page.
            acceptUrl = $"{_emailBaseUrl?.TrimEnd('/')}/accept-invite?token={invitation.Token}",
        });
    }

    private async Task<CallToolResult> RevokeInvitationAsync(User actor, IReadOnlyDictionary<string, JsonElement> args, CancellationToken ct)
    {
        var id = RequiredGuid(args, "id");

        Audit(actor, "revoke_invitation", new { id });

        var revoked = await _invitations.RevokeInvitationAsync(id, ct);
        return revoked
            ? Ok(new { id, revoked = true })
            : Error($"Invitation {id} was not found or is not pending.");
    }

    private async Task<CallToolResult> AssignRoleAsync(User actor, IReadOnlyDictionary<string, JsonElement> args, CancellationToken ct)
    {
        var userId = RequiredGuid(args, "userId");
        var roleId = RequiredGuid(args, "roleId");
        var tenantId = OptionalGuid(args, "tenantId");

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user == null) return Error($"User {userId} not found.");
        var role = await _db.Roles.FirstOrDefaultAsync(r => r.Id == roleId, ct);
        if (role == null) return Error($"Role {roleId} not found.");
        if (tenantId != null && !await _db.Tenants.AnyAsync(t => t.Id == tenantId, ct))
            return Error($"Tenant {tenantId} not found.");

        // Same escalation gate as the invitation path: an agent key whose user is not a
        // SuperAdmin cannot mint one.
        if (string.Equals(role.Name, SuperAdminRole, StringComparison.OrdinalIgnoreCase)
            && !await ActorIsSuperAdminAsync(actor, ct))
            return Error($"Only a {SuperAdminRole} may assign the {SuperAdminRole} role.");

        var exists = await _db.UserRoles.AnyAsync(
            ur => ur.UserId == userId && ur.RoleId == roleId && ur.TenantId == tenantId, ct);
        if (exists)
            return Ok(new { userId, roleId, tenantId, assigned = false, note = "The user already has this role in this scope." });

        Audit(actor, "assign_role", new { userId, roleId, tenantId });

        _db.UserRoles.Add(new UserRole
        {
            UserId = userId,
            RoleId = roleId,
            TenantId = tenantId,
            GrantedBy = actor.Id,
        });
        await _db.SaveChangesAsync(ct);
        return Ok(new { userId, roleId, tenantId, roleName = role.Name, assigned = true });
    }

    private async Task<CallToolResult> RemoveRoleAsync(User actor, IReadOnlyDictionary<string, JsonElement> args, CancellationToken ct)
    {
        var userId = RequiredGuid(args, "userId");
        var roleId = RequiredGuid(args, "roleId");

        var assignments = await _db.UserRoles
            .Where(ur => ur.UserId == userId && ur.RoleId == roleId)
            .ToListAsync(ct);
        if (assignments.Count == 0)
            return Ok(new { userId, roleId, removed = 0, note = "The user does not have this role." });

        Audit(actor, "remove_role", new { userId, roleId });

        _db.UserRoles.RemoveRange(assignments);
        await _db.SaveChangesAsync(ct);
        return Ok(new { userId, roleId, removed = assignments.Count });
    }

    // --- guards + audit ---

    private async Task<bool> IsSuperAdminRoleAsync(Guid roleId, CancellationToken ct)
    {
        var name = await _db.Roles.Where(r => r.Id == roleId).Select(r => r.Name).FirstOrDefaultAsync(ct);
        return string.Equals(name, SuperAdminRole, StringComparison.OrdinalIgnoreCase);
    }

    private Task<bool> ActorIsSuperAdminAsync(User actor, CancellationToken ct) =>
        _db.UserRoles.AnyAsync(ur => ur.UserId == actor.Id && ur.Role.Name == SuperAdminRole, ct);

    /// <summary>Audit line for every mutating tool: acting subject + tool + parameters.
    /// Parameters never contain secrets (no passwords/tokens exist on this surface; the
    /// invitation token is only in the result, never logged).</summary>
    private void Audit(User actor, string tool, object parameters) =>
        _logger.LogInformation("IAM MCP audit: user {ActingUserId} ({ActingEmail}) called {Tool} {Parameters}",
            actor.Id, actor.Email, tool, JsonSerializer.Serialize(parameters, ResultJson));

    // --- JSON helpers (argument extraction + nice errors for bad input) ---

    private sealed class McpArgumentException(string message) : Exception(message);

    private static string RequiredString(IReadOnlyDictionary<string, JsonElement> args, string name)
    {
        var value = OptionalString(args, name);
        if (string.IsNullOrWhiteSpace(value)) throw new McpArgumentException($"'{name}' is required.");
        return value;
    }

    private static string? OptionalString(IReadOnlyDictionary<string, JsonElement> args, string name)
    {
        if (!args.TryGetValue(name, out var v) || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
    }

    private static Guid RequiredGuid(IReadOnlyDictionary<string, JsonElement> args, string name) =>
        OptionalGuid(args, name) ?? throw new McpArgumentException($"'{name}' is required (GUID).");

    private static Guid? OptionalGuid(IReadOnlyDictionary<string, JsonElement> args, string name)
    {
        var raw = OptionalString(args, name);
        if (raw == null) return null;
        if (!Guid.TryParse(raw, out var guid)) throw new McpArgumentException($"'{name}' must be a GUID.");
        return guid;
    }

    private static int? OptionalInt(IReadOnlyDictionary<string, JsonElement> args, string name)
    {
        if (!args.TryGetValue(name, out var v) || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        throw new McpArgumentException($"'{name}' must be an integer.");
    }

    // --- result + schema helpers ---

    private static object UserDto(User u) => new
    {
        id = u.Id,
        email = u.Email,
        firstName = u.FirstName,
        lastName = u.LastName,
        isActive = u.IsActive,
        emailConfirmed = u.EmailConfirmed,
    };

    private static readonly JsonSerializerOptions ResultJson = new() { WriteIndented = false };

    private static CallToolResult Ok(object payload) => new()
    {
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload, ResultJson) }],
    };

    private static CallToolResult Error(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
    };

    private static Tool Tool(string name, string description, JsonElement schema, bool readOnly = false, bool destructive = false) => new()
    {
        Name = name,
        Description = description,
        InputSchema = schema,
        Annotations = new ToolAnnotations { ReadOnlyHint = readOnly, DestructiveHint = destructive },
    };

    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
