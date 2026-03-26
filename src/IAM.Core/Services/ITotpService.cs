namespace IAM.Core.Services;

public interface ITotpService
{
    TotpSetupResult GenerateSetupInfo(string email, string issuer = "IAM System");
    bool ValidateCode(string secret, string code);
    Task<TotpSetupResult> EnableTotpAsync(Guid userId, CancellationToken ct = default);
    Task<bool> VerifyAndActivateTotpAsync(Guid userId, string code, CancellationToken ct = default);
    Task<bool> DisableTotpAsync(Guid userId, string code, CancellationToken ct = default);
    Task<bool> ValidateTotpLoginAsync(Guid userId, string code, CancellationToken ct = default);
    Task<List<string>> GenerateRecoveryCodesAsync(Guid userId, int count = 8, CancellationToken ct = default);
    Task<bool> UseRecoveryCodeAsync(Guid userId, string code, CancellationToken ct = default);
}

public class TotpSetupResult
{
    public string Secret { get; set; } = string.Empty;
    public string QrCodeUri { get; set; } = string.Empty; // otpauth:// URI for QR code
    public string ManualEntryKey { get; set; } = string.Empty; // Base32 encoded for manual entry
}
