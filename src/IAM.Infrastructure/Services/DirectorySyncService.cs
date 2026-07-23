using System.DirectoryServices.Protocols;
using System.Net;
using System.Text.Json;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IAM.Infrastructure.Services;

public class DirectorySyncService : IDirectorySyncService
{
    private readonly IAMDbContext _context;
    private readonly ILogger<DirectorySyncService> _logger;

    // Default LDAP attribute mapping if none specified
    private static readonly Dictionary<string, string> DefaultAttributeMapping = new()
    {
        { "email", "mail" },
        { "firstName", "givenName" },
        { "lastName", "sn" },
        { "phoneNumber", "telephoneNumber" }
    };

    public DirectorySyncService(IAMDbContext context, ILogger<DirectorySyncService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<DirectorySyncConfig> CreateConfigAsync(DirectorySyncConfig config, CancellationToken ct = default)
    {
        var tenantExists = await _context.Tenants.AnyAsync(t => t.Id == config.TenantId, ct);
        if (!tenantExists)
        {
            throw new InvalidOperationException("Tenant not found");
        }

        _context.DirectorySyncConfigs.Add(config);
        await _context.SaveChangesAsync(ct);
        return config;
    }

    public async Task<DirectorySyncConfig?> GetConfigAsync(Guid configId, CancellationToken ct = default)
    {
        return await _context.DirectorySyncConfigs
            .Include(c => c.Tenant)
            .FirstOrDefaultAsync(c => c.Id == configId, ct);
    }

    public async Task<List<DirectorySyncConfig>> GetConfigsByTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        return await _context.DirectorySyncConfigs
            .Include(c => c.Tenant)
            .Where(c => c.TenantId == tenantId)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
    }

