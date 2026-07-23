using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IAM.Infrastructure.Services;

public class ConsentService : IConsentService
{
    private readonly IAMDbContext _context;
    private readonly ILogger<ConsentService> _logger;

    public ConsentService(IAMDbContext context, ILogger<ConsentService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<ConsentRecord> GrantConsentAsync(Guid userId, string clientId, string scopes, string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        // Revoke any existing active consent for this client (replace with new consent)
        var existing = await _context.ConsentRecords
            .FirstOrDefaultAsync(c => c.UserId == userId && c.ClientId == clientId && c.RevokedAt == null, ct);

        if (existing != null)
        {
            existing.RevokedAt = DateTime.UtcNow;
            _logger.LogInformation("Revoked previous consent for user {UserId} on client {ClientId} (replaced)", userId, clientId);
        }

        var consent = new ConsentRecord
        {
            UserId = userId,
            ClientId = clientId,
            Scopes = scopes,
            GrantedAt = DateTime.UtcNow,
            IpAddress = ipAddress,
            UserAgent = userAgent
        };

        _context.ConsentRecords.Add(consent);

        // Audit log
        _context.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            Action = "ConsentGranted",
            Resource = "ConsentRecord",
            Details = System.Text.Json.JsonSerializer.Serialize(new { clientId, scopes }),
            IpAddress = ipAddress,
            UserAgent = userAgent
        });

        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Consent granted for user {UserId} on client {ClientId} with scopes {Scopes}",
            userId, clientId, scopes);

        return consent;
    }

    public async Task<bool> RevokeConsentAsync(Guid userId, string clientId, CancellationToken ct = default)
    {
        var consent = await _context.ConsentRecords
            .FirstOrDefaultAsync(c => c.UserId == userId && c.ClientId == clientId && c.RevokedAt == null, ct);

        if (consent == null)
            return false;

        consent.RevokedAt = DateTime.UtcNow;

        // Audit log
        _context.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            Action = "ConsentRevoked",
            Resource = "ConsentRecord",
            Details = System.Text.Json.JsonSerializer.Serialize(new { clientId, scopes = consent.Scopes })
        });

        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Consent revoked for user {UserId} on client {ClientId}", userId, clientId);

        return true;
    }

    public async Task<bool> HasConsentAsync(Guid userId, string clientId, string scopes, CancellationToken ct = default)
    {
        var consent = await _context.ConsentRecords
            .FirstOrDefaultAsync(c => c.UserId == userId && c.ClientId == clientId && c.RevokedAt == null, ct);

        if (consent == null)
            return false;

        // Check if all requested scopes are covered by the granted scopes
        var grantedScopes = consent.Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var requestedScopes = scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return requestedScopes.All(s => grantedScopes.Contains(s));
    }

    public async Task<List<ConsentRecord>> GetConsentsAsync(Guid userId, CancellationToken ct = default)
    {
        return await _context.ConsentRecords
            .Where(c => c.UserId == userId && c.RevokedAt == null)
            .OrderByDescending(c => c.GrantedAt)
            .ToListAsync(ct);
    }

    public async Task<ConsentRecord?> GetConsentForClientAsync(Guid userId, string clientId, CancellationToken ct = default)
    {
        return await _context.ConsentRecords
            .FirstOrDefaultAsync(c => c.UserId == userId && c.ClientId == clientId && c.RevokedAt == null, ct);
    }
}
