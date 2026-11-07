using System.Security.Cryptography;
using System.Text;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IAM.Infrastructure.Services;

public class OtpService : IOtpService
{
    private readonly IAMDbContext _context;
    private readonly IEmailService _emailService;
    private readonly ISmsService _smsService;
    private readonly EmailSettings _emailSettings;
    private readonly ILogger<OtpService> _logger;

    private const int CodeLength = 6;
    private const int ExpiryMinutes = 10;
    private const int MaxAttemptsPerCode = 5;
    private const int MaxSmsPerPhonePerHour = 10;
    private const int MaxEmailPerAddressPerHour = 10;
    private const int MaxRequestsPerIpPerHour = 30;
    private static readonly TimeSpan ThrottleWindow = TimeSpan.FromHours(1);
    private readonly IRequestThrottle _throttle;

    public OtpService(
        IAMDbContext context,
        IEmailService emailService,
        ISmsService smsService,
        IOptions<EmailSettings> emailSettings,
        ILogger<OtpService> logger,
        IRequestThrottle? throttle = null)
    {
        _throttle = throttle ?? new InMemoryRequestThrottle();
        _context = context;
        _emailService = emailService;
        _smsService = smsService;
        _emailSettings = emailSettings.Value;
        _logger = logger;
    }

    public async Task<bool> SendEmailOtpAsync(string email, OtpPurpose purpose, string? ipAddress = null)
    {
        // Rate limit per address and per source IP. Counted for every request, whether or not an
        // account exists, and an over-limit request is silently dropped (still returns true) so the
        // response never reveals whether the address has an account.
        if (!WithinLimits("otp-email", email.Trim().ToLowerInvariant(), MaxEmailPerAddressPerHour, ipAddress))
        {
            _logger.LogWarning("Rate limit exceeded for email OTP requests; send skipped");
            return true;
        }

        // Find user by email (optional for OTP - some flows don't require existing user)
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == email);

        // For login purpose, user must exist
        if (purpose == OtpPurpose.Login && user == null)
        {
            // Don't reveal if email exists
            _logger.LogDebug("Email OTP requested for non-existent email {Email}", email);
            return true;
        }

        if (purpose == OtpPurpose.Login && user != null && !user.IsActive)
        {
            _logger.LogDebug("Email OTP requested for inactive user {Email}", email);
            return true;
        }

        var code = GenerateNumericCode();
        var codeHash = HashCode(code);

        var otpCode = new OtpCode
        {
            UserId = user?.Id,
            Email = email,
            Code = codeHash,
            Purpose = purpose,
            ExpiresAt = DateTime.UtcNow.AddMinutes(ExpiryMinutes),
            MaxAttempts = MaxAttemptsPerCode
        };

        _context.OtpCodes.Add(otpCode);
        await _context.SaveChangesAsync();

        // Send email with the plain code
        var username = user?.FirstName ?? "User";
        await _emailService.SendMfaCodeAsync(email, username, code);

