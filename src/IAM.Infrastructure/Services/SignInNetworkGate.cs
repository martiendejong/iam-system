using System.Net;
using System.Text.Json;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IAM.Infrastructure.Services;

/// <inheritdoc cref="ISignInNetworkGate"/>
public class SignInNetworkGate : ISignInNetworkGate
{
    private readonly IAMDbContext _context;
    private readonly ILogger<SignInNetworkGate> _logger;

    public SignInNetworkGate(IAMDbContext context, ILogger<SignInNetworkGate> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<bool> IsAllowedAsync(Guid userId, string? ipAddress, string? userAgent = null, Guid? tenantId = null, CancellationToken cancellationToken = default)
    {
        List<Guid> tenantIds;
        if (tenantId.HasValue)
        {
            tenantIds = new List<Guid> { tenantId.Value };
        }
        else
        {
            tenantIds = await _context.UserRoles
                .Where(ur => ur.UserId == userId && ur.TenantId != null)
                .WhereActive()
                .Select(ur => ur.TenantId!.Value)
                .Distinct()
                .ToListAsync(cancellationToken);
        }

        if (tenantIds.Count == 0) return true;

        var entries = await _context.IpAllowlistEntries
            .Where(e => e.IsActive && tenantIds.Contains(e.TenantId))
            .Select(e => new { e.TenantId, e.Cidr })
            .ToListAsync(cancellationToken);

        if (entries.Count == 0) return true;

        var normalizedIp = Normalize(ipAddress);
        var refusingTenants = entries
            .GroupBy(e => e.TenantId)
            .Where(g => normalizedIp == null || !g.Any(e => CidrMatcher.Contains(normalizedIp, e.Cidr)))
            .Select(g => g.Key)
            .ToList();

        if (refusingTenants.Count == 0) return true;

        var shownIp = normalizedIp ?? ipAddress ?? "unknown";
        foreach (var refusing in refusingTenants)
        {
            _context.BlockedIpLogs.Add(new BlockedIpLog
            {
                TenantId = refusing,
                IpAddress = shownIp,
                Reason = "IP is not in the tenant IP allowlist (sign-in / token request)"
            });
            _context.AuditLogs.Add(new AuditLog
            {
                UserId = userId,
                TenantId = refusing,
                Action = "SignInBlockedByIpAllowlist",
                Resource = "Tenant",
                Details = JsonSerializer.Serialize(new { ipAddress = shownIp }),
                IpAddress = ipAddress,
                UserAgent = userAgent
            });
        }
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogWarning("Sign-in refused for user {UserId} from {IpAddress}: not in the IP allowlist of tenant(s) {Tenants}",
            userId, shownIp, string.Join(",", refusingTenants));
        return false;
    }

    private static string? Normalize(string? ipAddress)
    {
        if (string.IsNullOrWhiteSpace(ipAddress) || !IPAddress.TryParse(ipAddress.Trim(), out var ip)) return null;
        return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4().ToString() : ip.ToString();
    }
}
