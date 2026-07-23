using System.Text.Json;
using System.Text.Json.Serialization;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IAM.Infrastructure.Services;

public class DataRequestService : IDataRequestService
{
    private readonly IAMDbContext _context;
    private readonly IEmailService _emailService;
    private readonly ILogger<DataRequestService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReferenceHandler = ReferenceHandler.IgnoreCycles
    };

    public DataRequestService(IAMDbContext context, IEmailService emailService, ILogger<DataRequestService> logger)
    {
        _context = context;
        _emailService = emailService;
        _logger = logger;
    }

    public async Task<DataRequest> CreateExportRequestAsync(Guid userId, CancellationToken ct = default)
    {
        var request = new DataRequest
        {
            UserId = userId,
            Type = DataRequestType.Export,
            Status = DataRequestStatus.Pending,
            RequestedAt = DateTime.UtcNow
        };

        _context.DataRequests.Add(request);

        _context.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            Action = "DataExportRequested",
            Resource = "DataRequest",
            Details = JsonSerializer.Serialize(new { requestId = request.Id, type = "Export" })
        });

        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Data export request {RequestId} created for user {UserId}", request.Id, userId);

        return request;
    }

    public async Task<DataRequest> CreateDeletionRequestAsync(Guid userId, string? reason, CancellationToken ct = default)
    {
        var request = new DataRequest
        {
            UserId = userId,
            Type = DataRequestType.Deletion,
            Status = DataRequestStatus.Pending,
            RequestedAt = DateTime.UtcNow,
            Notes = reason
        };

        _context.DataRequests.Add(request);

        _context.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            Action = "DataDeletionRequested",
            Resource = "DataRequest",
            Details = JsonSerializer.Serialize(new { requestId = request.Id, type = "Deletion", reason })
        });

        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Data deletion request {RequestId} created for user {UserId}", request.Id, userId);

        return request;
    }

    public async Task<DataRequest> ProcessExportAsync(Guid requestId, CancellationToken ct = default)
    {
        var request = await _context.DataRequests
            .FirstOrDefaultAsync(r => r.Id == requestId, ct)
            ?? throw new InvalidOperationException($"Data request {requestId} not found");

        if (request.Status != DataRequestStatus.Pending)
            throw new InvalidOperationException($"Data request {requestId} is not in Pending status");

        request.Status = DataRequestStatus.Processing;
        await _context.SaveChangesAsync(ct);

        var userId = request.UserId;

        // Gather ALL user data across all entities
        var user = await _context.Users
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        var userRoles = await _context.UserRoles
            .Where(ur => ur.UserId == userId)
            .Select(ur => new { ur.Id, ur.RoleId, ur.TenantId, ur.GrantedAt })
            .ToListAsync(ct);

        var groupMemberships = await _context.GroupMemberships
            .Where(gm => gm.UserId == userId)
            .Select(gm => new { gm.Id, gm.GroupId, gm.Role, gm.IsActive, gm.JoinedAt })
            .ToListAsync(ct);

        var sessions = await _context.UserSessions
            .Where(s => s.UserId == userId)
            .Select(s => new { s.Id, s.IpAddress, s.DeviceInfo, s.CreatedAt, s.LastActivityAt, s.ExpiresAt, s.IsRevoked })
            .ToListAsync(ct);

        var auditLogs = await _context.AuditLogs
            .Where(a => a.UserId == userId)
            .Select(a => new { a.Id, a.Action, a.Resource, a.Details, a.IpAddress, a.CreatedAt })
            .OrderByDescending(a => a.CreatedAt)
            .Take(1000) // Limit to last 1000 entries for practical export size
            .ToListAsync(ct);

        var credentials = await _context.Credentials
            .Where(c => c.UserId == userId)
            .Select(c => new { c.Id, c.CredType, c.Name, c.DeviceType, c.CreatedAt, c.LastUsedAt })
            .ToListAsync(ct);

        var refreshTokens = await _context.RefreshTokens
            .Where(rt => rt.UserId == userId)
            .Select(rt => new { rt.Id, rt.CreatedAt, rt.ExpiresAt, rt.IsRevoked })
            .ToListAsync(ct);

        var recoveryCodes = await _context.RecoveryCodes
            .Where(rc => rc.UserId == userId)
            .Select(rc => new { rc.Id, rc.CreatedAt, rc.IsUsed, rc.UsedAt })
            .ToListAsync(ct);

        var apiKeys = await _context.ApiKeys
            .Where(ak => ak.UserId == userId)
            .Select(ak => new { ak.Id, ak.Name, ak.KeyPrefix, ak.IsActive, ak.CreatedAt, ak.ExpiresAt, ak.LastUsedAt })
            .ToListAsync(ct);

        var consentRecords = await _context.ConsentRecords
            .Where(c => c.UserId == userId)
            .Select(c => new { c.Id, c.ClientId, c.Scopes, c.GrantedAt, c.RevokedAt })
            .ToListAsync(ct);

        var dataRequests = await _context.DataRequests
            .Where(dr => dr.UserId == userId)
            .Select(dr => new { dr.Id, dr.Type, dr.Status, dr.RequestedAt, dr.CompletedAt })
            .ToListAsync(ct);

        var exportData = new
        {
            exportedAt = DateTime.UtcNow,
            userId,
            personalInformation = user != null ? new
            {
                user.Email,
                user.FirstName,
                user.LastName,
                user.PhoneNumber,
                user.EmailConfirmed,
                user.TwoFactorEnabled,
                user.IsActive,
                user.CreatedAt,
                user.UpdatedAt,
                user.LastLoginAt
            } : null,
            roles = userRoles,
            groupMemberships,
            sessions,
            auditLogs,
            credentials,
            refreshTokens,
            recoveryCodes,
            apiKeys,
            consentRecords,
            dataRequests
        };

        var jsonExport = JsonSerializer.Serialize(exportData, JsonOptions);

        // Store the export as a base64-encoded data URL
        // In production, this would be stored in blob storage with a signed URL
        var base64Data = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(jsonExport));
        request.DataUrl = $"data:application/json;base64,{base64Data}";
        request.ExpiresAt = DateTime.UtcNow.AddHours(48);
        request.Status = DataRequestStatus.Completed;
        request.CompletedAt = DateTime.UtcNow;

        _context.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            Action = "DataExportCompleted",
            Resource = "DataRequest",
            Details = JsonSerializer.Serialize(new { requestId, expiresAt = request.ExpiresAt })
        });

        await _context.SaveChangesAsync(ct);

        // Notify user that export is ready
        if (user != null)
        {
            try
            {
                await _emailService.SendEmailVerificationAsync(
                    user.Email,
                    user.FirstName,
                    $"Your data export is ready for download. It will be available for 48 hours.",
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send export notification email to user {UserId}", userId);
            }
        }

        _logger.LogInformation("Data export request {RequestId} completed for user {UserId}", requestId, userId);

        return request;
    }

    public async Task<DataRequest> ProcessDeletionAsync(Guid requestId, Guid processedBy, CancellationToken ct = default)
    {
        var request = await _context.DataRequests
            .FirstOrDefaultAsync(r => r.Id == requestId, ct)
            ?? throw new InvalidOperationException($"Data request {requestId} not found");

        if (request.Status != DataRequestStatus.Pending)
            throw new InvalidOperationException($"Data request {requestId} is not in Pending status");

        request.Status = DataRequestStatus.Processing;
        request.ProcessedBy = processedBy;
        await _context.SaveChangesAsync(ct);

        var userId = request.UserId;
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user == null)
        {
            request.Status = DataRequestStatus.Rejected;
            request.Notes = "User not found";
            request.CompletedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);
            return request;
        }

        var userEmail = user.Email; // Save for notification before anonymization

        // GDPR Anonymization: anonymize user data, do NOT hard delete (audit trail integrity)
        var anonymizedGuid = Guid.NewGuid().ToString("N")[..12];

        user.FirstName = "Deleted User";
        user.LastName = string.Empty;
        user.Email = $"deleted-{anonymizedGuid}@anonymized.local";
        user.PhoneNumber = null;
        user.PasswordHash = string.Empty;
        user.IsActive = false;
        user.TwoFactorEnabled = false;
        user.TwoFactorSecret = null;
        user.EmailVerificationToken = null;
        user.PasswordResetToken = null;
        user.PasswordResetTokenExpiry = null;
        user.EmailVerificationTokenExpiry = null;
        user.UpdatedAt = DateTime.UtcNow;

        // Revoke all active sessions
        var activeSessions = await _context.UserSessions
            .Where(s => s.UserId == userId && !s.IsRevoked)
            .ToListAsync(ct);

        foreach (var session in activeSessions)
        {
            session.IsRevoked = true;
            session.RevokedAt = DateTime.UtcNow;
            session.RevokedReason = "gdpr_deletion";
        }

        // Revoke all refresh tokens
        var activeTokens = await _context.RefreshTokens
            .Where(rt => rt.UserId == userId && rt.RevokedAt == null)
            .ToListAsync(ct);

        foreach (var token in activeTokens)
        {
            token.RevokedAt = DateTime.UtcNow;
        }

        // Remove credentials (passkeys) - these are authentication material, not audit data
        var credentials = await _context.Credentials
            .Where(c => c.UserId == userId)
            .ToListAsync(ct);
        _context.Credentials.RemoveRange(credentials);

        // Remove recovery codes
        var recoveryCodes = await _context.RecoveryCodes
            .Where(rc => rc.UserId == userId)
            .ToListAsync(ct);
        _context.RecoveryCodes.RemoveRange(recoveryCodes);

        // Deactivate API keys
        var apiKeys = await _context.ApiKeys
            .Where(ak => ak.UserId == userId)
            .ToListAsync(ct);
        foreach (var key in apiKeys)
        {
            key.IsActive = false;
        }

        // Revoke all active consents
        var activeConsents = await _context.ConsentRecords
            .Where(c => c.UserId == userId && c.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var consent in activeConsents)
        {
            consent.RevokedAt = DateTime.UtcNow;
        }

        // Complete the request
        request.Status = DataRequestStatus.Completed;
        request.CompletedAt = DateTime.UtcNow;
        request.Notes = $"User data anonymized. Sessions revoked: {activeSessions.Count}. Tokens revoked: {activeTokens.Count}. Credentials removed: {credentials.Count}. API keys deactivated: {apiKeys.Count}. Consents revoked: {activeConsents.Count}.";

        _context.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            Action = "DataDeletionCompleted",
            Resource = "DataRequest",
            Details = JsonSerializer.Serialize(new
            {
                requestId,
                processedBy,
                anonymizedEmail = user.Email,
                sessionsRevoked = activeSessions.Count,
                tokensRevoked = activeTokens.Count,
                credentialsRemoved = credentials.Count,
                apiKeysDeactivated = apiKeys.Count,
                consentsRevoked = activeConsents.Count
            })
        });

        await _context.SaveChangesAsync(ct);

        // Try to notify user at original email
        try
        {
            await _emailService.SendEmailVerificationAsync(
                userEmail,
                "User",
                "Your account data has been anonymized as requested. This action is irreversible.",
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send deletion notification email for user {UserId}", userId);
        }

        _logger.LogInformation("Data deletion request {RequestId} completed for user {UserId}. Processed by {ProcessedBy}",
            requestId, userId, processedBy);

        return request;
    }

    public async Task<List<DataRequest>> GetRequestsAsync(Guid? userId, CancellationToken ct = default)
    {
        var query = _context.DataRequests.AsQueryable();

        if (userId.HasValue)
            query = query.Where(r => r.UserId == userId.Value);

        return await query
            .OrderByDescending(r => r.RequestedAt)
            .ToListAsync(ct);
    }

    public async Task<List<DataRequest>> GetPendingRequestsAsync(CancellationToken ct = default)
    {
        return await _context.DataRequests
            .Where(r => r.Status == DataRequestStatus.Pending)
            .OrderBy(r => r.RequestedAt)
            .ToListAsync(ct);
    }
}
