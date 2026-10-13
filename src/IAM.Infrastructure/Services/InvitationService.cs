using System.Security.Cryptography;
using System.Text.Json;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenIddict.EntityFrameworkCore.Models;

namespace IAM.Infrastructure.Services;

public class InvitationService : IInvitationService
{
    private readonly IAMDbContext _context;
    private readonly IEmailService _emailService;
    private readonly ILogger<InvitationService> _logger;
    private readonly int _defaultExpiryDays;
    private readonly string? _emailBaseUrl;

    public InvitationService(
        IAMDbContext context,
        IEmailService emailService,
        IConfiguration configuration,
        ILogger<InvitationService> logger)
    {
        _context = context;
        _emailService = emailService;
        _logger = logger;
        _defaultExpiryDays = configuration.GetValue("Invitations:DefaultExpiryDays", 7);
        _emailBaseUrl = configuration.GetValue<string>("Email:BaseUrl");
    }

    private static string RenderTemplate(string template, Dictionary<string, string> placeholders)
    {
        foreach (var (key, value) in placeholders)
        {
            template = template.Replace(key, value);
        }
        return template;
    }

    public async Task<Invitation> SendInvitationAsync(
        string email,
        Guid tenantId,
        Guid roleId,
        Guid invitedByUserId,
        int? expiryDays = null,
        CancellationToken ct = default)
    {
        // Validate tenant exists
        var tenant = await _context.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct)
            ?? throw new InvalidOperationException("Tenant not found");

        // Validate role exists
        var role = await _context.Roles.FirstOrDefaultAsync(r => r.Id == roleId, ct)
            ?? throw new InvalidOperationException("Role not found");

        // Check organization settings for email domain restrictions
        var orgSettings = await _context.Set<OrganizationSettings>()
            .FirstOrDefaultAsync(os => os.TenantId == tenantId, ct);

