using System.DirectoryServices.Protocols;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Hazina.Security.ApiKeys;
using IAM.Core.Entities;
using IAM.Core.Security;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IAM.Infrastructure.Services;

public class DirectorySyncService : IDirectorySyncService
{
    // Bind passwords are stored as "enc:<secret id>" pointing at an AES-GCM entry in the secrets vault.
    private const string EncryptedPrefix = "enc:";

    // Placeholder password hash of users created by a directory sync (auth goes through LDAP).
    private const string LdapManagedMarker = "LDAP_MANAGED";

    private readonly IAMDbContext _context;
    private readonly ILogger<DirectorySyncService> _logger;
    private readonly ILdapDirectoryClient _ldap;
    private readonly ISecretsVaultService _secretsVault;
    private readonly IHostResolver _resolver;
    private readonly HashSet<string> _allowedHosts;
    private readonly IApiKeyCache? _apiKeyCache;

    // Default LDAP attribute mapping if none specified
    private static readonly Dictionary<string, string> DefaultAttributeMapping = new()
    {
        { "email", "mail" },
        { "firstName", "givenName" },
        { "lastName", "sn" },
        { "phoneNumber", "telephoneNumber" }
    };

    public DirectorySyncService(
        IAMDbContext context,
        ILogger<DirectorySyncService> logger,
        ILdapDirectoryClient ldap,
        ISecretsVaultService secretsVault,
        IHostResolver resolver,
        IConfiguration configuration,
        IApiKeyCache? apiKeyCache = null)
    {
        _apiKeyCache = apiKeyCache;
        _context = context;
        _logger = logger;
        _ldap = ldap;
        _secretsVault = secretsVault;
        _resolver = resolver;
        _allowedHosts = new HashSet<string>(
            configuration.GetSection("DirectorySync:AllowedHosts").Get<string[]>() ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
    }

    // ── Configuration CRUD ───────────────────────────────────────────────

    public async Task<DirectorySyncConfig> CreateConfigAsync(
        DirectorySyncConfig config, bool callerIsSuperAdmin = false, CancellationToken ct = default)
    {
        var tenantExists = await _context.Tenants.AnyAsync(t => t.Id == config.TenantId, ct);
        if (!tenantExists)
        {
            throw new DirectorySyncValidationException("Tenant not found");
        }

        await ValidateAsync(config.TenantId, config.LdapUrl, config.GroupToRoleMapping, callerIsSuperAdmin, ct);

        if (!string.IsNullOrEmpty(config.BindPassword))
            config.BindPassword = await EncryptAsync(config.BindPassword, config.TenantId, ct);

        _context.DirectorySyncConfigs.Add(config);
        await _context.SaveChangesAsync(ct);
        return config;
    }

    public async Task<DirectorySyncConfig?> GetConfigAsync(Guid configId, CancellationToken ct = default)
    {
        return await _context.DirectorySyncConfigs
            .AsNoTracking()
            .Include(c => c.Tenant)
            .FirstOrDefaultAsync(c => c.Id == configId, ct);
    }

    public async Task<Guid?> GetConfigTenantIdAsync(Guid configId, CancellationToken ct = default)
    {
        return await _context.DirectorySyncConfigs
            .AsNoTracking()
            .Where(c => c.Id == configId)
            .Select(c => (Guid?)c.TenantId)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<List<DirectorySyncConfig>> GetConfigsByTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        return await _context.DirectorySyncConfigs
            .AsNoTracking()
            .Include(c => c.Tenant)
            .Where(c => c.TenantId == tenantId)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
    }

    public async Task<DirectorySyncConfig> UpdateConfigAsync(
        DirectorySyncConfig config, bool callerIsSuperAdmin = false, CancellationToken ct = default)
    {
        var existing = await _context.DirectorySyncConfigs.FindAsync(new object[] { config.Id }, ct);
        if (existing == null)
        {
            throw new InvalidOperationException("Directory sync configuration not found");
        }

        await ValidateAsync(existing.TenantId, config.LdapUrl, config.GroupToRoleMapping, callerIsSuperAdmin, ct);

        existing.Name = config.Name;
        existing.LdapUrl = config.LdapUrl;
        existing.BindDn = config.BindDn;

        // Only update password if a new one is provided (non-empty); it is stored encrypted.
        if (!string.IsNullOrWhiteSpace(config.BindPassword))
        {
            existing.BindPassword = await EncryptAsync(config.BindPassword, existing.TenantId, ct);
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

    public async Task<int> EncryptLegacyBindPasswordsAsync(CancellationToken ct = default)
    {
        var legacy = await _context.DirectorySyncConfigs
            .Where(c => c.BindPassword != "" && !c.BindPassword.StartsWith(EncryptedPrefix))
            .ToListAsync(ct);

        foreach (var config in legacy)
            config.BindPassword = await EncryptAsync(config.BindPassword, config.TenantId, ct);

        if (legacy.Count > 0)
            await _context.SaveChangesAsync(ct);

        return legacy.Count;
    }

    // ── Connection test and sync ─────────────────────────────────────────

    public async Task<DirectoryTestResult> TestConnectionAsync(Guid configId, CancellationToken ct = default)
    {
        var config = await _context.DirectorySyncConfigs.FindAsync(new object[] { configId }, ct);
        if (config == null)
        {
            return new DirectoryTestResult { Success = false, Message = "Configuration not found" };
        }

        try
        {
            await EnsureConnectableAsync(config.LdapUrl, ct);
            var bindPassword = await ResolveBindPasswordAsync(config, ct);

            var info = await _ldap.TestAsync(config.LdapUrl, config.BindDn, bindPassword, config.SearchBase, config.SearchFilter, ct);

            return new DirectoryTestResult
            {
                Success = true,
                Message = $"Connection successful. Found {info.EntryCount}+ entries matching filter.",
                UserCount = info.EntryCount,
                ServerType = info.ServerType
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

    public Task<DirectorySyncLog> RunFullSyncAsync(Guid configId, CancellationToken ct = default) =>
        RunSyncAsync(configId, DirectorySyncType.Full, ct);

    public Task<DirectorySyncLog> RunDeltaSyncAsync(Guid configId, CancellationToken ct = default) =>
        RunSyncAsync(configId, DirectorySyncType.Delta, ct);

    private async Task<DirectorySyncLog> RunSyncAsync(Guid configId, DirectorySyncType type, CancellationToken ct)
    {
        var config = await _context.DirectorySyncConfigs.FindAsync(new object[] { configId }, ct);
        if (config == null)
        {
            throw new InvalidOperationException("Configuration not found");
        }

        var log = new DirectorySyncLog
        {
            ConfigId = configId,
            SyncType = type,
            Status = DirectorySyncStatus.Running
        };

        _context.DirectorySyncLogs.Add(log);
        await _context.SaveChangesAsync(ct);

        var errors = new List<string>();

        try
        {
            var attributeMapping = ParseAttributeMapping(config.AttributeMapping);

            // Only tenant roles are ever granted: platform-wide, unknown and foreign roles in the stored
            // mapping are ignored here even if they got into the row some other way.
            var groupToRoleMapping = await LoadAllowedGroupMappingAsync(config, errors, ct);

            var ldapAttributes = attributeMapping.Values
                .Concat(new[] { "dn", "memberOf", "whenChanged" })
                .Distinct()
                .ToArray();

            var filter = config.SearchFilter;
            if (type == DirectorySyncType.Delta && config.LastSyncAt.HasValue)
            {
                var lastSyncLdap = config.LastSyncAt.Value.ToString("yyyyMMddHHmmss.0Z");
                filter = $"(&{config.SearchFilter}(whenChanged>={lastSyncLdap}))";
            }

            await EnsureConnectableAsync(config.LdapUrl, ct);
            var bindPassword = await ResolveBindPasswordAsync(config, ct);

            var entries = await _ldap.SearchAsync(
                config.LdapUrl, config.BindDn, bindPassword, config.SearchBase, filter, ldapAttributes, ct);

            // Track which directory users we've seen (by email) to detect removed users
            var directoryEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var result = await SyncUserFromEntry(entry, config.TenantId, attributeMapping, groupToRoleMapping, ct);
                    if (result.email != null) directoryEmails.Add(result.email);
                    if (result.skipped != null) errors.Add(result.skipped);

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

            // Disable users that no longer exist in the directory (full sync only): this tenant's own
            // LDAP-managed users, never anyone who belongs to another tenant.
            if (type == DirectorySyncType.Full && directoryEmails.Count > 0)
            {
                log.UsersDisabled = await DisableMissingUsersAsync(config.TenantId, directoryEmails, ct);
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
                "{Type} sync completed for config {ConfigId}: {Created} created, {Updated} updated, {Disabled} disabled, {Errors} errors",
                type, configId, log.UsersCreated, log.UsersUpdated, log.UsersDisabled, errors.Count);
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

            _logger.LogError(ex, "{Type} sync failed for config {ConfigId}", type, configId);
        }

        return log;
    }

    public async Task<List<DirectorySyncLog>> GetSyncLogsAsync(Guid configId, int limit = 50, CancellationToken ct = default)
    {
        return await _context.DirectorySyncLogs
            .AsNoTracking()
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

    // ── Validation, URL guard, secret handling ───────────────────────────

    private async Task ValidateAsync(
        Guid tenantId, string ldapUrl, string groupToRoleMappingJson, bool callerIsSuperAdmin, CancellationToken ct)
    {
        var uri = ParseLdapUrl(ldapUrl);

        // Outside SuperAdmin the host must be public (blocks probing of internal services). SuperAdmin may
        // point at an internal directory; running it against a private address additionally needs the host on
        // DirectorySync:AllowedHosts (see EnsureConnectableAsync).
        if (!callerIsSuperAdmin)
            await RequirePublicHostAsync(uri.IdnHost, ct);

        await ValidateGroupMappingAsync(tenantId, groupToRoleMappingJson, ct);
    }

    private static Uri ParseLdapUrl(string ldapUrl)
    {
        if (!Uri.TryCreate(ldapUrl, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            throw new DirectorySyncValidationException("LDAP URL must be a valid absolute ldaps:// URL");

        if (!uri.Scheme.Equals("ldaps", StringComparison.OrdinalIgnoreCase))
            throw new DirectorySyncValidationException("LDAP URL must use ldaps:// (plain ldap:// is not allowed)");

        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new DirectorySyncValidationException("LDAP URL must not contain credentials");

        return uri;
    }

    /// <summary>The same checks again when a connection is about to be made (stored URLs, DNS changes).</summary>
    private async Task EnsureConnectableAsync(string ldapUrl, CancellationToken ct)
    {
        var uri = ParseLdapUrl(ldapUrl);
        await RequirePublicHostAsync(uri.IdnHost, ct);
    }

    private async Task RequirePublicHostAsync(string host, CancellationToken ct)
    {
        host = host.Trim().TrimStart('[').TrimEnd(']');
        if (_allowedHosts.Contains(host))
            return;

        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = new[] { literal };
        }
        else
        {
            try
            {
                addresses = await _resolver.ResolveAsync(host, ct);
            }
            catch (Exception ex) when (ex is SocketException or ArgumentException)
            {
                throw new DirectorySyncValidationException("LDAP host could not be resolved");
            }
        }

        if (addresses.Length == 0)
            throw new DirectorySyncValidationException("LDAP host could not be resolved");

        // Every address must be public: a host that ALSO resolves to an internal address is refused.
        if (addresses.Any(a => !PublicAddress.IsPublic(a)))
        {
            throw new DirectorySyncValidationException(
                "LDAP host must be a public internet address (loopback, private and link-local addresses are not allowed)");
        }
    }

    private async Task ValidateGroupMappingAsync(Guid tenantId, string json, CancellationToken ct)
    {
        Dictionary<string, Guid>? mapping;
        try
        {
            mapping = string.IsNullOrWhiteSpace(json) ? new() : JsonSerializer.Deserialize<Dictionary<string, Guid>>(json);
        }
        catch (JsonException)
        {
            throw new DirectorySyncValidationException("Group-to-role mapping must map group DNs to role ids");
        }

        if (mapping == null || mapping.Count == 0)
            return;

        var roleIds = mapping.Values.Distinct().ToList();
        var roles = await _context.Roles.AsNoTracking().Where(r => roleIds.Contains(r.Id)).ToListAsync(ct);

        foreach (var (group, roleId) in mapping)
        {
            var role = roles.FirstOrDefault(r => r.Id == roleId);
            switch (DirectoryRoleMappingRules.Check(role, tenantId))
            {
                case DirectoryRoleProblem.NotFound:
                    throw new DirectorySyncValidationException($"Group '{group}' is mapped to a role that does not exist");
                case DirectoryRoleProblem.PlatformRole:
                    throw new DirectorySyncValidationException(
                        $"Group '{group}' is mapped to the platform-wide role '{role!.Name}'; a directory group can only grant tenant roles");
                case DirectoryRoleProblem.OtherTenantRole:
                    throw new DirectorySyncValidationException($"Group '{group}' is mapped to a role of a different tenant");
            }
        }
    }

    /// <summary>The stored mapping minus every entry that may not be granted; ignored entries are reported in the log.</summary>
    private async Task<Dictionary<string, Guid>> LoadAllowedGroupMappingAsync(
        DirectorySyncConfig config, List<string> errors, CancellationToken ct)
    {
        var mapping = ParseGroupToRoleMapping(config.GroupToRoleMapping);
        if (mapping.Count == 0)
            return mapping;

        var roleIds = mapping.Values.Distinct().ToList();
        var roles = await _context.Roles.AsNoTracking().Where(r => roleIds.Contains(r.Id)).ToListAsync(ct);

        var allowed = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var (group, roleId) in mapping)
        {
            var role = roles.FirstOrDefault(r => r.Id == roleId);
            var problem = DirectoryRoleMappingRules.Check(role, config.TenantId);
            if (problem == DirectoryRoleProblem.None)
            {
                allowed[group] = roleId;
            }
            else
            {
                errors.Add($"Group mapping '{group}' ignored: its role is {problem} and cannot be granted by a directory sync");
                _logger.LogWarning(
                    "Directory sync config {ConfigId}: group mapping {Group} ignored ({Problem})", config.Id, group, problem);
            }
        }

        return allowed;
    }

    private async Task<string> EncryptAsync(string plain, Guid tenantId, CancellationToken ct)
    {
        var entry = await _secretsVault.CreateSecretAsync(
            name: $"directory-bind-password-{Guid.NewGuid():N}",
            plainTextValue: plain,
            tenantId: tenantId,
            secretType: "DirectoryBindPassword",
            ct: ct);
        return EncryptedPrefix + entry.Id;
    }

    /// <summary>
    /// The plain bind password. A value stored in plain text before task 4698 is encrypted on first use.
    /// </summary>
    private async Task<string> ResolveBindPasswordAsync(DirectorySyncConfig config, CancellationToken ct)
    {
        var stored = config.BindPassword;
        if (string.IsNullOrEmpty(stored))
            return string.Empty;

        if (stored.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
        {
            if (!Guid.TryParse(stored[EncryptedPrefix.Length..], out var secretId))
                throw new InvalidOperationException("Bind password reference is malformed");

            return await _secretsVault.GetSecretValueAsync(secretId, ct)
                ?? throw new InvalidOperationException("Bind password could not be decrypted");
        }

        config.BindPassword = await EncryptAsync(stored, config.TenantId, ct);
        await _context.SaveChangesAsync(ct);
        return stored;
    }

    // ── Sync loop (scoped to the config's tenant) ────────────────────────

    private async Task<(bool created, bool updated, string? email, string? skipped)> SyncUserFromEntry(
        DirectoryEntry entry,
        Guid tenantId,
        Dictionary<string, string> attributeMapping,
        Dictionary<string, Guid> groupToRoleMapping,
        CancellationToken ct)
    {
        var email = entry.FirstValue(attributeMapping.GetValueOrDefault("email", "mail"));
        if (string.IsNullOrWhiteSpace(email))
        {
            return (false, false, null, null);
        }

        var firstName = entry.FirstValue(attributeMapping.GetValueOrDefault("firstName", "givenName")) ?? "";
        var lastName = entry.FirstValue(attributeMapping.GetValueOrDefault("lastName", "sn")) ?? "";
        var phoneNumber = entry.FirstValue(attributeMapping.GetValueOrDefault("phoneNumber", "telephoneNumber"));

        // Emails are unique across the whole system, so the lookup is global - but what the sync may do with a
        // match is not (see below).
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
                PasswordHash = LdapManagedMarker, // Placeholder - auth goes through LDAP
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
            // A sync only touches users that belong to its own tenant: a member of this tenant, or an
            // LDAP-managed account that is not a member of any tenant yet. Anyone else - a user of another
            // tenant, or a regular account that merely shares the email - is left completely alone.
            var isMember = await _context.UserRoles.AnyAsync(
                ur => ur.UserId == existingUser.Id && ur.TenantId == tenantId, ct);
            var isLdapManaged = existingUser.PasswordHash == LdapManagedMarker;

            if (!isMember)
            {
                var belongsElsewhere = await _context.UserRoles.AnyAsync(
                    ur => ur.UserId == existingUser.Id && (ur.TenantId == null || ur.TenantId != tenantId), ct);

                if (!isLdapManaged || belongsElsewhere)
                {
                    return (false, false, email,
                        $"User {email} exists outside this tenant and was not changed");
                }
            }

            // Profile data, phone number and the active flag are only managed for LDAP-managed accounts; a
            // regular member keeps its own phone number (SMS-OTP) and activation state.
            if (isLdapManaged)
            {
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
            }

            // Always re-sync roles from group membership
            await SyncUserRolesFromGroups(existingUser.Id, entry, tenantId, groupToRoleMapping, ct);
        }

        return (created, updated, email, null);
    }

    private async Task SyncUserRolesFromGroups(
        Guid userId,
        DirectoryEntry entry,
        Guid tenantId,
        Dictionary<string, Guid> groupToRoleMapping,
        CancellationToken ct)
    {
        if (groupToRoleMapping.Count == 0) return;

        var memberOfGroups = new HashSet<string>(entry.Values("memberOf"), StringComparer.OrdinalIgnoreCase);
        if (memberOfGroups.Count == 0) return;

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

    internal async Task<int> DisableMissingUsersAsync(
        Guid tenantId,
        HashSet<string> directoryEmails,
        CancellationToken ct)
    {
        var presentEmails = directoryEmails.Select(e => e.ToLowerInvariant()).ToList();

        // Active LDAP-managed users of THIS tenant (a role row in it) that are no longer in the directory. A user
        // who also belongs to another tenant (or holds a global role) is not this directory's to disable.
        var usersToDisable = await _context.Users
            .Where(u => u.IsActive && u.PasswordHash == LdapManagedMarker)
            .Where(u => !presentEmails.Contains(u.Email.ToLower()))
            .Where(u => _context.UserRoles.Any(ur => ur.UserId == u.Id && ur.TenantId == tenantId))
            .Where(u => !_context.UserRoles.Any(ur => ur.UserId == u.Id && (ur.TenantId == null || ur.TenantId != tenantId)))
            .ToListAsync(ct);

        foreach (var user in usersToDisable)
        {
            user.IsActive = false;
            user.UpdatedAt = DateTime.UtcNow;
        }

        if (usersToDisable.Count > 0)
        {
            // A user removed from the directory must not keep refreshing an existing session or calling with an API key.
            var disabledIds = usersToDisable.Select(u => u.Id).ToList();
            await _context.RevokeRefreshTokensAsync(disabledIds, ct);
            var revokedKeys = await _context.RevokeApiKeysAsync(disabledIds, ct);
            await _context.SaveChangesAsync(ct);
            _apiKeyCache.Forget(revokedKeys);
        }

        return usersToDisable.Count;
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
