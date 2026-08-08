using System.Security.Cryptography;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IAM.Infrastructure.Services;

public class MagicLinkService : IMagicLinkService
{
    private readonly IAMDbContext _context;
    private readonly IEmailService _emailService;
    private readonly EmailSettings _emailSettings;
    private readonly ILogger<MagicLinkService> _logger;

    private const int TokenLength = 64;
    private const int ExpiryMinutes = 15;
    private const int MaxRequestsPerHour = 5;

    public MagicLinkService(
        IAMDbContext context,
        IEmailService emailService,
        IOptions<EmailSettings> emailSettings,
        ILogger<MagicLinkService> logger)
    {
        _context = context;
        _emailService = emailService;
        _emailSettings = emailSettings.Value;
        _logger = logger;
    }

    public async Task<bool> SendMagicLinkAsync(string email, MagicLinkPurpose purpose, string? ipAddress = null, string? returnUrl = null)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user == null)
        {
            // Don't reveal if email exists
            _logger.LogDebug("Magic link requested for non-existent email {Email}", email);
            return true;
        }

        if (!user.IsActive)
        {
            _logger.LogDebug("Magic link requested for inactive user {Email}", email);
            return true;
        }

        // Rate limit: max 5 per email per hour
        var oneHourAgo = DateTime.UtcNow.AddHours(-1);
        var recentCount = await _context.MagicLinkTokens
            .CountAsync(t => t.UserId == user.Id && t.CreatedAt >= oneHourAgo);

        if (recentCount >= MaxRequestsPerHour)
        {
            _logger.LogWarning("Rate limit exceeded for magic link requests: {Email}", email);
            return false;
        }

        // Generate cryptographically random token
        var token = GenerateSecureToken();

        var magicLinkToken = new MagicLinkToken
        {
            UserId = user.Id,
            Token = token,
            Purpose = purpose,
            ExpiresAt = DateTime.UtcNow.AddMinutes(ExpiryMinutes),
            IpAddress = ipAddress
        };

        _context.MagicLinkTokens.Add(magicLinkToken);
        await _context.SaveChangesAsync();

        // Build magic link URL and send email
        var baseUrl = _emailSettings.BaseUrl?.TrimEnd('/');
        var magicLinkUrl = $"{baseUrl}/magic-link?token={token}";
        if (!string.IsNullOrWhiteSpace(returnUrl))
        {
            magicLinkUrl += $"&returnUrl={Uri.EscapeDataString(returnUrl)}";
        }

        await _emailService.SendMfaCodeAsync(email, user.FirstName, magicLinkUrl);

        _logger.LogInformation("Magic link sent to {Email} for purpose {Purpose}", email, purpose);
        return true;
    }

    public async Task<User?> ValidateMagicLinkAsync(string token)
    {
        var magicLinkToken = await _context.MagicLinkTokens
            .Include(t => t.User)
                .ThenInclude(u => u.UserRoles)
                    .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(t => t.Token == token);

        if (magicLinkToken == null)
        {
            _logger.LogDebug("Magic link validation failed: token not found");
            return null;
        }

        if (magicLinkToken.UsedAt != null)
        {
            _logger.LogWarning("Magic link validation failed: token already used");
            return null;
        }

        if (magicLinkToken.ExpiresAt < DateTime.UtcNow)
        {
            _logger.LogDebug("Magic link validation failed: token expired");
            return null;
        }

        // Mark as used
        magicLinkToken.UsedAt = DateTime.UtcNow;

        // Update user last login
        magicLinkToken.User.LastLoginAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        _logger.LogInformation("Magic link validated for user {UserId}", magicLinkToken.UserId);
        return magicLinkToken.User;
    }

    public async Task<int> CleanupExpiredTokensAsync()
    {
        var expiredTokens = await _context.MagicLinkTokens
            .Where(t => t.ExpiresAt < DateTime.UtcNow || t.UsedAt != null)
            .Where(t => t.CreatedAt < DateTime.UtcNow.AddDays(-1))
            .ToListAsync();

        _context.MagicLinkTokens.RemoveRange(expiredTokens);
        await _context.SaveChangesAsync();

        if (expiredTokens.Count > 0)
        {
            _logger.LogInformation("Cleaned up {Count} expired magic link tokens", expiredTokens.Count);
        }

        return expiredTokens.Count;
    }

    private static string GenerateSecureToken()
    {
        var randomBytes = new byte[TokenLength / 2]; // Each byte = 2 hex chars
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomBytes);
        return Convert.ToHexString(randomBytes).ToLowerInvariant();
    }
}