        if (orgSettings != null)
        {
            // Check allowed email domains
            var allowedDomains = JsonSerializer.Deserialize<List<string>>(orgSettings.AllowedEmailDomains ?? "[]") ?? new List<string>();
            if (allowedDomains.Count > 0)
            {
                var emailDomain = email.Split('@').LastOrDefault()?.ToLowerInvariant();
                if (emailDomain == null || !allowedDomains.Any(d => d.Equals(emailDomain, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException($"Email domain '{emailDomain}' is not allowed for this organization. Allowed domains: {string.Join(", ", allowedDomains)}");
                }
            }

            // Check max members
            if (orgSettings.MaxMembers > 0)
            {
                var currentMemberCount = await _context.UserRoles
                    .Where(ur => ur.TenantId == tenantId)
                    .Select(ur => ur.UserId)
                    .Distinct()
                    .CountAsync(ct);

                var pendingCount = await _context.Set<Invitation>()
                    .CountAsync(i => i.TenantId == tenantId && i.Status == "Pending", ct);

                if (currentMemberCount + pendingCount >= orgSettings.MaxMembers)
                {
                    throw new InvalidOperationException($"Organization has reached its maximum member limit of {orgSettings.MaxMembers}");
                }
            }
        }

        // Check for existing pending invitation
        var existingInvite = await _context.Set<Invitation>()
            .FirstOrDefaultAsync(i => i.Email == email && i.TenantId == tenantId && i.Status == "Pending", ct);

        if (existingInvite != null)
        {
            // Revoke existing and create new one
            existingInvite.Status = "Revoked";
        }

        // Generate secure token
        var token = GenerateSecureToken();

        var invitation = new Invitation
        {
            Email = email,
            TenantId = tenantId,
            RoleId = roleId,
            Token = token,
            Status = "Pending",
            InvitedByUserId = invitedByUserId,
            ExpiresAt = DateTime.UtcNow.AddDays(expiryDays ?? _defaultExpiryDays)
        };

        _context.Set<Invitation>().Add(invitation);
        await _context.SaveChangesAsync(ct);

        // Send invitation email
        var inviter = await _context.Users.FirstOrDefaultAsync(u => u.Id == invitedByUserId, ct);
        var inviterName = inviter != null ? $"{inviter.FirstName} {inviter.LastName}".Trim() : "A team member";

        var customTemplate = await _context.Set<EmailTemplate>()
            .FirstOrDefaultAsync(t => t.TenantId == tenantId && t.Key == "Invitation" && t.IsActive, ct);

        if (customTemplate != null)
        {
            var inviteUrl = $"{_emailBaseUrl?.TrimEnd('/')}/accept-invite?token={token}";
            var placeholders = new Dictionary<string, string>
            {
                ["{{InviterName}}"] = inviterName,
                ["{{TenantName}}"] = tenant.Name,
                ["{{RoleName}}"] = role.Name,
                ["{{InviteUrl}}"] = inviteUrl,
                // Raw token so app-branded templates can point the accept link at their
                // own accept page (e.g. Jengo Cloud Workspace /plane/accept?token=...)
                // instead of the IAM's generic accept-invite UI (JengoWork 3632).
                ["{{Token}}"] = token,
                ["{{Email}}"] = email
            };

            await _emailService.SendRawEmailAsync(
                email,
                RenderTemplate(customTemplate.Subject, placeholders),
                RenderTemplate(customTemplate.BodyHtml, placeholders),
                ct);
        }
        else
        {
            await _emailService.SendInvitationAsync(email, inviterName, tenant.Name, token, ct, tenantId: tenantId);
        }

        _logger.LogInformation(
            "Invitation sent to {Email} for tenant {TenantName} ({TenantId}) by user {InviterId}",
            email, tenant.Name, tenantId, invitedByUserId);

        return invitation;
    }

    public async Task<BulkInviteResult> SendBulkInvitationsAsync(
        IEnumerable<BulkInviteEntry> entries,
        Guid tenantId,
        Guid invitedByUserId,
        bool callerIsSuperAdmin,
        CancellationToken ct = default,
        bool callerIsTenantOwner = false)
    {
        var grantor = new TenantGrantor(callerIsSuperAdmin, callerIsSuperAdmin || callerIsTenantOwner);
        var result = new BulkInviteResult();
        var entryList = entries.ToList();
        result.TotalProcessed = entryList.Count;

        // Pre-load roles for the tenant to resolve role names
        var tenantRoles = await _context.Roles
            .Where(r => r.TenantId == tenantId || r.TenantId == null)
            .ToListAsync(ct);

        // Get org settings for default role
        var orgSettings = await _context.Set<OrganizationSettings>()
            .FirstOrDefaultAsync(os => os.TenantId == tenantId, ct);

        // The stored default role is only used while it still passes the rule the settings API enforces
        // (exists, non-privileged, global or this tenant's). A row written before that rule existed, or
        // straight into the database, may hold an admin role or another tenant's role (task 4738).
        string? defaultRoleError = null;
        if (orgSettings?.DefaultRoleId != null)
        {
            var storedDefaultRole = await _context.Roles.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == orgSettings.DefaultRoleId.Value, ct);

            defaultRoleError = DefaultRoleRules.Check(storedDefaultRole, tenantId) switch
            {
                DefaultRoleProblem.NotFound => "The organization's default role no longer exists",
                DefaultRoleProblem.Privileged =>
                    $"The organization's default role '{storedDefaultRole!.Name}' is a privileged role and cannot be assigned by bulk invitation",
                DefaultRoleProblem.OtherTenant => "The organization's default role belongs to another tenant",
                _ => null
            };
        }

        for (var i = 0; i < entryList.Count; i++)
        {
            var entry = entryList[i];

            try
            {
                // Resolve role
                Guid roleId;
                if (!string.IsNullOrWhiteSpace(entry.RoleName))
                {
                    var role = tenantRoles.FirstOrDefault(r =>
                        r.Name.Equals(entry.RoleName, StringComparison.OrdinalIgnoreCase));

                    if (role == null)
                    {
                        result.Errors.Add(new BulkInviteError
                        {
                            Row = i + 1,
                            Email = entry.Email,
                            Error = $"Role '{entry.RoleName}' not found"
                        });
                        result.Failed++;
                        continue;
                    }

                    roleId = role.Id;
                }
                else if (orgSettings?.DefaultRoleId != null)
                {
                    if (defaultRoleError != null)
                    {
                        result.Errors.Add(new BulkInviteError
                        {
                            Row = i + 1,
                            Email = entry.Email,
                            Error = defaultRoleError
                        });
                        result.Failed++;
                        continue;
                    }

                    roleId = orgSettings.DefaultRoleId.Value;
                }
                else
                {
                    result.Errors.Add(new BulkInviteError
                    {
                        Row = i + 1,
                        Email = entry.Email,
                        Error = "No role specified and no default role configured"
                    });
                    result.Failed++;
                    continue;
                }

                // The same grant rule as the single invitation and the member role change (task 4700).
                var targetRole = tenantRoles.FirstOrDefault(r => r.Id == roleId);
                if (targetRole != null)
                {
                    var problem = TenantRoleGrantRules.Check(targetRole, tenantId, grantor);
                    if (problem != RoleGrantProblem.None)
                    {
                        result.Errors.Add(new BulkInviteError
                        {
                            Row = i + 1,
                            Email = entry.Email,
                            Error = TenantRoleGrantRules.Describe(problem, targetRole.Name)
                        });
                        result.Failed++;
                        continue;
                    }
                }

                await SendInvitationAsync(entry.Email, tenantId, roleId, invitedByUserId, ct: ct);
                result.Succeeded++;
            }
            catch (Exception ex)
            {
                result.Errors.Add(new BulkInviteError
                {
                    Row = i + 1,
                    Email = entry.Email,
                    Error = ex.Message
                });
                result.Failed++;
            }
        }

        _logger.LogInformation(
            "Bulk invitation completed for tenant {TenantId}: {Succeeded}/{Total} succeeded, {Failed} failed",
            tenantId, result.Succeeded, result.TotalProcessed, result.Failed);

        return result;
    }

