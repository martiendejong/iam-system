using IAM.Core.Entities;
using IAM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/directory-sync")]
[Authorize]
public class DirectorySyncController : ControllerBase
{
    private readonly IDirectorySyncService _syncService;

    public DirectorySyncController(IDirectorySyncService syncService)
    {
        _syncService = syncService;
    }

    /// <summary>
    /// Get all directory sync configs for a tenant
    /// </summary>
    [HttpGet("configs")]
    public async Task<IActionResult> GetConfigs([FromQuery] Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            return BadRequest(new { error = "Tenant ID is required" });
        }

        var configs = await _syncService.GetConfigsByTenantAsync(tenantId);

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
    public async Task<IActionResult> GetConfig(Guid id)
    {
        var config = await _syncService.GetConfigAsync(id);
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
    /// Create a new directory sync configuration
    /// </summary>
    [HttpPost("configs")]
    public async Task<IActionResult> CreateConfig([FromBody] CreateDirectorySyncConfigRequest request)
    {
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
            var created = await _syncService.CreateConfigAsync(config);

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
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Update an existing directory sync configuration
    /// </summary>
    [HttpPut("configs/{id:guid}")]
    public async Task<IActionResult> UpdateConfig(Guid id, [FromBody] UpdateDirectorySyncConfigRequest request)
    {
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
            var updated = await _syncService.UpdateConfigAsync(config);

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
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Delete a directory sync configuration
    /// </summary>
    [HttpDelete("configs/{id:guid}")]
    public async Task<IActionResult> DeleteConfig(Guid id)
    {
        var result = await _syncService.DeleteConfigAsync(id);
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
    public async Task<IActionResult> TestConnection(Guid id)
    {
        var result = await _syncService.TestConnectionAsync(id);

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
    public async Task<IActionResult> TriggerSync(Guid id, [FromQuery] string type = "full")
    {
        try
        {
            DirectorySyncLog log;

            if (type.Equals("delta", StringComparison.OrdinalIgnoreCase))
            {
                log = await _syncService.RunDeltaSyncAsync(id);
            }
            else
            {
                log = await _syncService.RunFullSyncAsync(id);
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
    public async Task<IActionResult> GetSyncLogs(Guid id, [FromQuery] int limit = 50)
    {
        var logs = await _syncService.GetSyncLogsAsync(id, limit);

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
