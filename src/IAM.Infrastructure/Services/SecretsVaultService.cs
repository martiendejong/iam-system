using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IAM.Infrastructure.Services;

/// <summary>
/// Secrets vault service with AES-256 encryption at rest, automatic rotation scheduling,
/// grace periods for version transitions, and full version history tracking.
/// </summary>
public class SecretsVaultService : ISecretsVaultService
{
    private readonly IAMDbContext _context;
    private readonly ILogger<SecretsVaultService> _logger;
    private readonly byte[] _masterKey;

    public SecretsVaultService(
        IAMDbContext context,
        IConfiguration configuration,
        ILogger<SecretsVaultService> logger)
    {
        _context = context;
        _logger = logger;

        // Master encryption key from configuration (must be 32 bytes for AES-256)
        var masterKeyString = configuration["SecretsVault:MasterKey"]
            ?? throw new InvalidOperationException("SecretsVault:MasterKey is not configured. Set a 32-byte Base64 key.");
        _masterKey = Convert.FromBase64String(masterKeyString);

        if (_masterKey.Length != 32)
            throw new InvalidOperationException("SecretsVault:MasterKey must be exactly 32 bytes (256 bits) when decoded from Base64.");
    }

    public async Task<SecretEntry> CreateSecretAsync(
        string name,
        string plainTextValue,
        Guid? tenantId = null,
        string secretType = "Generic",
        string? description = null,
        string? rotationScheduleJson = null,
        string? tags = null,
        Guid? createdByUserId = null,
        CancellationToken ct = default)
    {
        var (encryptedValue, iv) = Encrypt(plainTextValue);

        var secret = new SecretEntry
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            EncryptedValue = encryptedValue,
            IV = iv,
            RotationSchedule = rotationScheduleJson,
            SecretType = secretType,
            Description = description,
            Tags = tags,
            CreatedByUserId = createdByUserId,
            Version = 1,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // Calculate next rotation time if schedule is provided
        if (!string.IsNullOrEmpty(rotationScheduleJson))
        {
            secret.NextRotationAt = CalculateNextRotation(rotationScheduleJson);
        }

        _context.SecretEntries.Add(secret);

        // Create initial version history entry
        var version = new SecretVersion
        {
            Id = Guid.NewGuid(),
            SecretEntryId = secret.Id,
            EncryptedValue = encryptedValue,
            IV = iv,
            Version = 1,
            RotationReason = "Initial",
            RotatedByUserId = createdByUserId,
            CreatedAt = DateTime.UtcNow
        };

        _context.SecretVersions.Add(version);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Secret created: {SecretId} ({SecretName}) type={SecretType} tenant={TenantId}",
            secret.Id, secret.Name, secret.SecretType, secret.TenantId);

