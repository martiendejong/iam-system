using System.Security.Cryptography;
using System.Text;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IAM.Infrastructure.Services;

public class TotpService : ITotpService
{
    // Stored secrets are "enc:<vault secret id>" pointing at an AES-GCM entry in the secrets vault
    // (same pattern as SocialAuthService). Values without the prefix are legacy plain-text base32.
    internal const string EncryptedPrefix = "enc:";
    internal const string VaultSecretType = "TotpSecret";

    private readonly IAMDbContext _context;
    private readonly ISecretsVaultService _secretsVault;
    private readonly ILogger<TotpService> _logger;

    // RFC 6238 defaults
    private const int SecretLength = 20; // 160 bits
    private const int CodeDigits = 6;
    private const int TimeStepSeconds = 30;
    private const int ClockSkewSteps = 1; // Allow +/- 1 time step (30s) for clock drift
    private const int RecoveryCodeLength = 8;

    // Base32 alphabet (RFC 4648)
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public TotpService(IAMDbContext context, ISecretsVaultService secretsVault, ILogger<TotpService>? logger = null)
    {
        _context = context;
        _secretsVault = secretsVault;
        _logger = logger ?? NullLogger<TotpService>.Instance;
    }

    public TotpSetupResult GenerateSetupInfo(string email, string issuer = "IAM System")
    {
        var secretBytes = new byte[SecretLength];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(secretBytes);

        var base32Secret = Base32Encode(secretBytes);

        // Build otpauth:// URI per Google Authenticator key URI format
        // https://github.com/google/google-authenticator/wiki/Key-Uri-Format
        var encodedIssuer = Uri.EscapeDataString(issuer);
        var encodedEmail = Uri.EscapeDataString(email);
        // Note: existing users must re-enroll after this change (SHA-1 → SHA-256 is a breaking change for authenticator apps).
        var qrCodeUri = $"otpauth://totp/{encodedIssuer}:{encodedEmail}?secret={base32Secret}&issuer={encodedIssuer}&algorithm=SHA256&digits={CodeDigits}&period={TimeStepSeconds}";

        return new TotpSetupResult
        {
            Secret = base32Secret,
            QrCodeUri = qrCodeUri,
            ManualEntryKey = FormatManualEntryKey(base32Secret)
        };
    }

    public bool ValidateCode(string secret, string code) => FindMatchingStep(secret, code) != null;

