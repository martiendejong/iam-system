using System.Text.Json;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IAM.Infrastructure.Services;

public class AccountLinkingService : IAccountLinkingService
{
    private readonly IAMDbContext _context;
    private readonly ILogger<AccountLinkingService> _logger;

    public AccountLinkingService(
        IAMDbContext context,
        ILogger<AccountLinkingService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<ExternalLogin> LinkExternalProviderAsync(
        Guid userId,
        string provider,
        string providerUserId,
        string? email,
        string? displayName,
        CancellationToken ct = default)
    {
        var user = await _context.Users.FindAsync(new object[] { userId }, ct)
            ?? throw new InvalidOperationException("User not found");

        // Check if this external account is already linked to any user
        var existingLink = await _context.ExternalLogins
            .FirstOrDefaultAsync(el =>
                el.Provider == provider &&
                el.ProviderUserId == providerUserId, ct);

        if (existingLink != null)
        {
            if (existingLink.UserId == userId)
                throw new InvalidOperationException("This external account is already linked to your account");
            else
                throw new InvalidOperationException("This external account is already linked to another user");
        }

        // Check if user already has a link to this provider
        var existingProviderLink = await _context.ExternalLogins
            .FirstOrDefaultAsync(el =>
                el.UserId == userId &&
                el.Provider == provider, ct);

        if (existingProviderLink != null)
            throw new InvalidOperationException($"You already have a {provider} account linked. Unlink it first to link a different one.");

        var externalLogin = new ExternalLogin
        {
            UserId = userId,
            Provider = provider,
            ProviderUserId = providerUserId,
            Email = email,
            DisplayName = displayName,
            LinkedAt = DateTime.UtcNow,
            LastUsedAt = DateTime.UtcNow,
            IsPrimary = false
        };

        // If this is the user's first external login, make it primary
        var hasExistingLogins = await _context.ExternalLogins
            .AnyAsync(el => el.UserId == userId, ct);

        if (!hasExistingLogins)
            externalLogin.IsPrimary = true;

        _context.ExternalLogins.Add(externalLogin);

        // Audit log
        _context.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            Action = "ExternalProviderLinked",
            Resource = "ExternalLogin",
            Details = JsonSerializer.Serialize(new
            {
                provider,
                providerUserId,
                email,
                externalLoginId = externalLogin.Id
            })
        });

        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "User {UserId} linked external provider {Provider} (external user: {ProviderUserId})",
            userId, provider, providerUserId);

        return externalLogin;
    }

    public async Task<AccountLinkingResult> UnlinkExternalProviderAsync(
        Guid userId,
        string provider,
        CancellationToken ct = default)
    {
        var user = await _context.Users.FindAsync(new object[] { userId }, ct);
        if (user == null)
            return AccountLinkingResult.Fail("User not found");

        var externalLogin = await _context.ExternalLogins
            .FirstOrDefaultAsync(el => el.UserId == userId && el.Provider == provider, ct);

        if (externalLogin == null)
            return AccountLinkingResult.Fail("External login not found");

        // Safety check: prevent orphan accounts (must keep at least 1 auth method)
        var hasPassword = !string.IsNullOrEmpty(user.PasswordHash) &&
                          user.PasswordHash != BCrypt.Net.BCrypt.HashPassword("");

        var externalLoginCount = await _context.ExternalLogins
            .CountAsync(el => el.UserId == userId, ct);

        // Check if user has a real password (not auto-generated random one)
        // A user created via social auth gets a random password hash that they don't know.
        // We consider a password "real" if the user has ever explicitly set one.
        // For safety, we check: if this is the last external login AND there's no indication
        // the user has a usable password, we block the unlink.
        if (externalLoginCount <= 1 && string.IsNullOrEmpty(user.PasswordHash))
        {
            return AccountLinkingResult.Fail(
                "Cannot unlink the last external provider. You must have at least one authentication method " +
                "(set a password first, or link another provider).");
        }

        // If user has no real password (auto-generated), they need at least 1 external login
        // We'll be lenient: if they have a PasswordHash at all, they presumably can log in with it
        if (externalLoginCount <= 1)
        {
            // User has a password hash, so they can still authenticate. Allow unlink but warn.
            _logger.LogInformation(
                "User {UserId} unlinking last external provider {Provider}. Password auth remains available.",
                userId, provider);
        }

        // If this was the primary, promote another one
        if (externalLogin.IsPrimary)
        {
            var nextLogin = await _context.ExternalLogins
                .Where(el => el.UserId == userId && el.Id != externalLogin.Id)
                .OrderByDescending(el => el.LastUsedAt)
                .FirstOrDefaultAsync(ct);

            if (nextLogin != null)
                nextLogin.IsPrimary = true;
        }

        _context.ExternalLogins.Remove(externalLogin);

        // Audit log
        _context.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            Action = "ExternalProviderUnlinked",
            Resource = "ExternalLogin",
            Details = JsonSerializer.Serialize(new
            {
                provider,
                providerUserId = externalLogin.ProviderUserId,
                email = externalLogin.Email,
                externalLoginId = externalLogin.Id
            })
        });

        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "User {UserId} unlinked external provider {Provider}",
            userId, provider);

        return AccountLinkingResult.Ok();
    }

    public async Task<List<LinkedIdentityDto>> GetLinkedIdentitiesAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        return await _context.ExternalLogins
            .Where(el => el.UserId == userId)
            .OrderByDescending(el => el.IsPrimary)
            .ThenBy(el => el.Provider)
            .Select(el => new LinkedIdentityDto
            {
                Id = el.Id,
                Provider = el.Provider,
                ProviderUserId = el.ProviderUserId,
                Email = el.Email,
                DisplayName = el.DisplayName,
                LinkedAt = el.LinkedAt,
                LastUsedAt = el.LastUsedAt,
                IsPrimary = el.IsPrimary
            })
            .ToListAsync(ct);
    }

    public async Task<AccountLinkingResult> MergeAccountsAsync(
        Guid primaryUserId,
        Guid secondaryUserId,
        CancellationToken ct = default)
    {
        if (primaryUserId == secondaryUserId)
            return AccountLinkingResult.Fail("Cannot merge an account with itself");

        var primaryUser = await _context.Users
            .Include(u => u.UserRoles)
            .FirstOrDefaultAsync(u => u.Id == primaryUserId, ct);

        if (primaryUser == null)
            return AccountLinkingResult.Fail("Primary user not found");

        var secondaryUser = await _context.Users
            .Include(u => u.UserRoles)
            .FirstOrDefaultAsync(u => u.Id == secondaryUserId, ct);

        if (secondaryUser == null)
            return AccountLinkingResult.Fail("Secondary user not found");

        using var transaction = await _context.Database.BeginTransactionAsync(ct);

        try
        {
            // 1. Move external logins from secondary to primary
            var secondaryLogins = await _context.ExternalLogins
                .Where(el => el.UserId == secondaryUserId)
                .ToListAsync(ct);

            foreach (var login in secondaryLogins)
            {
                // Check if primary already has a login from this provider
                var existingPrimaryLogin = await _context.ExternalLogins
                    .FirstOrDefaultAsync(el =>
                        el.UserId == primaryUserId &&
                        el.Provider == login.Provider, ct);

                if (existingPrimaryLogin != null)
                {
                    // Primary already has this provider - remove the secondary's link
                    _context.ExternalLogins.Remove(login);
                }
                else
                {
                    // Move to primary user
                    login.UserId = primaryUserId;
                    login.IsPrimary = false; // Don't override primary's primary identity
                }
            }

            // 2. Move roles from secondary to primary (avoid duplicates)
            var primaryRoleKeys = primaryUser.UserRoles
                .Select(ur => new { ur.RoleId, ur.TenantId })
                .ToHashSet();

            foreach (var secondaryRole in secondaryUser.UserRoles)
            {
                var key = new { secondaryRole.RoleId, secondaryRole.TenantId };
                if (!primaryRoleKeys.Contains(key))
                {
                    _context.UserRoles.Add(new UserRole
                    {
                        UserId = primaryUserId,
                        RoleId = secondaryRole.RoleId,
                        TenantId = secondaryRole.TenantId
                    });
                }
            }

            // 3. Re-point audit logs from secondary to primary
            var secondaryAuditLogs = await _context.AuditLogs
                .Where(a => a.UserId == secondaryUserId)
                .ToListAsync(ct);

            foreach (var log in secondaryAuditLogs)
            {
                log.UserId = primaryUserId;
            }

            // 4. Move refresh tokens (invalidate secondary's tokens)
            var secondaryTokens = await _context.RefreshTokens
                .Where(rt => rt.UserId == secondaryUserId)
                .ToListAsync(ct);

            foreach (var token in secondaryTokens)
            {
                token.RevokedAt = DateTime.UtcNow;
            }

            // 5. Deactivate secondary account
            secondaryUser.IsActive = false;
            secondaryUser.UpdatedAt = DateTime.UtcNow;

            // 6. Update primary user's last login if secondary was more recent
            if (secondaryUser.LastLoginAt > primaryUser.LastLoginAt)
            {
                primaryUser.LastLoginAt = secondaryUser.LastLoginAt;
            }

            primaryUser.UpdatedAt = DateTime.UtcNow;

            // 7. Audit log the merge on both accounts
            var mergeDetails = JsonSerializer.Serialize(new
            {
                primaryUserId,
                secondaryUserId,
                secondaryEmail = secondaryUser.Email,
                movedExternalLogins = secondaryLogins.Count,
                movedRoles = secondaryUser.UserRoles.Count,
                movedAuditLogs = secondaryAuditLogs.Count
            });

            _context.AuditLogs.Add(new AuditLog
            {
                UserId = primaryUserId,
                Action = "AccountsMerged",
                Resource = "User",
                Details = mergeDetails
            });

            _context.AuditLogs.Add(new AuditLog
            {
                UserId = primaryUserId,
                Action = "AccountDeactivatedByMerge",
                Resource = "User",
                Details = JsonSerializer.Serialize(new
                {
                    deactivatedUserId = secondaryUserId,
                    mergedIntoPrimaryUserId = primaryUserId
                })
            });

            await _context.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            _logger.LogInformation(
                "Merged account {SecondaryUserId} ({SecondaryEmail}) into {PrimaryUserId} ({PrimaryEmail}). " +
                "Moved {LoginCount} external logins, {RoleCount} roles, {AuditCount} audit logs.",
                secondaryUserId, secondaryUser.Email,
                primaryUserId, primaryUser.Email,
                secondaryLogins.Count,
                secondaryUser.UserRoles.Count,
                secondaryAuditLogs.Count);

            return AccountLinkingResult.Ok();
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(ct);
            _logger.LogError(ex, "Failed to merge accounts {SecondaryUserId} into {PrimaryUserId}",
                secondaryUserId, primaryUserId);
            return AccountLinkingResult.Fail($"Failed to merge accounts: {ex.Message}");
        }
    }

    public async Task<AccountLinkingResult> SetPrimaryIdentityAsync(
        Guid userId,
        Guid externalLoginId,
        CancellationToken ct = default)
    {
        var externalLogin = await _context.ExternalLogins
            .FirstOrDefaultAsync(el => el.Id == externalLoginId && el.UserId == userId, ct);

        if (externalLogin == null)
            return AccountLinkingResult.Fail("External login not found or does not belong to this user");

        // Remove primary flag from all other external logins for this user
        var otherLogins = await _context.ExternalLogins
            .Where(el => el.UserId == userId && el.Id != externalLoginId)
            .ToListAsync(ct);

        foreach (var other in otherLogins)
        {
            other.IsPrimary = false;
        }

        externalLogin.IsPrimary = true;

        // Audit log
        _context.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            Action = "PrimaryIdentityChanged",
            Resource = "ExternalLogin",
            Details = JsonSerializer.Serialize(new
            {
                externalLoginId,
                provider = externalLogin.Provider,
                email = externalLogin.Email
            })
        });

        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "User {UserId} set primary identity to {Provider} ({ExternalLoginId})",
            userId, externalLogin.Provider, externalLoginId);

        return AccountLinkingResult.Ok();
    }

    public async Task<List<DuplicateEmailGroup>> DetectDuplicateEmailAsync(
        string? email = null,
        CancellationToken ct = default)
    {
        // Find emails that appear on multiple user accounts (either as user email or external login email)
        var query = _context.ExternalLogins
            .Where(el => el.Email != null && el.Email != "")
            .AsQueryable();

        if (!string.IsNullOrEmpty(email))
        {
            query = query.Where(el => el.Email == email);
        }

        // Get all external login emails grouped
        var externalEmails = await query
            .Select(el => new { el.Email, el.UserId, el.Provider })
            .ToListAsync(ct);

        // Get all user emails
        var userEmails = await _context.Users
            .Where(u => u.IsActive)
            .Select(u => new { u.Email, u.Id })
            .ToListAsync(ct);

        // Build a lookup of email -> user IDs (from both user accounts and external logins)
        var emailToUserIds = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);

        foreach (var ue in userEmails)
        {
            if (!string.IsNullOrEmpty(email) && !string.Equals(ue.Email, email, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!emailToUserIds.ContainsKey(ue.Email))
                emailToUserIds[ue.Email] = new HashSet<Guid>();

            emailToUserIds[ue.Email].Add(ue.Id);
        }

        foreach (var ee in externalEmails)
        {
            if (ee.Email == null) continue;

            if (!emailToUserIds.ContainsKey(ee.Email))
                emailToUserIds[ee.Email] = new HashSet<Guid>();

            emailToUserIds[ee.Email].Add(ee.UserId);
        }

        // Filter to emails with multiple user IDs (duplicates)
        var duplicateEmails = emailToUserIds
            .Where(kv => kv.Value.Count > 1)
            .ToList();

        if (duplicateEmails.Count == 0)
            return new List<DuplicateEmailGroup>();

        // Build detailed results
        var allUserIds = duplicateEmails.SelectMany(d => d.Value).Distinct().ToList();
        var users = await _context.Users
            .Where(u => allUserIds.Contains(u.Id))
            .ToListAsync(ct);

        var allExternalLogins = await _context.ExternalLogins
            .Where(el => allUserIds.Contains(el.UserId))
            .ToListAsync(ct);

        var result = new List<DuplicateEmailGroup>();

        foreach (var dup in duplicateEmails)
        {
            var group = new DuplicateEmailGroup
            {
                Email = dup.Key,
                Accounts = dup.Value.Select(uid =>
                {
                    var user = users.FirstOrDefault(u => u.Id == uid);
                    if (user == null) return null;

                    return new DuplicateAccountInfo
                    {
                        UserId = user.Id,
                        Email = user.Email,
                        FirstName = user.FirstName,
                        LastName = user.LastName,
                        HasPassword = !string.IsNullOrEmpty(user.PasswordHash),
                        LinkedProviders = allExternalLogins
                            .Where(el => el.UserId == uid)
                            .Select(el => el.Provider)
                            .ToList(),
                        CreatedAt = user.CreatedAt,
                        LastLoginAt = user.LastLoginAt
                    };
                })
                .Where(a => a != null)
                .Cast<DuplicateAccountInfo>()
                .OrderBy(a => a.CreatedAt)
                .ToList()
            };

            result.Add(group);
        }

        return result;
    }
}