    public async Task<InvitationAcceptResult> AcceptInvitationAsync(
        string token,
        string? password,
        string? firstName,
        string? lastName,
        CancellationToken ct = default)
    {
        var invitation = await _context.Set<Invitation>()
            .Include(i => i.Tenant)
            .Include(i => i.Role)
            .FirstOrDefaultAsync(i => i.Token == token, ct);

        if (invitation == null)
        {
            return new InvitationAcceptResult { Success = false, Error = "Invalid invitation token" };
        }

        if (invitation.Status != "Pending")
        {
            return new InvitationAcceptResult { Success = false, Error = $"Invitation is {invitation.Status.ToLowerInvariant()}" };
        }

        if (invitation.ExpiresAt < DateTime.UtcNow)
        {
            invitation.Status = "Expired";
            await _context.SaveChangesAsync(ct);
            return new InvitationAcceptResult { Success = false, Error = "Invitation has expired" };
        }

        // Acceptance cannot bypass the grant rule (task 4700): an invitation created before the rule existed (or by
        // someone who has since lost the authority) must not hand out a platform-wide role. Platform-wide roles
        // need a sender who is a SuperAdmin right now; a role of another tenant is never assigned.
        var senderIsSuperAdmin = await _context.UserRoles.AnyAsync(ur =>
            ur.UserId == invitation.InvitedByUserId
            && ur.Role.Name == "SuperAdmin"
            && (ur.ExpiresAt == null || ur.ExpiresAt > DateTime.UtcNow), ct);
        var acceptProblem = TenantRoleGrantRules.Check(invitation.Role, invitation.TenantId, new TenantGrantor(senderIsSuperAdmin, true));
        if (acceptProblem != RoleGrantProblem.None)
        {
            invitation.Status = "Revoked";
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning(
                "Invitation {InvitationId} for {Email} revoked at acceptance: role {RoleName} may not be granted by its sender ({Problem})",
                invitation.Id, invitation.Email, invitation.Role.Name, acceptProblem);
            return new InvitationAcceptResult { Success = false, Error = "This invitation can no longer be accepted" };
        }

        // Check if user already exists
        var existingUser = await _context.Users
            .FirstOrDefaultAsync(u => u.Email == invitation.Email, ct);

        User user;
        AccountReset? accountReset = null;

        if (existingUser != null)
        {
            // Link existing user to tenant + role
            user = existingUser;

            // Task 5158: an account whose e-mail was never confirmed may have been registered by someone else
            // for this address (e.g. through a social login whose provider does not verify the address). Handing
            // it the invited role would give that person the tenant access. So the person holding the invitation
            // (it was mailed to the real owner) takes the account over: every old way in is removed and a new
            // password is required. Refused BEFORE anything is touched, so a refusal leaves the account as it was.
            if (!existingUser.EmailConfirmed)
            {
                if (string.IsNullOrWhiteSpace(password))
                {
                    return new InvitationAcceptResult
                    {
                        Success = false,
                        Error = "A new password is required to accept this invitation: the e-mail address of the existing account was never confirmed"
                    };
                }

                // Only stages changes; they are saved together with the role grant below.
                accountReset = await ResetUnconfirmedAccountAsync(existingUser, password, firstName, lastName, ct);
            }
        }
        else
        {
            // Create new user
            if (string.IsNullOrWhiteSpace(password))
            {
                return new InvitationAcceptResult { Success = false, Error = "Password is required for new accounts" };
            }

            user = new User
            {
                Email = invitation.Email,
                FirstName = firstName ?? string.Empty,
                LastName = lastName ?? string.Empty,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12),
                EmailConfirmed = true, // Email is verified via invitation
                IsActive = true
            };

            _context.Users.Add(user);
        }