        _logger.LogInformation("Email OTP sent to {Email} for purpose {Purpose}", email, purpose);
        return true;
    }

    public async Task<bool> SendSmsOtpAsync(string phoneNumber, OtpPurpose purpose, string? ipAddress = null)
    {
        var normalized = PhoneNumberNormalizer.Normalize(phoneNumber);
        if (normalized == null) return true;

        if (!WithinLimits("otp-sms", normalized, MaxSmsPerPhonePerHour, ipAddress))
        {
            _logger.LogWarning("Rate limit exceeded for SMS OTP requests; send skipped");
            return true;
        }

        // Only a VERIFIED number identifies an account. Unknown, unverified and ambiguous numbers all
        // take the same silent path so the response never reveals which case applied.
        var matches = await _context.Users.Where(u => u.VerifiedPhoneNumber == normalized).Take(2).ToListAsync();
        var user = matches.Count == 1 ? matches[0] : null;

        if (purpose == OtpPurpose.Login && (user == null || !user.IsActive))
        {
            _logger.LogDebug("SMS login OTP not sent: no single active account with this verified number");
            return true;
        }

        await IssueSmsCodeAsync(user?.Id, normalized, purpose);
        return true;
    }

    public async Task<PhoneVerificationSendResult> SendPhoneVerificationAsync(User user, string? ipAddress = null)
    {
        var normalized = PhoneNumberNormalizer.Normalize(user.PhoneNumber);
        if (!PhoneNumberNormalizer.IsValid(normalized))
            return PhoneVerificationSendResult.InvalidNumber;

        if (await _context.Users.AnyAsync(u => u.Id != user.Id && u.VerifiedPhoneNumber == normalized))
            return PhoneVerificationSendResult.AlreadyVerifiedElsewhere;

        if (!WithinLimits("otp-sms", normalized!, MaxSmsPerPhonePerHour, ipAddress))
            return PhoneVerificationSendResult.RateLimited;

        await IssueSmsCodeAsync(user.Id, normalized!, OtpPurpose.PhoneVerification);
        return PhoneVerificationSendResult.Sent;
    }

    public async Task<PhoneVerificationConfirmResult> ConfirmPhoneVerificationAsync(User user, string code)
    {
        var normalized = PhoneNumberNormalizer.Normalize(user.PhoneNumber);
        if (!PhoneNumberNormalizer.IsValid(normalized))
            return PhoneVerificationConfirmResult.InvalidNumber;

        if (await _context.Users.AnyAsync(u => u.Id != user.Id && u.VerifiedPhoneNumber == normalized))
            return PhoneVerificationConfirmResult.AlreadyVerifiedElsewhere;

        if (!await ValidateOtpAsync(null, normalized, code, OtpPurpose.PhoneVerification))
            return PhoneVerificationConfirmResult.InvalidCode;

        user.MarkPhoneVerified(normalized!);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Lost a race with another account verifying the same number: the unique index decides.
            user.VerifiedPhoneNumber = null;
            user.PhoneVerifiedAt = null;
            return PhoneVerificationConfirmResult.AlreadyVerifiedElsewhere;
        }

        return PhoneVerificationConfirmResult.Verified;
    }

    private async Task IssueSmsCodeAsync(Guid? userId, string normalizedPhone, OtpPurpose purpose)
    {
        var code = GenerateNumericCode();
        var otpCode = new OtpCode
        {
            UserId = userId,
            PhoneNumber = normalizedPhone,
            Code = HashCode(code),
            Purpose = purpose,
            ExpiresAt = DateTime.UtcNow.AddMinutes(ExpiryMinutes),
            MaxAttempts = MaxAttemptsPerCode
        };

        _context.OtpCodes.Add(otpCode);
        await _context.SaveChangesAsync();

        var message = $"Your IAM System verification code is: {code}. It expires in {ExpiryMinutes} minutes.";
        await _smsService.SendSmsAsync(normalizedPhone, message);

        _logger.LogInformation("SMS OTP sent for purpose {Purpose}", purpose);
    }

    private bool WithinLimits(string channel, string subject, int perSubjectLimit, string? ipAddress)
    {
        // Evaluate both so each counter records the request regardless of the other's outcome.
        var subjectOk = _throttle.TryAcquire($"{channel}:addr:{subject}", perSubjectLimit, ThrottleWindow);
        var ipOk = string.IsNullOrEmpty(ipAddress)
            || _throttle.TryAcquire($"{channel}:ip:{ipAddress}", MaxRequestsPerIpPerHour, ThrottleWindow);
        return subjectOk && ipOk;
    }

    public async Task<bool> ValidateOtpAsync(string? email, string? phoneNumber, string code, OtpPurpose purpose)
    {
        if (string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(phoneNumber))
        {
            return false;
        }

        var codeHash = HashCode(code);
        if (string.IsNullOrWhiteSpace(email))
        {
            phoneNumber = PhoneNumberNormalizer.Normalize(phoneNumber);
            if (phoneNumber == null) return false;
        }

        // Find the most recent matching OTP
        var query = _context.OtpCodes
            .Where(c => c.Purpose == purpose && c.UsedAt == null && c.ExpiresAt > DateTime.UtcNow);

        if (!string.IsNullOrWhiteSpace(email))
        {
            query = query.Where(c => c.Email == email);
        }
        else
        {
            query = query.Where(c => c.PhoneNumber == phoneNumber);
        }

        var otpCode = await query
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync();

        if (otpCode == null)
        {
            _logger.LogDebug("OTP validation failed: no matching code found");
            return false;
        }

        var otpId = otpCode.Id;

        if (!_context.Database.IsRelational())
        {
            // Non-relational providers (EF InMemory, unit tests only) cannot run ExecuteUpdateAsync.
            // Production always uses Postgres and takes the atomic path below.
            return await ValidateNonAtomicAsync(otpCode, codeHash, email ?? phoneNumber, purpose);
        }

        // Count this guess atomically: the WHERE clause is the limit, so parallel guesses can never
        // push Attempts past MaxAttempts. 0 rows updated = limit reached or code used/expired meanwhile.
        var now = DateTime.UtcNow;
        var counted = await _context.OtpCodes
            .Where(c => c.Id == otpId && c.UsedAt == null && c.ExpiresAt > now && c.Attempts < c.MaxAttempts)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Attempts, c => c.Attempts + 1));

        if (counted == 0)
        {
            _logger.LogWarning("OTP validation failed: max attempts exceeded or code no longer valid");
            return false;
        }

        if (otpCode.Code != codeHash)
        {
            _logger.LogDebug("OTP validation failed: code mismatch");
            return false;
        }

        // Consume the code exactly once: only the request that flips UsedAt from NULL wins.
        var consumed = await _context.OtpCodes
            .Where(c => c.Id == otpId && c.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedAt, DateTime.UtcNow));

        if (consumed == 0)
        {
            _logger.LogWarning("OTP validation failed: code already used by a concurrent request");
            return false;
        }

        _logger.LogInformation("OTP validated successfully for {Target} purpose {Purpose}",
            email ?? phoneNumber, purpose);
        return true;
    }

    private async Task<bool> ValidateNonAtomicAsync(OtpCode otpCode, string codeHash, string? target, OtpPurpose purpose)
    {
        if (otpCode.Attempts >= otpCode.MaxAttempts)
        {
            return false;
        }

        otpCode.Attempts++;

        if (otpCode.Code != codeHash)
        {
            await _context.SaveChangesAsync();
            return false;
        }

        otpCode.UsedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        _logger.LogInformation("OTP validated successfully for {Target} purpose {Purpose}", target, purpose);
        return true;
    }

    public async Task<bool> SendLoginTwoFactorCodeAsync(User user, string? returnUrl = null)
    {
        // Rate limit: max per email per hour (shared budget with other email OTP purposes)
        var oneHourAgo = DateTime.UtcNow.AddHours(-1);
        var recentCount = await _context.OtpCodes
            .CountAsync(c => c.Email == user.Email && c.Purpose == OtpPurpose.LoginTwoFactor && c.CreatedAt >= oneHourAgo);

        if (recentCount >= MaxEmailPerAddressPerHour)
        {
            _logger.LogWarning("Rate limit exceeded for login 2FA code requests: {Email}", user.Email);
            return false;
        }

        var code = GenerateNumericCode();
        var codeHash = HashCode(code);

        var otpCode = new OtpCode
        {
            UserId = user.Id,
            Email = user.Email,
            Code = codeHash,
            Purpose = OtpPurpose.LoginTwoFactor,
            ExpiresAt = DateTime.UtcNow.AddMinutes(ExpiryMinutes),
            MaxAttempts = MaxAttemptsPerCode
        };

        _context.OtpCodes.Add(otpCode);
        await _context.SaveChangesAsync();

        var baseUrl = _emailSettings.BaseUrl?.TrimEnd('/');
        var verifyUrl = $"{baseUrl}/verify-2fa?userId={user.Id}&code={code}";
        var safeReturnUrl = SanitizeReturnUrl(returnUrl);
        if (!string.IsNullOrWhiteSpace(safeReturnUrl))
        {
            verifyUrl += $"&returnUrl={Uri.EscapeDataString(safeReturnUrl)}";
        }

        await _emailService.SendLoginTwoFactorCodeAsync(user.Email, user.FirstName, code, verifyUrl);

        _logger.LogInformation("Login 2FA code sent to {Email}", user.Email);
        return true;
    }

    public async Task<int> CleanupExpiredCodesAsync()
    {
        var expiredCodes = await _context.OtpCodes
            .Where(c => c.ExpiresAt < DateTime.UtcNow || c.UsedAt != null)
            .Where(c => c.CreatedAt < DateTime.UtcNow.AddDays(-1))
            .ToListAsync();

        _context.OtpCodes.RemoveRange(expiredCodes);
        await _context.SaveChangesAsync();

        if (expiredCodes.Count > 0)
        {
            _logger.LogInformation("Cleaned up {Count} expired OTP codes", expiredCodes.Count);
        }

        return expiredCodes.Count;
    }

    private static string GenerateNumericCode()
    {
        // Generate cryptographically random 6-digit code
        var randomBytes = new byte[4];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomBytes);
        var number = BitConverter.ToUInt32(randomBytes, 0) % 1_000_000;
        return number.ToString("D6");
    }

    private static string HashCode(string code)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(code));
        return Convert.ToBase64String(hashBytes);
    }

    /// <summary>
    /// Validates a returnUrl to prevent open redirect attacks.
    /// Only allows relative URLs starting with / (not protocol-relative or absolute).
    /// </summary>
    private static string? SanitizeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl)) return null;
        // Only allow relative URLs starting with /
        if (!returnUrl.StartsWith("/") || returnUrl.StartsWith("//") || returnUrl.Contains("://"))
            return null;
        return returnUrl;
    }
}