    public async Task<DirectorySyncConfig> UpdateConfigAsync(DirectorySyncConfig config, CancellationToken ct = default)
    {
        var existing = await _context.DirectorySyncConfigs.FindAsync(new object[] { config.Id }, ct);
        if (existing == null)
        {
            throw new InvalidOperationException("Directory sync configuration not found");
        }

        existing.Name = config.Name;
        existing.LdapUrl = config.LdapUrl;
        existing.BindDn = config.BindDn;

        // Only update password if a new one is provided (non-empty)
        if (!string.IsNullOrWhiteSpace(config.BindPassword))
        {
            existing.BindPassword = config.BindPassword;
        }

        existing.SearchBase = config.SearchBase;
        existing.SearchFilter = config.SearchFilter;
        existing.SyncInterval = config.SyncInterval;
        existing.AttributeMapping = config.AttributeMapping;
        existing.GroupToRoleMapping = config.GroupToRoleMapping;
        existing.IsActive = config.IsActive;
        existing.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);
        return existing;
    }

    public async Task<bool> DeleteConfigAsync(Guid configId, CancellationToken ct = default)
    {
        var config = await _context.DirectorySyncConfigs
            .Include(c => c.SyncLogs)
            .FirstOrDefaultAsync(c => c.Id == configId, ct);

        if (config == null)
        {
            return false;
        }

        _context.DirectorySyncLogs.RemoveRange(config.SyncLogs);
        _context.DirectorySyncConfigs.Remove(config);
        await _context.SaveChangesAsync(ct);

        return true;
    }

    public async Task<DirectoryTestResult> TestConnectionAsync(Guid configId, CancellationToken ct = default)
    {
        var config = await _context.DirectorySyncConfigs.FindAsync(new object[] { configId }, ct);
        if (config == null)
        {
            return new DirectoryTestResult { Success = false, Message = "Configuration not found" };
        }

        try
        {
            using var connection = CreateLdapConnection(config);
            connection.Bind(new NetworkCredential(config.BindDn, config.BindPassword));

            // Try a simple search to verify permissions
            var searchRequest = new SearchRequest(
                config.SearchBase,
                config.SearchFilter,
                SearchScope.Subtree,
                "dn");
            searchRequest.SizeLimit = 5;

            var response = (SearchResponse)connection.SendRequest(searchRequest);

            // Try to detect server type from root DSE
            string? serverType = null;
            try
            {
                var rootDseRequest = new SearchRequest(
                    "",
                    "(objectClass=*)",
                    SearchScope.Base,
                    "vendorName", "vendorVersion", "isGlobalCatalogReady");

                var rootDseResponse = (SearchResponse)connection.SendRequest(rootDseRequest);
                if (rootDseResponse.Entries.Count > 0)
                {
                    var entry = rootDseResponse.Entries[0];
                    if (entry.Attributes["isGlobalCatalogReady"] != null)
                    {
                        serverType = "Active Directory";
                    }
                    else if (entry.Attributes["vendorName"] != null)
                    {
                        serverType = entry.Attributes["vendorName"][0]?.ToString();
                    }
                }
            }
            catch
            {
                // Root DSE detection is optional
                serverType = "LDAP";
            }

            return new DirectoryTestResult
            {
                Success = true,
                Message = $"Connection successful. Found {response.Entries.Count}+ entries matching filter.",
                UserCount = response.Entries.Count,
                ServerType = serverType ?? "LDAP"
            };
        }
        catch (LdapException ex)
        {
            _logger.LogWarning(ex, "LDAP connection test failed for config {ConfigId}", configId);
            return new DirectoryTestResult
            {
                Success = false,
                Message = $"LDAP error: {ex.Message} (Error code: {ex.ErrorCode})"
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Connection test failed for config {ConfigId}", configId);
            return new DirectoryTestResult
            {
                Success = false,
                Message = $"Connection failed: {ex.Message}"
            };
        }
    }

    public async Task<DirectorySyncLog> RunFullSyncAsync(Guid configId, CancellationToken ct = default)
    {
        var config = await _context.DirectorySyncConfigs.FindAsync(new object[] { configId }, ct);
        if (config == null)
        {
            throw new InvalidOperationException("Configuration not found");
        }

        var log = new DirectorySyncLog
        {
            ConfigId = configId,
            SyncType = DirectorySyncType.Full,
            Status = DirectorySyncStatus.Running
        };

        _context.DirectorySyncLogs.Add(log);
        await _context.SaveChangesAsync(ct);

        var errors = new List<string>();

        try
        {
            var attributeMapping = ParseAttributeMapping(config.AttributeMapping);
            var groupToRoleMapping = ParseGroupToRoleMapping(config.GroupToRoleMapping);

            // Get all LDAP attributes we need to fetch
            var ldapAttributes = attributeMapping.Values
                .Concat(new[] { "dn", "memberOf", "whenChanged" })
                .Distinct()
                .ToArray();

            using var connection = CreateLdapConnection(config);
            connection.Bind(new NetworkCredential(config.BindDn, config.BindPassword));

            var searchRequest = new SearchRequest(
                config.SearchBase,
                config.SearchFilter,
                SearchScope.Subtree,
                ldapAttributes);

            var response = (SearchResponse)connection.SendRequest(searchRequest);

            // Track which directory users we've seen (by email) to detect removed users
            var directoryEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (SearchResultEntry entry in response.Entries)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var result = await SyncUserFromEntry(entry, config.TenantId, attributeMapping, groupToRoleMapping, ct);
                    if (result.email != null) directoryEmails.Add(result.email);

                    if (result.created) log.UsersCreated++;
                    else if (result.updated) log.UsersUpdated++;
                }
                catch (Exception ex)
                {
                    var dn = entry.DistinguishedName;
                    errors.Add($"Error syncing {dn}: {ex.Message}");
                    _logger.LogWarning(ex, "Error syncing LDAP entry {DN}", dn);
                }
            }

            // Disable users that no longer exist in directory
            // (Only disable users that were previously synced from this directory)
            if (directoryEmails.Count > 0)
            {
                var disabledCount = await DisableMissingUsersAsync(
                    config.TenantId, directoryEmails, ct);
                log.UsersDisabled = disabledCount;
            }

            // Sync group role mappings count
            log.GroupsSynced = groupToRoleMapping.Count;

            log.Status = errors.Count > 0 ? DirectorySyncStatus.PartialSuccess : DirectorySyncStatus.Success;
            log.CompletedAt = DateTime.UtcNow;
            log.Errors = errors.Count > 0 ? JsonSerializer.Serialize(errors) : null;

            config.LastSyncAt = DateTime.UtcNow;
            config.LastSyncStatus = log.Status.ToString();
            config.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Full sync completed for config {ConfigId}: {Created} created, {Updated} updated, {Disabled} disabled, {Errors} errors",
                configId, log.UsersCreated, log.UsersUpdated, log.UsersDisabled, errors.Count);
        }
        catch (Exception ex)
        {
            log.Status = DirectorySyncStatus.Failed;
            log.CompletedAt = DateTime.UtcNow;
            errors.Add($"Sync failed: {ex.Message}");
            log.Errors = JsonSerializer.Serialize(errors);

            config.LastSyncStatus = "Failed";
            config.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync(ct);

            _logger.LogError(ex, "Full sync failed for config {ConfigId}", configId);
        }

        return log;
    }

    public async Task<DirectorySyncLog> RunDeltaSyncAsync(Guid configId, CancellationToken ct = default)
    {
        var config = await _context.DirectorySyncConfigs.FindAsync(new object[] { configId }, ct);
        if (config == null)
        {
            throw new InvalidOperationException("Configuration not found");
        }

        var log = new DirectorySyncLog
        {
            ConfigId = configId,
            SyncType = DirectorySyncType.Delta,
            Status = DirectorySyncStatus.Running
        };

        _context.DirectorySyncLogs.Add(log);
        await _context.SaveChangesAsync(ct);

        var errors = new List<string>();

        try
        {
            var attributeMapping = ParseAttributeMapping(config.AttributeMapping);
            var groupToRoleMapping = ParseGroupToRoleMapping(config.GroupToRoleMapping);

            var ldapAttributes = attributeMapping.Values
                .Concat(new[] { "dn", "memberOf", "whenChanged" })
                .Distinct()
                .ToArray();

            // Build delta filter: combine original filter with whenChanged >= lastSync
            var deltaFilter = config.SearchFilter;
            if (config.LastSyncAt.HasValue)
            {
                var lastSyncLdap = config.LastSyncAt.Value.ToString("yyyyMMddHHmmss.0Z");
                deltaFilter = $"(&{config.SearchFilter}(whenChanged>={lastSyncLdap}))";
            }

            using var connection = CreateLdapConnection(config);
            connection.Bind(new NetworkCredential(config.BindDn, config.BindPassword));

            var searchRequest = new SearchRequest(
                config.SearchBase,
                deltaFilter,
                SearchScope.Subtree,
                ldapAttributes);

            var response = (SearchResponse)connection.SendRequest(searchRequest);

            foreach (SearchResultEntry entry in response.Entries)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var result = await SyncUserFromEntry(entry, config.TenantId, attributeMapping, groupToRoleMapping, ct);
                    if (result.created) log.UsersCreated++;
                    else if (result.updated) log.UsersUpdated++;
                }
                catch (Exception ex)
                {
                    var dn = entry.DistinguishedName;
                    errors.Add($"Error syncing {dn}: {ex.Message}");
                    _logger.LogWarning(ex, "Error syncing LDAP entry {DN} during delta sync", dn);
                }
            }

            log.GroupsSynced = groupToRoleMapping.Count;
            log.Status = errors.Count > 0 ? DirectorySyncStatus.PartialSuccess : DirectorySyncStatus.Success;
            log.CompletedAt = DateTime.UtcNow;
            log.Errors = errors.Count > 0 ? JsonSerializer.Serialize(errors) : null;

            config.LastSyncAt = DateTime.UtcNow;
            config.LastSyncStatus = log.Status.ToString();
            config.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Delta sync completed for config {ConfigId}: {Created} created, {Updated} updated, {Errors} errors",
                configId, log.UsersCreated, log.UsersUpdated, errors.Count);
        }
        catch (Exception ex)
        {
            log.Status = DirectorySyncStatus.Failed;
            log.CompletedAt = DateTime.UtcNow;
            errors.Add($"Sync failed: {ex.Message}");
            log.Errors = JsonSerializer.Serialize(errors);

            config.LastSyncStatus = "Failed";
            config.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync(ct);

            _logger.LogError(ex, "Delta sync failed for config {ConfigId}", configId);
        }

        return log;
    }

    public async Task<List<DirectorySyncLog>> GetSyncLogsAsync(Guid configId, int limit = 50, CancellationToken ct = default)
    {
        return await _context.DirectorySyncLogs
            .Where(l => l.ConfigId == configId)
            .OrderByDescending(l => l.StartedAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<List<DirectorySyncConfig>> GetConfigsDueForSyncAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        return await _context.DirectorySyncConfigs
            .Where(c => c.IsActive && c.SyncInterval > 0)
            .Where(c => c.LastSyncAt == null ||
                        c.LastSyncAt.Value.AddMinutes(c.SyncInterval) <= now)
            .ToListAsync(ct);
    }

    // ── Private Helpers ──────────────────────────────────────────────────

    private static LdapConnection CreateLdapConnection(DirectorySyncConfig config)
    {
        var uri = new Uri(config.LdapUrl);
        var directoryIdentifier = new LdapDirectoryIdentifier(uri.Host, uri.Port);

        var connection = new LdapConnection(directoryIdentifier)
        {
            AuthType = AuthType.Basic,
            Timeout = TimeSpan.FromSeconds(30)
        };

        connection.SessionOptions.ProtocolVersion = 3;

        // Enable SSL/TLS for ldaps:// connections
        if (uri.Scheme.Equals("ldaps", StringComparison.OrdinalIgnoreCase))
        {
            connection.SessionOptions.SecureSocketLayer = true;
        }

        return connection;
    }

    private async Task<(bool created, bool updated, string? email)> SyncUserFromEntry(
        SearchResultEntry entry,
        Guid tenantId,
        Dictionary<string, string> attributeMapping,
        Dictionary<string, Guid> groupToRoleMapping,
        CancellationToken ct)
    {
        var email = GetAttributeValue(entry, attributeMapping.GetValueOrDefault("email", "mail"));
        if (string.IsNullOrWhiteSpace(email))
        {
            return (false, false, null);
        }

        var firstName = GetAttributeValue(entry, attributeMapping.GetValueOrDefault("firstName", "givenName")) ?? "";
        var lastName = GetAttributeValue(entry, attributeMapping.GetValueOrDefault("lastName", "sn")) ?? "";
        var phoneNumber = GetAttributeValue(entry, attributeMapping.GetValueOrDefault("phoneNumber", "telephoneNumber"));

        var existingUser = await _context.Users
            .FirstOrDefaultAsync(u => u.Email == email, ct);

        bool created = false;
        bool updated = false;

        if (existingUser == null)
        {
            // Create new user - LDAP wins (default conflict resolution)
            var user = new User
            {
                Email = email,
                FirstName = firstName,
                LastName = lastName,
                PhoneNumber = phoneNumber,
                PasswordHash = "LDAP_MANAGED", // Placeholder - auth goes through LDAP
                EmailConfirmed = true, // Directory users are pre-verified
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.Users.Add(user);
            await _context.SaveChangesAsync(ct);

            // Assign roles based on group membership
            await SyncUserRolesFromGroups(user.Id, entry, tenantId, groupToRoleMapping, ct);

            created = true;
        }
        else
        {
            // Update existing user - LDAP wins by default (conflict resolution)
            bool hasChanges = false;

            if (existingUser.FirstName != firstName && !string.IsNullOrEmpty(firstName))
            {
                existingUser.FirstName = firstName;
                hasChanges = true;
            }

            if (existingUser.LastName != lastName && !string.IsNullOrEmpty(lastName))
            {
                existingUser.LastName = lastName;
                hasChanges = true;
            }

            if (existingUser.PhoneNumber != phoneNumber)
            {
                existingUser.PhoneNumber = phoneNumber;
                hasChanges = true;
            }

            // Ensure the user is active (re-enable if previously disabled)
            if (!existingUser.IsActive)
            {
                existingUser.IsActive = true;
                hasChanges = true;
            }

            if (hasChanges)
            {
                existingUser.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync(ct);
                updated = true;
            }

            // Always re-sync roles from group membership
            await SyncUserRolesFromGroups(existingUser.Id, entry, tenantId, groupToRoleMapping, ct);
        }

        return (created, updated, email);
    }

    private async Task SyncUserRolesFromGroups(
        Guid userId,
        SearchResultEntry entry,
        Guid tenantId,
        Dictionary<string, Guid> groupToRoleMapping,
        CancellationToken ct)
    {
        if (groupToRoleMapping.Count == 0) return;

        var memberOfAttr = entry.Attributes["memberOf"];
        if (memberOfAttr == null) return;

        var memberOfGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < memberOfAttr.Count; i++)
        {
            memberOfGroups.Add(memberOfAttr[i]?.ToString() ?? "");
        }

        foreach (var (groupDn, roleId) in groupToRoleMapping)
        {
            if (memberOfGroups.Contains(groupDn))
            {
                // Ensure user has this role
                var existingRole = await _context.UserRoles
                    .FirstOrDefaultAsync(ur => ur.UserId == userId && ur.RoleId == roleId && ur.TenantId == tenantId, ct);

                if (existingRole == null)
                {
                    _context.UserRoles.Add(new UserRole
                    {
                        UserId = userId,
                        RoleId = roleId,
                        TenantId = tenantId,
                        GrantedAt = DateTime.UtcNow
                    });
                }
            }
        }

        await _context.SaveChangesAsync(ct);
    }

    private async Task<int> DisableMissingUsersAsync(
        Guid tenantId,
        HashSet<string> directoryEmails,
        CancellationToken ct)
    {
        // Find active users in this tenant who were LDAP-managed but are no longer in the directory
        var usersToDisable = await _context.Users
            .Where(u => u.IsActive && u.PasswordHash == "LDAP_MANAGED")
            .Where(u => !directoryEmails.Contains(u.Email))
            .ToListAsync(ct);

        foreach (var user in usersToDisable)
        {
            user.IsActive = false;
            user.UpdatedAt = DateTime.UtcNow;
        }

        if (usersToDisable.Count > 0)
        {
            await _context.SaveChangesAsync(ct);
        }

        return usersToDisable.Count;
    }

    private static string? GetAttributeValue(SearchResultEntry entry, string? attributeName)
    {
        if (string.IsNullOrWhiteSpace(attributeName)) return null;

        var attr = entry.Attributes[attributeName];
        if (attr == null || attr.Count == 0) return null;

        return attr[0]?.ToString();
    }

    private static Dictionary<string, string> ParseAttributeMapping(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? DefaultAttributeMapping;
        }
        catch (JsonException)
        {
            return DefaultAttributeMapping;
        }
    }

    private static Dictionary<string, Guid> ParseGroupToRoleMapping(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, Guid>>(json) ?? new Dictionary<string, Guid>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, Guid>();
        }
    }
}