        // Check if user already has this role for this tenant
        var existingAssignment = await _context.UserRoles
            .AnyAsync(ur => ur.UserId == user.Id && ur.RoleId == invitation.RoleId && ur.TenantId == invitation.TenantId, ct);

        if (!existingAssignment)
        {
            var userRole = new UserRole
            {
                UserId = user.Id,
                RoleId = invitation.RoleId,
                TenantId = invitation.TenantId,
                GrantedBy = invitation.InvitedByUserId
            };

            _context.UserRoles.Add(userRole);
        }

        if (accountReset != null)
        {
            _context.AuditLogs.Add(new AuditLog
            {
                UserId = user.Id,
                TenantId = invitation.TenantId,
                Action = "UnconfirmedAccountResetOnInvitation",
                Resource = "User",
                Details = JsonSerializer.Serialize(new
                {
                    invitationId = invitation.Id,
                    externalLogins = accountReset.ExternalLogins,
                    passkeys = accountReset.Passkeys,
                    refreshTokens = accountReset.RefreshTokens,
                    oauthTokensRevoked = accountReset.OAuthTokens,
                    otherSignInMethods = accountReset.OtherSignInMethods
                })
            });
        }

        // Mark invitation as accepted
        invitation.Status = "Accepted";
        invitation.AcceptedAt = DateTime.UtcNow;

        // One save: the account cleanup (if any), the role grant and the invitation status land together or not at all.
        await _context.SaveChangesAsync(ct);

        if (accountReset != null)
        {
            _logger.LogWarning(
                "Invitation {InvitationId}: unconfirmed account {UserId} ({Email}) was reset before granting role {RoleName} - removed {ExternalLogins} social login(s), {Passkeys} passkey(s), {RefreshTokens} refresh token(s), revoked {OAuthTokens} OAuth token(s), {OtherSignInMethods} other sign-in item(s)",
                invitation.Id, user.Id, user.Email, invitation.Role.Name, accountReset.ExternalLogins, accountReset.Passkeys,
                accountReset.RefreshTokens, accountReset.OAuthTokens, accountReset.OtherSignInMethods);
        }

