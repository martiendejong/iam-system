using System.Security.Claims;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace IAM.API.Controllers;

/// <summary>
/// Directory (LDAP) sync configuration. Every action needs SuperAdmin or an active BuildingOwner UserRole for the
/// configuration's tenant (task 4698); a sync can only touch and grant what belongs to that tenant.
/// </summary>
[ApiController]
[Route("api/directory-sync")]
[Authorize]
public class DirectorySyncController : ControllerBase
{
    private readonly IDirectorySyncService _syncService;
    private readonly IAMDbContext _context;

    public DirectorySyncController(IDirectorySyncService syncService, IAMDbContext context)
    {
        _syncService = syncService;
        _context = context;
    }

    // ----- authorization -----------------------------------------------------------------------

    private Guid? CurrentUserId()
    {
        var subject = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.TryParse(subject, out var id) ? id : null;
    }

    private bool IsSuperAdmin => User.IsInRole("SuperAdmin");

    private ObjectResult Forbidden(string message) =>
        StatusCode(StatusCodes.Status403Forbidden, new { error = message });

    /// <summary>
    /// SuperAdmin, or an active BuildingOwner row for exactly this tenant. Runs before any lookup, so a caller
    /// without authority gets the same answer whether or not the tenant or configuration exists.
    /// Null = allowed, otherwise the response to send.
    /// </summary>
    private async Task<IActionResult?> RequireTenantOwnerAsync(Guid tenantId, CancellationToken ct)
    {
        var userId = CurrentUserId();
        if (userId == null)
            return Unauthorized();

        if (IsSuperAdmin)
            return null;

        if (!await DirectorySyncAuthority.IsOwnerOfTenantAsync(_context, userId.Value, tenantId, ct))
            return Forbidden("Only a SuperAdmin or an owner of this tenant can manage its directory sync");

        return null;
    }

    /// <summary>
    /// For id-only routes: a coarse gate first (a caller who owns no tenant gets 403 for any id), then the
    /// configuration's own tenant. Returns the denial/404 to send, or null with the tenant id resolved.
    /// </summary>
    private async Task<(IActionResult? Denied, Guid TenantId)> RequireConfigOwnerAsync(Guid configId, CancellationToken ct)
    {
        var userId = CurrentUserId();
        if (userId == null)
            return (Unauthorized(), Guid.Empty);

        if (!IsSuperAdmin && !await DirectorySyncAuthority.OwnsAnyTenantAsync(_context, userId.Value, ct))
            return (Forbidden("Only a SuperAdmin or a tenant owner can manage directory sync"), Guid.Empty);

        var tenantId = await _syncService.GetConfigTenantIdAsync(configId, ct);
        if (tenantId == null)
            return (NotFound(new { error = "Configuration not found" }), Guid.Empty);

        var denied = await RequireTenantOwnerAsync(tenantId.Value, ct);
        return (denied, tenantId.Value);
    }

    // ----- actions -----------------------------------------------------------------------------

    /// <summary>
    /// Get all directory sync configs for a tenant
    /// </summary>
    [HttpGet("configs")]
    public async Task<IActionResult> GetConfigs([FromQuery] Guid tenantId, CancellationToken ct)
    {
        var denied = await RequireTenantOwnerAsync(tenantId, ct);
        if (denied != null)
            return denied;

        if (tenantId == Guid.Empty)
        {
            return BadRequest(new { error = "Tenant ID is required" });
        }

        var configs = await _syncService.GetConfigsByTenantAsync(tenantId, ct);

        return Ok(configs.Select(c => new
        {
            id = c.Id,
            tenantId = c.TenantId,
            tenantName = c.Tenant?.Name,
            name = c.Name,
            ldapUrl = c.LdapUrl,
            bindDn = c.BindDn,
            searchBase = c.SearchBase,
            searchFilter = c.SearchFilter,
            syncInterval = c.SyncInterval,
            attributeMapping = ParseJsonSafe(c.AttributeMapping),
            groupToRoleMapping = ParseJsonSafe(c.GroupToRoleMapping),
            isActive = c.IsActive,
            lastSyncAt = c.LastSyncAt,
            lastSyncStatus = c.LastSyncStatus,
            createdAt = c.CreatedAt,
            updatedAt = c.UpdatedAt
        }));
    }