        return secret;
    }

    public async Task<SecretEntry?> GetSecretAsync(Guid id, CancellationToken ct = default)
    {
        return await _context.SecretEntries
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id, ct);
    }

    public async Task<string?> GetSecretValueAsync(Guid id, CancellationToken ct = default)
    {
        var secret = await _context.SecretEntries
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id && s.IsActive, ct);

        if (secret == null)
            return null;

        return Decrypt(secret.EncryptedValue, secret.IV);
    }

    public async Task<List<SecretEntry>> GetSecretsAsync(
        Guid? tenantId = null,
        string? secretType = null,
        bool? isActive = null,
        CancellationToken ct = default)
    {
        var query = _context.SecretEntries.AsNoTracking().AsQueryable();

        if (tenantId.HasValue)
            query = query.Where(s => s.TenantId == tenantId.Value);

        if (!string.IsNullOrEmpty(secretType))
            query = query.Where(s => s.SecretType == secretType);

        if (isActive.HasValue)
            query = query.Where(s => s.IsActive == isActive.Value);

        return await query
            .OrderByDescending(s => s.UpdatedAt)
            .ToListAsync(ct);
    }

    public async Task<SecretEntry?> UpdateSecretAsync(
        Guid id,
        string? name = null,
        string? description = null,
        string? rotationScheduleJson = null,
        string? tags = null,
        string? secretType = null,
        bool? isActive = null,
        CancellationToken ct = default)
    {
        var secret = await _context.SecretEntries.FindAsync(new object[] { id }, ct);
        if (secret == null)
            return null;

        if (name != null) secret.Name = name;
        if (description != null) secret.Description = description;
        if (tags != null) secret.Tags = tags;
        if (secretType != null) secret.SecretType = secretType;
        if (isActive.HasValue) secret.IsActive = isActive.Value;

        if (rotationScheduleJson != null)
        {
            secret.RotationSchedule = rotationScheduleJson;
            secret.NextRotationAt = CalculateNextRotation(rotationScheduleJson);
        }

        secret.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Secret updated: {SecretId} ({SecretName})", secret.Id, secret.Name);

        return secret;
    }

    public async Task<SecretEntry> RotateSecretAsync(
        Guid id,
        string newPlainTextValue,
        string rotationReason = "Manual",
        Guid? rotatedByUserId = null,
        TimeSpan? gracePeriod = null,
        CancellationToken ct = default)
    {
        var secret = await _context.SecretEntries.FindAsync(new object[] { id }, ct)
            ?? throw new KeyNotFoundException($"Secret with ID {id} not found.");

        var (encryptedValue, iv) = Encrypt(newPlainTextValue);

        // Increment version
        secret.Version++;
        secret.EncryptedValue = encryptedValue;
        secret.IV = iv;
        secret.LastRotatedAt = DateTime.UtcNow;
        secret.UpdatedAt = DateTime.UtcNow;

        // Recalculate next rotation from schedule
        if (!string.IsNullOrEmpty(secret.RotationSchedule))
        {
            secret.NextRotationAt = CalculateNextRotation(secret.RotationSchedule);
        }

        // Create version history entry
        var version = new SecretVersion
        {
            Id = Guid.NewGuid(),
            SecretEntryId = secret.Id,
            EncryptedValue = encryptedValue,
            IV = iv,
            Version = secret.Version,
            RotationReason = rotationReason,
            RotatedByUserId = rotatedByUserId,
            CreatedAt = DateTime.UtcNow,
            GracePeriodEndsAt = gracePeriod.HasValue ? DateTime.UtcNow.Add(gracePeriod.Value) : null
        };

        _context.SecretVersions.Add(version);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Secret rotated: {SecretId} ({SecretName}) to version {Version}, reason={Reason}, gracePeriod={GracePeriod}",
            secret.Id, secret.Name, secret.Version, rotationReason,
            gracePeriod?.ToString() ?? "none");

        return secret;
    }

    public async Task<List<SecretVersion>> GetSecretHistoryAsync(Guid secretId, CancellationToken ct = default)
    {
        return await _context.SecretVersions
            .AsNoTracking()
            .Where(v => v.SecretEntryId == secretId)
            .OrderByDescending(v => v.Version)
            .ToListAsync(ct);
    }

    public async Task<bool> DeleteSecretAsync(Guid id, CancellationToken ct = default)
    {
        var secret = await _context.SecretEntries.FindAsync(new object[] { id }, ct);
        if (secret == null)
            return false;

        // Soft delete - deactivate rather than remove
        secret.IsActive = false;
        secret.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Secret deactivated: {SecretId} ({SecretName})", secret.Id, secret.Name);
        return true;
    }

    public async Task<List<SecretEntry>> GetSecretsDueForRotationAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        return await _context.SecretEntries
            .Where(s => s.IsActive && s.NextRotationAt.HasValue && s.NextRotationAt.Value <= now)
            .OrderBy(s => s.NextRotationAt)
            .ToListAsync(ct);
    }

    #region Encryption Helpers

    // Version byte prefixes stored as the first byte of the base64-decoded blob.
    // 0x01 = legacy AES-CBC (IV stored separately in the IV column)
    // 0x02 = AES-GCM authenticated encryption (nonce+tag+ciphertext, no separate IV column)
    private const byte VersionCbc = 0x01;
    private const byte VersionGcm = 0x02;
    private const int GcmNonceSize = 12; // 96-bit nonce per NIST SP 800-38D
    private const int GcmTagSize = 16;   // 128-bit authentication tag

    /// <summary>
    /// Encrypts plaintext using AES-256-GCM (authenticated encryption).
    /// Returns (EncryptedBase64, IvBase64) where IvBase64 is empty — the nonce is
    /// embedded in the blob so the column is kept for schema compatibility.
    /// </summary>
    private (string EncryptedBase64, string IvBase64) Encrypt(string plainText)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var nonce = new byte[GcmNonceSize];
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[plainBytes.Length];
        var tag = new byte[GcmTagSize];

        using var aesGcm = new AesGcm(_masterKey, GcmTagSize);
        aesGcm.Encrypt(nonce, plainBytes, ciphertext, tag);

        // Layout: version(1) + nonce(12) + tag(16) + ciphertext
        var blob = new byte[1 + GcmNonceSize + GcmTagSize + ciphertext.Length];
        blob[0] = VersionGcm;
        nonce.CopyTo(blob, 1);
        tag.CopyTo(blob, 1 + GcmNonceSize);
        ciphertext.CopyTo(blob, 1 + GcmNonceSize + GcmTagSize);

        // IV column is unused for GCM (nonce is in the blob); store empty string.
        return (Convert.ToBase64String(blob), string.Empty);
    }

    /// <summary>
    /// Decrypts a secret value. Supports both legacy AES-CBC (version 0x01, uses
    /// the ivBase64 column) and new AES-GCM (version 0x02, nonce embedded in blob).
    /// </summary>
    private string Decrypt(string encryptedBase64, string ivBase64)
    {
        var blob = Convert.FromBase64String(encryptedBase64);

        // Detect version: if the first byte is 0x02 use GCM, otherwise treat as legacy CBC.
        // Legacy blobs don't start with 0x01 explicitly — any non-0x02 byte falls through to CBC.
        if (blob.Length > 0 && blob[0] == VersionGcm)
        {
            return DecryptGcm(blob);
        }

        // Legacy path: AES-CBC with PKCS7 padding, IV from the separate column.
        return DecryptCbc(blob, ivBase64);
    }

    private string DecryptGcm(byte[] blob)
    {
        // blob layout: version(1) + nonce(12) + tag(16) + ciphertext
        if (blob.Length < 1 + GcmNonceSize + GcmTagSize)
            throw new CryptographicException("GCM blob is too short to be valid.");

        var nonce = blob[1..(1 + GcmNonceSize)];
        var tag = blob[(1 + GcmNonceSize)..(1 + GcmNonceSize + GcmTagSize)];
        var ciphertext = blob[(1 + GcmNonceSize + GcmTagSize)..];
        var plaintext = new byte[ciphertext.Length];

        using var aesGcm = new AesGcm(_masterKey, GcmTagSize);
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }

    private string DecryptCbc(byte[] cipherBytes, string ivBase64)
    {
        using var aes = Aes.Create();
        aes.Key = _masterKey;
        aes.IV = Convert.FromBase64String(ivBase64);
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var decryptor = aes.CreateDecryptor();
        var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
        return Encoding.UTF8.GetString(plainBytes);
    }

    #endregion

    #region Rotation Schedule Helpers

    /// <summary>
    /// Parses a rotation schedule JSON and calculates the next rotation datetime.
    /// Schedule format: { "intervalDays": 30, "gracePeriodHours": 24 }
    /// </summary>
    private DateTime? CalculateNextRotation(string rotationScheduleJson)
    {
        try
        {
            var schedule = JsonSerializer.Deserialize<RotationScheduleConfig>(rotationScheduleJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (schedule == null || schedule.IntervalDays <= 0)
                return null;

            return DateTime.UtcNow.AddDays(schedule.IntervalDays);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid rotation schedule JSON: {Schedule}", rotationScheduleJson);
            return null;
        }
    }

    #endregion
}

/// <summary>
/// Configuration model for secret rotation scheduling.
/// </summary>
public class RotationScheduleConfig
{
    /// <summary>
    /// Number of days between automatic rotations. 0 = disabled.
    /// </summary>
    public int IntervalDays { get; set; }

    /// <summary>
    /// Grace period in hours during which both old and new secret values are valid.
    /// Allows consuming services to pick up the rotated value.
    /// </summary>
    public int GracePeriodHours { get; set; } = 24;

    /// <summary>
    /// If true, the system will auto-generate a new secret value on rotation.
    /// If false, rotation is flagged as "pending" for manual value entry.
    /// </summary>
    public bool AutoGenerate { get; set; }

    /// <summary>
    /// Length of auto-generated secrets (default 64 characters).
    /// </summary>
    public int AutoGenerateLength { get; set; } = 64;
}