        // Get welcome message from org settings
        var orgSettings = await _context.Set<OrganizationSettings>()
            .FirstOrDefaultAsync(os => os.TenantId == invitation.TenantId, ct);

        // Send welcome email
        var username = $"{user.FirstName} {user.LastName}".Trim();
        if (string.IsNullOrWhiteSpace(username)) username = user.Email;
        await _emailService.SendWelcomeEmailAsync(user.Email, username, invitation.Tenant.Name, ct, tenantId: invitation.TenantId);

        _logger.LogInformation(
            "Invitation accepted: {Email} joined tenant {TenantName} ({TenantId}) with role {RoleName}",
            invitation.Email, invitation.Tenant.Name, invitation.TenantId, invitation.Role.Name);

        return new InvitationAcceptResult
        {
            Success = true,
            User = user,
            WelcomeMessage = orgSettings?.WelcomeMessage,
            MfaSetupRequired = orgSettings?.RequireMfa == true && !user.TwoFactorEnabled
        };
    }

    private sealed record AccountReset(int ExternalLogins, int Passkeys, int RefreshTokens, int OAuthTokens, int OtherSignInMethods);

    /// <summary>
    /// Stages (does not save) the take-over of an existing account whose e-mail was never confirmed: every way of
    /// signing in or staying signed in that the account had is removed or revoked, and the password becomes the
    /// accepter's. Roles and group memberships are left alone; only the credentials go. The caller saves the
    /// result together with the role grant.
    /// </summary>
    private async Task<AccountReset> ResetUnconfirmedAccountAsync(
        User user, string newPassword, string? firstName, string? lastName, CancellationToken ct)
    {
        var userId = user.Id;
        var now = DateTime.UtcNow;

        // Social logins (the attacker's way back in), passkeys and the login refresh tokens.
        var externalLogins = await _context.ExternalLogins.Where(e => e.UserId == userId).ToListAsync(ct);
        var passkeys = await _context.Credentials.Where(c => c.UserId == userId).ToListAsync(ct);
        var refreshTokens = await _context.RefreshTokens.Where(t => t.UserId == userId).ToListAsync(ct);
        _context.ExternalLogins.RemoveRange(externalLogins);
        _context.Credentials.RemoveRange(passkeys);
        _context.RefreshTokens.RemoveRange(refreshTokens);

        // OAuth/OIDC refresh tokens and consents issued to apps for this subject. Their roles are rebuilt from the
        // database on every refresh, so a surviving one would pick up the role granted below.
        var subject = userId.ToString();
        var revoked = OpenIddict.Abstractions.OpenIddictConstants.Statuses.Revoked;
        var oauthTokens = await _context.Set<OpenIddictEntityFrameworkCoreToken>()
            .Where(t => t.Subject == subject && t.Status != revoked).ToListAsync(ct);
        var oauthAuthorizations = await _context.Set<OpenIddictEntityFrameworkCoreAuthorization>()
            .Where(a => a.Subject == subject && a.Status != revoked).ToListAsync(ct);
        foreach (var token in oauthTokens) token.Status = revoked;
        foreach (var authorization in oauthAuthorizations) authorization.Status = revoked;

        // Other saved sign-in methods: 2FA recovery codes, remembered devices, unused magic-link / one-time codes
        // (including codes addressed to the old phone number), API keys owned by the account, open sessions.
        var recoveryCodes = await _context.RecoveryCodes.Where(r => r.UserId == userId).ToListAsync(ct);
        var trustedDevices = await _context.TrustedDevices.Where(d => d.UserId == userId).ToListAsync(ct);
        var magicLinks = await _context.MagicLinkTokens.Where(m => m.UserId == userId && m.UsedAt == null).ToListAsync(ct);
        var oldPhone = user.PhoneNumber;
        var otpCodes = await _context.OtpCodes
            .Where(o => o.UsedAt == null && (o.UserId == userId || (oldPhone != null && o.PhoneNumber == oldPhone)))
            .ToListAsync(ct);
        var apiKeys = await _context.ApiKeys.Where(k => k.UserId == userId && k.IsActive).ToListAsync(ct);
        var sessions = await _context.UserSessions.Where(s => s.UserId == userId && !s.IsRevoked).ToListAsync(ct);
        _context.RecoveryCodes.RemoveRange(recoveryCodes);
        _context.TrustedDevices.RemoveRange(trustedDevices);
        _context.MagicLinkTokens.RemoveRange(magicLinks);
        _context.OtpCodes.RemoveRange(otpCodes);
        foreach (var apiKey in apiKeys) apiKey.IsActive = false;
        foreach (var session in sessions)
        {
            session.IsRevoked = true;
            session.RevokedAt = now;
            session.RevokedReason = "invitation_account_reset";
        }

        // The account row itself: new password, no leftover tokens, 2FA secret, phone number (SMS one-time-code
        // login finds the account by it) or lockout state from the previous holder. The e-mail is now proven by the
        // invitation, which was mailed to it.
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword, workFactor: 12);
        user.EmailConfirmed = true;
        user.EmailVerificationToken = null;
        user.EmailVerificationTokenExpiry = null;
        user.PasswordResetToken = null;
        user.PasswordResetTokenExpiry = null;
        user.TwoFactorEnabled = false;
        user.TwoFactorSecret = null;
        user.TwoFactorMethod = TwoFactorMethod.None;
        user.PhoneNumber = null;
        user.AvatarUrl = null;
        user.FailedLoginAttempts = 0;
        user.IsLockedOut = false;
        user.LockoutEnd = null;
        if (!string.IsNullOrWhiteSpace(firstName)) user.FirstName = firstName;
        if (!string.IsNullOrWhiteSpace(lastName)) user.LastName = lastName;
        user.UpdatedAt = now;

        return new AccountReset(
            externalLogins.Count,
            passkeys.Count,
            refreshTokens.Count,
            oauthTokens.Count + oauthAuthorizations.Count,
            recoveryCodes.Count + trustedDevices.Count + magicLinks.Count + otpCodes.Count + apiKeys.Count + sessions.Count);
    }

    public async Task<bool> RevokeInvitationAsync(Guid invitationId, CancellationToken ct = default)
    {
        var invitation = await _context.Set<Invitation>()
            .FirstOrDefaultAsync(i => i.Id == invitationId && i.Status == "Pending", ct);

        if (invitation == null)
        {
            return false;
        }

        invitation.Status = "Revoked";
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Invitation {InvitationId} revoked for {Email}", invitationId, invitation.Email);
        return true;
    }

    public async Task<List<Invitation>> GetInvitationsByTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        return await _context.Set<Invitation>()
            .Include(i => i.Role)
            .Include(i => i.InvitedByUser)
            .Where(i => i.TenantId == tenantId)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<List<Invitation>> GetPendingInvitationsAsync(Guid tenantId, CancellationToken ct = default)
    {
        return await _context.Set<Invitation>()
            .Include(i => i.Role)
            .Include(i => i.InvitedByUser)
            .Where(i => i.TenantId == tenantId && i.Status == "Pending" && i.ExpiresAt > DateTime.UtcNow)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<Invitation?> GetInvitationByTokenAsync(string token, CancellationToken ct = default)
    {
        return await _context.Set<Invitation>()
            .Include(i => i.Tenant)
            .Include(i => i.Role)
            .Include(i => i.InvitedByUser)
            .FirstOrDefaultAsync(i => i.Token == token, ct);
    }

    private static string GenerateSecureToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }
}