    /// <summary>
    /// Get a single directory sync config by ID
    /// </summary>
    [HttpGet("configs/{id:guid}")]
    public async Task<IActionResult> GetConfig(Guid id, CancellationToken ct)
    {
        var (denied, _) = await RequireConfigOwnerAsync(id, ct);
        if (denied != null)
            return denied;

        var config = await _syncService.GetConfigAsync(id, ct);
        if (config == null)
        {
            return NotFound(new { error = "Configuration not found" });
        }

        return Ok(new
        {
            id = config.Id,
            tenantId = config.TenantId,
            tenantName = config.Tenant?.Name,
            name = config.Name,
            ldapUrl = config.LdapUrl,
            bindDn = config.BindDn,
            searchBase = config.SearchBase,
            searchFilter = config.SearchFilter,
            syncInterval = config.SyncInterval,
            attributeMapping = ParseJsonSafe(config.AttributeMapping),
            groupToRoleMapping = ParseJsonSafe(config.GroupToRoleMapping),
            isActive = config.IsActive,
            lastSyncAt = config.LastSyncAt,
            lastSyncStatus = config.LastSyncStatus,
            createdAt = config.CreatedAt,
            updatedAt = config.UpdatedAt
        });
    }

    /// <summary>
    /// Create a new directory sync configuration. The TenantId in the body decides who may do it.
    /// </summary>
    [HttpPost("configs")]
    public async Task<IActionResult> CreateConfig([FromBody] CreateDirectorySyncConfigRequest request, CancellationToken ct)
    {
        var denied = await RequireTenantOwnerAsync(request.TenantId, ct);
        if (denied != null)
            return denied;

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { error = "Name is required" });
        }

        if (request.TenantId == Guid.Empty)
        {
            return BadRequest(new { error = "Tenant ID is required" });
        }

        if (string.IsNullOrWhiteSpace(request.LdapUrl))
        {
            return BadRequest(new { error = "LDAP URL is required" });
        }

        var config = new DirectorySyncConfig
        {
            TenantId = request.TenantId,
            Name = request.Name,
            LdapUrl = request.LdapUrl,
            BindDn = request.BindDn ?? string.Empty,
            BindPassword = request.BindPassword ?? string.Empty,
            SearchBase = request.SearchBase ?? string.Empty,
            SearchFilter = request.SearchFilter ?? "(objectClass=person)",
            SyncInterval = request.SyncInterval ?? 60,
            AttributeMapping = request.AttributeMapping != null
                ? JsonSerializer.Serialize(request.AttributeMapping)
                : "{}",
            GroupToRoleMapping = request.GroupToRoleMapping != null
                ? JsonSerializer.Serialize(request.GroupToRoleMapping)
                : "{}",
            IsActive = request.IsActive ?? true
        };

        try
        {
            var created = await _syncService.CreateConfigAsync(config, IsSuperAdmin, ct);

            return CreatedAtAction(
                nameof(GetConfig),
                new { id = created.Id },
                new
                {
                    id = created.Id,
                    tenantId = created.TenantId,
                    name = created.Name,
                    ldapUrl = created.LdapUrl,
                    bindDn = created.BindDn,
                    searchBase = created.SearchBase,
                    searchFilter = created.SearchFilter,
                    syncInterval = created.SyncInterval,
                    attributeMapping = request.AttributeMapping,
                    groupToRoleMapping = request.GroupToRoleMapping,
                    isActive = created.IsActive,
                    createdAt = created.CreatedAt
                });
        }
        catch (DirectorySyncValidationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Update an existing directory sync configuration (the tenant cannot change)
    /// </summary>
    [HttpPut("configs/{id:guid}")]
    public async Task<IActionResult> UpdateConfig(Guid id, [FromBody] UpdateDirectorySyncConfigRequest request, CancellationToken ct)
    {
        var (denied, _) = await RequireConfigOwnerAsync(id, ct);
        if (denied != null)
            return denied;

        var config = new DirectorySyncConfig
        {
            Id = id,
            Name = request.Name ?? string.Empty,
            LdapUrl = request.LdapUrl ?? string.Empty,
            BindDn = request.BindDn ?? string.Empty,
            BindPassword = request.BindPassword ?? string.Empty,
            SearchBase = request.SearchBase ?? string.Empty,
            SearchFilter = request.SearchFilter ?? "(objectClass=person)",
            SyncInterval = request.SyncInterval ?? 60,
            AttributeMapping = request.AttributeMapping != null
                ? JsonSerializer.Serialize(request.AttributeMapping)
                : "{}",
            GroupToRoleMapping = request.GroupToRoleMapping != null
                ? JsonSerializer.Serialize(request.GroupToRoleMapping)
                : "{}",
            IsActive = request.IsActive ?? true
        };

        try
        {
            var updated = await _syncService.UpdateConfigAsync(config, IsSuperAdmin, ct);

            return Ok(new
            {
                id = updated.Id,
                tenantId = updated.TenantId,
                name = updated.Name,
                ldapUrl = updated.LdapUrl,
                bindDn = updated.BindDn,
                searchBase = updated.SearchBase,
                searchFilter = updated.SearchFilter,
                syncInterval = updated.SyncInterval,
                attributeMapping = request.AttributeMapping,
                groupToRoleMapping = request.GroupToRoleMapping,
                isActive = updated.IsActive,
                lastSyncAt = updated.LastSyncAt,
                lastSyncStatus = updated.LastSyncStatus,
                updatedAt = updated.UpdatedAt
            });
        }
        catch (DirectorySyncValidationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Delete a directory sync configuration
    /// </summary>
    [HttpDelete("configs/{id:guid}")]
    public async Task<IActionResult> DeleteConfig(Guid id, CancellationToken ct)
    {
        var (denied, _) = await RequireConfigOwnerAsync(id, ct);
        if (denied != null)
            return denied;

        var result = await _syncService.DeleteConfigAsync(id, ct);
        if (!result)
        {
            return NotFound(new { error = "Configuration not found" });
        }

        return Ok(new { message = "Directory sync configuration deleted successfully" });
    }

    /// <summary>
    /// Test LDAP connection for a configuration
    /// </summary>
    [HttpPost("configs/{id:guid}/test-connection")]
    public async Task<IActionResult> TestConnection(Guid id, CancellationToken ct)
    {
        var (denied, _) = await RequireConfigOwnerAsync(id, ct);
        if (denied != null)
            return denied;

        var result = await _syncService.TestConnectionAsync(id, ct);

        return Ok(new
        {
            success = result.Success,
            message = result.Message,
            userCount = result.UserCount,
            serverType = result.ServerType
        });
    }

    /// <summary>
    /// Manually trigger a sync for a configuration
    /// </summary>
    [HttpPost("configs/{id:guid}/sync")]
    public async Task<IActionResult> TriggerSync(Guid id, CancellationToken ct, [FromQuery] string type = "full")
    {
        var (denied, _) = await RequireConfigOwnerAsync(id, ct);
        if (denied != null)
            return denied;

        try
        {
            DirectorySyncLog log;

            if (type.Equals("delta", StringComparison.OrdinalIgnoreCase))
            {
                log = await _syncService.RunDeltaSyncAsync(id, ct);
            }
            else
            {
                log = await _syncService.RunFullSyncAsync(id, ct);
            }

            return Ok(new
            {
                id = log.Id,
                configId = log.ConfigId,
                syncType = log.SyncType.ToString(),
                status = log.Status.ToString(),
                usersCreated = log.UsersCreated,
                usersUpdated = log.UsersUpdated,
                usersDisabled = log.UsersDisabled,
                groupsSynced = log.GroupsSynced,
                errors = ParseJsonSafe(log.Errors),
                startedAt = log.StartedAt,
                completedAt = log.CompletedAt
            });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get sync logs for a configuration
    /// </summary>
    [HttpGet("configs/{id:guid}/logs")]
    public async Task<IActionResult> GetSyncLogs(Guid id, CancellationToken ct, [FromQuery] int limit = 50)
    {
        var (denied, _) = await RequireConfigOwnerAsync(id, ct);
        if (denied != null)
            return denied;

        var logs = await _syncService.GetSyncLogsAsync(id, limit, ct);

        return Ok(logs.Select(l => new
        {
            id = l.Id,
            configId = l.ConfigId,
            syncType = l.SyncType.ToString(),
            status = l.Status.ToString(),
            usersCreated = l.UsersCreated,
            usersUpdated = l.UsersUpdated,
            usersDisabled = l.UsersDisabled,
            groupsSynced = l.GroupsSynced,
            errors = ParseJsonSafe(l.Errors),
            startedAt = l.StartedAt,
            completedAt = l.CompletedAt,
            duration = l.CompletedAt.HasValue
                ? (l.CompletedAt.Value - l.StartedAt).TotalSeconds
                : (double?)null
        }));
    }

    private static object? ParseJsonSafe(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize<object>(json);
        }
        catch
        {
            return json;
        }
    }
}

public record CreateDirectorySyncConfigRequest(
    Guid TenantId,
    string Name,
    string LdapUrl,
    string? BindDn,
    string? BindPassword,
    string? SearchBase,
    string? SearchFilter,
    int? SyncInterval,
    Dictionary<string, string>? AttributeMapping,
    Dictionary<string, string>? GroupToRoleMapping,
    bool? IsActive
);

public record UpdateDirectorySyncConfigRequest(
    string? Name,
    string? LdapUrl,
    string? BindDn,
    string? BindPassword,
    string? SearchBase,
    string? SearchFilter,
    int? SyncInterval,
    Dictionary<string, string>? AttributeMapping,
    Dictionary<string, string>? GroupToRoleMapping,
    bool? IsActive
);
