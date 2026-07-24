using System.Security.Cryptography;
using System.Text.Json;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

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
        CancellationToken ct = default)
    {
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

                if (!callerIsSuperAdmin)
                {
                    var targetRole = tenantRoles.FirstOrDefault(r => r.Id == roleId);
                    if (targetRole != null && targetRole.Name.Equals("SuperAdmin", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Errors.Add(new BulkInviteError
                        {
                            Row = i + 1,
                            Email = entry.Email,
                            Error = "Only a SuperAdmin can grant the SuperAdmin role"
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

        // Check if user already exists
        var existingUser = await _context.Users
            .FirstOrDefaultAsync(u => u.Email == invitation.Email, ct);

        User user;

        if (existingUser != null)
        {
            // Link existing user to tenant + role
            user = existingUser;
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
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
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

        // Mark invitation as accepted
        invitation.Status = "Accepted";
        invitation.AcceptedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);

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