    /// <summary>
    /// Returns the time step the code belongs to (current step +/- clock skew), or null when it matches none.
    /// </summary>
    private static long? FindMatchingStep(string? secret, string? code)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(code))
            return null;

        if (code.Length != CodeDigits || !code.All(char.IsDigit))
            return null;

        byte[] secretBytes;
        try { secretBytes = Base32Decode(secret); }
        catch (FormatException) { return null; }

        var currentStep = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / TimeStepSeconds;
        var codeBytes = Encoding.UTF8.GetBytes(code);
        long? match = null;

        // Check every step in the window (newest first, no early exit) so timing does not reveal which step matched.
        for (int offset = ClockSkewSteps; offset >= -ClockSkewSteps; offset--)
        {
            var timeStep = currentStep + offset;
            var expectedCode = GenerateTotpCode(secretBytes, timeStep);

            // Constant-time comparison to prevent timing attacks
            if (CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expectedCode), codeBytes)
                && match == null)
            {
                match = timeStep;
            }
        }

        return match;
    }

    /// <summary>
    /// Resolves the plain base32 secret of a user: decrypts "enc:" values through the vault, passes
    /// legacy plain-text values through. Null when the secret cannot be read.
    /// </summary>
    private async Task<string?> ReadSecretAsync(User user, CancellationToken ct)
    {
        var stored = user.TwoFactorSecret;
        if (string.IsNullOrWhiteSpace(stored))
            return null;

        if (!stored.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
            return stored;

        if (!Guid.TryParse(stored[EncryptedPrefix.Length..], out var secretId))
            return null;

        try
        {
            return await _secretsVault.GetSecretValueAsync(secretId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not decrypt the TOTP secret of user {UserId}", user.Id);
            return null;
        }
    }

    private async Task<string> EncryptSecretAsync(Guid userId, string plainBase32, CancellationToken ct)
    {
        var entry = await _secretsVault.CreateSecretAsync(
            name: $"totp-secret-{userId:N}-{Guid.NewGuid():N}",
            plainTextValue: plainBase32,
            secretType: VaultSecretType,
            createdByUserId: userId,
            ct: ct);
        return EncryptedPrefix + entry.Id;
    }

    private async Task DeleteStoredSecretAsync(string? stored, CancellationToken ct)
    {
        if (stored != null && stored.StartsWith(EncryptedPrefix, StringComparison.Ordinal)
            && Guid.TryParse(stored[EncryptedPrefix.Length..], out var id))
        {
            try { await _secretsVault.DeleteSecretAsync(id, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not delete vault entry {SecretId} of a TOTP secret", id); }
        }
    }

    /// <summary>
    /// Checks the code against the user's secret and burns its time step. Accepts only a step newer than
    /// the last accepted one; the conditional UPDATE makes parallel requests with the same code race on one row.
    /// </summary>
    private async Task<bool> ValidateAndConsumeAsync(User user, string code, CancellationToken ct)
    {
        var secret = await ReadSecretAsync(user, ct);
        var step = FindMatchingStep(secret, code);
        if (step == null)
            return false;

        var stepValue = step.Value;
        if (user.TwoFactorLastUsedStep != null && user.TwoFactorLastUsedStep >= stepValue)
        {
            _logger.LogWarning("Rejected replayed TOTP code for user {UserId}", user.Id);
            return false;
        }

        if (!_context.Database.IsRelational())
        {
            // Non-relational providers (EF InMemory, unit tests only) cannot run ExecuteUpdateAsync.
            user.TwoFactorLastUsedStep = stepValue;
            await _context.SaveChangesAsync(ct);
            return true;
        }

        var userId = user.Id;
        var updated = await _context.Users
            .Where(u => u.Id == userId && (u.TwoFactorLastUsedStep == null || u.TwoFactorLastUsedStep < stepValue))
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.TwoFactorLastUsedStep, stepValue), ct);

        if (updated == 0)
        {
            _logger.LogWarning("Rejected TOTP code already used by a concurrent request for user {UserId}", userId);
            return false;
        }

        return true;
    }

    /// <summary>
    /// One-off, repeatable conversion of legacy plain-text secrets to vault-encrypted ones. Users stay enrolled
    /// (same secret, new storage). Returns the number of secrets converted; 0 on a re-run.
    /// </summary>
    public async Task<int> EncryptLegacySecretsAsync(CancellationToken ct = default)
    {
        var legacy = await _context.Users
            .Where(u => u.TwoFactorSecret != null && u.TwoFactorSecret != "" && !u.TwoFactorSecret.StartsWith(EncryptedPrefix))
            .ToListAsync(ct);

        var converted = 0;
        foreach (var user in legacy)
        {
            var stored = await EncryptSecretAsync(user.Id, user.TwoFactorSecret!, ct);
            user.TwoFactorSecret = stored;
            try
            {
                await _context.SaveChangesAsync(ct);
                converted++;
            }
            catch
            {
                await DeleteStoredSecretAsync(stored, ct);
                throw;
            }
        }

        if (converted > 0)
            _logger.LogInformation("Encrypted {Count} legacy plain-text TOTP secret(s)", converted);
        return converted;
    }

    public async Task<TotpSetupResult> EnableTotpAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _context.Users.FindAsync(new object[] { userId }, ct);
        if (user == null)
            throw new InvalidOperationException("User not found");

        if (user.TwoFactorEnabled)
            throw new InvalidOperationException("TOTP is already enabled for this user");

        // Generate new setup info
        var setupResult = GenerateSetupInfo(user.Email);

        // Store the secret but do NOT enable 2FA yet (pending verification)
        await DeleteStoredSecretAsync(user.TwoFactorSecret, ct); // abandoned earlier setup
        user.TwoFactorSecret = await EncryptSecretAsync(user.Id, setupResult.Secret, ct);
        user.TwoFactorLastUsedStep = null;
        user.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);

        return setupResult;
    }

    public async Task<bool> VerifyAndActivateTotpAsync(Guid userId, string code, CancellationToken ct = default)
    {
        var user = await _context.Users.FindAsync(new object[] { userId }, ct);
        if (user == null)
            return false;

        if (string.IsNullOrWhiteSpace(user.TwoFactorSecret))
            return false;

        if (user.TwoFactorEnabled)
            return false; // Already activated

        // Validate the code against the stored (but not yet activated) secret
        if (!await ValidateAndConsumeAsync(user, code, ct))
            return false;

        // Code is valid - activate TOTP
        user.TwoFactorEnabled = true;
        user.TwoFactorMethod = TwoFactorMethod.Totp;
        user.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DisableTotpAsync(Guid userId, string code, CancellationToken ct = default)
    {
        var user = await _context.Users.FindAsync(new object[] { userId }, ct);
        if (user == null)
            return false;

        if (!user.TwoFactorEnabled || string.IsNullOrWhiteSpace(user.TwoFactorSecret))
            return false;

        // Must provide a valid code to disable (security confirmation)
        if (!await ValidateAndConsumeAsync(user, code, ct))
            return false;

        // Disable TOTP and clear secret
        var oldStored = user.TwoFactorSecret;
        user.TwoFactorEnabled = false;
        user.TwoFactorSecret = null;
        user.TwoFactorLastUsedStep = null;
        user.TwoFactorMethod = TwoFactorMethod.None;
        user.UpdatedAt = DateTime.UtcNow;

        // Remove all recovery codes for this user
        var recoveryCodes = await _context.RecoveryCodes
            .Where(rc => rc.UserId == userId)
            .ToListAsync(ct);
        _context.RecoveryCodes.RemoveRange(recoveryCodes);

        await _context.SaveChangesAsync(ct);
        await DeleteStoredSecretAsync(oldStored, ct);
        return true;
    }

    public async Task<bool> ValidateTotpLoginAsync(Guid userId, string code, CancellationToken ct = default)
    {
        var user = await _context.Users.FindAsync(new object[] { userId }, ct);
        if (user == null)
            return false;

        if (!user.TwoFactorEnabled || string.IsNullOrWhiteSpace(user.TwoFactorSecret))
            return false;

        return await ValidateAndConsumeAsync(user, code, ct);
    }

    public async Task<List<string>> GenerateRecoveryCodesAsync(Guid userId, int count = 8, CancellationToken ct = default)
    {
        var user = await _context.Users.FindAsync(new object[] { userId }, ct);
        if (user == null)
            throw new InvalidOperationException("User not found");

        if (!user.TwoFactorEnabled)
            throw new InvalidOperationException("TOTP must be enabled before generating recovery codes");

        // Remove any existing recovery codes
        var existingCodes = await _context.RecoveryCodes
            .Where(rc => rc.UserId == userId)
            .ToListAsync(ct);
        _context.RecoveryCodes.RemoveRange(existingCodes);

        // Generate new recovery codes
        var plaintextCodes = new List<string>();
        var codeEntities = new List<RecoveryCode>();

        using var rng = RandomNumberGenerator.Create();
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

        for (int i = 0; i < count; i++)
        {
            var codeBytes = new byte[RecoveryCodeLength];
            rng.GetBytes(codeBytes);

            var code = new StringBuilder(RecoveryCodeLength);
            for (int j = 0; j < RecoveryCodeLength; j++)
            {
                code.Append(chars[codeBytes[j] % chars.Length]);
            }

            var plaintextCode = code.ToString();
            plaintextCodes.Add(plaintextCode);

            codeEntities.Add(new RecoveryCode
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                CodeHash = BCrypt.Net.BCrypt.HashPassword(plaintextCode, workFactor: 12),
                IsUsed = false,
                CreatedAt = DateTime.UtcNow
            });
        }

        await _context.RecoveryCodes.AddRangeAsync(codeEntities, ct);
        await _context.SaveChangesAsync(ct);

        // Return plaintext codes (only time they're visible - user must save them)
        return plaintextCodes;
    }

    public async Task<bool> UseRecoveryCodeAsync(Guid userId, string code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            return false;

        var normalizedCode = code.Trim().ToUpperInvariant().Replace("-", "").Replace(" ", "");

        var unusedCodes = await _context.RecoveryCodes
            .Where(rc => rc.UserId == userId && !rc.IsUsed)
            .ToListAsync(ct);

        foreach (var recoveryCode in unusedCodes)
        {
            if (BCrypt.Net.BCrypt.Verify(normalizedCode, recoveryCode.CodeHash))
            {
                // Mark this code as used
                recoveryCode.IsUsed = true;
                recoveryCode.UsedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync(ct);
                return true;
            }
        }

        return false;
    }

    // ---------- TOTP Core Algorithm (RFC 6238) ----------

    /// <summary>
    /// Generates a TOTP code using HMAC-SHA1 per RFC 6238 / RFC 4226.
    /// </summary>
    internal static string GenerateTotpCode(byte[] secret, long timeStep)
    {
        // Convert time step counter to 8-byte big-endian
        var timeStepBytes = new byte[8];
        for (int i = 7; i >= 0; i--)
        {
            timeStepBytes[i] = (byte)(timeStep & 0xFF);
            timeStep >>= 8;
        }

        // HMAC-SHA256 (upgraded from SHA-1; existing enrollments must re-enroll).
        using var hmac = new HMACSHA256(secret);
        var hash = hmac.ComputeHash(timeStepBytes);

        // Dynamic truncation per RFC 4226 Section 5.4
        int offset = hash[^1] & 0x0F;
        int binaryCode =
            ((hash[offset] & 0x7F) << 24) |
            ((hash[offset + 1] & 0xFF) << 16) |
            ((hash[offset + 2] & 0xFF) << 8) |
            (hash[offset + 3] & 0xFF);

        // Modulo to get the desired number of digits
        int otp = binaryCode % (int)Math.Pow(10, CodeDigits);

        return otp.ToString().PadLeft(CodeDigits, '0');
    }

    // ---------- Base32 Encoding/Decoding (RFC 4648) ----------

    /// <summary>
    /// Encodes a byte array to Base32 string (RFC 4648, no padding).
    /// </summary>
    internal static string Base32Encode(byte[] data)
    {
        if (data.Length == 0)
            return string.Empty;

        var result = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0;
        int bitsLeft = 0;

        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;

            while (bitsLeft >= 5)
            {
                bitsLeft -= 5;
                int index = (buffer >> bitsLeft) & 0x1F;
                result.Append(Base32Alphabet[index]);
            }
        }

        // Handle remaining bits (pad with zeros on the right)
        if (bitsLeft > 0)
        {
            int index = (buffer << (5 - bitsLeft)) & 0x1F;
            result.Append(Base32Alphabet[index]);
        }

        return result.ToString();
    }

    /// <summary>
    /// Decodes a Base32 string to byte array (RFC 4648, tolerates missing padding).
    /// </summary>
    internal static byte[] Base32Decode(string base32)
    {
        if (string.IsNullOrWhiteSpace(base32))
            return Array.Empty<byte>();

        // Normalize: uppercase, strip padding and whitespace
        var normalized = base32.Trim().ToUpperInvariant()
            .Replace("=", "")
            .Replace(" ", "")
            .Replace("-", "");

        var output = new List<byte>();
        int buffer = 0;
        int bitsLeft = 0;

        foreach (var c in normalized)
        {
            int value = Base32Alphabet.IndexOf(c);
            if (value < 0)
                throw new FormatException($"Invalid Base32 character: {c}");

            buffer = (buffer << 5) | value;
            bitsLeft += 5;

            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                output.Add((byte)(buffer >> bitsLeft));
                buffer &= (1 << bitsLeft) - 1;
            }
        }

        return output.ToArray();
    }

    /// <summary>
    /// Formats a Base32 secret into groups of 4 for easier manual entry.
    /// Example: "JBSWY3DPEHPK3PXP" becomes "JBSW Y3DP EHPK 3PXP"
    /// </summary>
    private static string FormatManualEntryKey(string base32Secret)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < base32Secret.Length; i++)
        {
            if (i > 0 && i % 4 == 0)
                sb.Append(' ');
            sb.Append(base32Secret[i]);
        }
        return sb.ToString();
    }
}
