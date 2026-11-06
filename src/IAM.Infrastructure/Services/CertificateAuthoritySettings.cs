using Microsoft.Extensions.Configuration;

namespace IAM.Infrastructure.Services;

/// <summary>
/// Where the IAM certificate authority keeps its root key file and what protects it (task 5165). Resolved once at
/// startup (<see cref="FromConfiguration"/>), before any CA file is read, so a missing password stops the app
/// instead of silently protecting the root key with a password that is written in the source.
/// </summary>
public sealed class CertificateAuthoritySettings
{
    /// <summary>
    /// Password used outside production-like environments when none is configured. Development only: it is never used
    /// when the environment is not Development, which refuses to start without a configured password.
    /// </summary>
    public const string DevelopmentOnlyPassword = "dev-only-ca-password-do-not-use-in-production";

    /// <summary>Where earlier versions kept the key file by default (relative to the working directory).</summary>
    public const string LegacyDefaultRelativePath = "./ca-certs/ca.pfx";

    public CertificateAuthoritySettings(
        string certificatePath,
        string certificatePassword,
        string? legacyCertificatePassword = null,
        bool usesLegacyDefaultLocation = false)
    {
        if (string.IsNullOrWhiteSpace(certificatePath))
            throw new ArgumentException("A CA certificate path is required.", nameof(certificatePath));
        if (string.IsNullOrWhiteSpace(certificatePassword))
            throw new ArgumentException("A CA certificate password is required.", nameof(certificatePassword));

        CertificatePath = certificatePath;
        CertificatePassword = certificatePassword;
        LegacyCertificatePassword = string.IsNullOrWhiteSpace(legacyCertificatePassword) ? null : legacyCertificatePassword;
        UsesLegacyDefaultLocation = usesLegacyDefaultLocation;
    }

    public string CertificatePath { get; }

    /// <summary>The password the CA key file is (and, after the one-time re-protect, will be) protected with.</summary>
    public string CertificatePassword { get; }

    /// <summary>
    /// Only for the one-time upgrade of a key file made by an older version: when the file cannot be opened with
    /// <see cref="CertificatePassword"/>, this password is tried once and the file is re-saved with
    /// <see cref="CertificatePassword"/>. Never used for anything else; null when not configured.
    /// </summary>
    public string? LegacyCertificatePassword { get; }

    /// <summary>True when no path is configured and an existing key file at the old default location is being kept in place.</summary>
    public bool UsesLegacyDefaultLocation { get; }

    /// <summary>
    /// Reads <c>Ca:CertificatePath</c>, <c>Ca:CertificatePassword</c> and <c>Ca:LegacyCertificatePassword</c>.
    /// Outside Development a missing password throws <see cref="InvalidOperationException"/> (same style as the
    /// OpenIddict signing certificate check in Program.cs).
    /// </summary>
    /// <param name="defaultPath">Override of the default key file path (tests).</param>
    /// <param name="legacyPath">Override of the old default key file path (tests).</param>
    public static CertificateAuthoritySettings FromConfiguration(
        IConfiguration configuration,
        bool isDevelopment,
        string? defaultPath = null,
        string? legacyPath = null)
    {
        var password = configuration["Ca:CertificatePassword"];
        if (string.IsNullOrWhiteSpace(password))
        {
            if (!isDevelopment)
            {
                throw new InvalidOperationException(
                    "Production CA certificate password not configured. " +
                    "Set Ca:CertificatePassword in configuration (a strong generated value; it protects the CA root key file).");
            }

            password = DevelopmentOnlyPassword;
        }

        var legacyPassword = configuration["Ca:LegacyCertificatePassword"];

        var configuredPath = configuration["Ca:CertificatePath"];
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return new CertificateAuthoritySettings(configuredPath, password, legacyPassword);

        // No path configured: the default lives outside the application folder. An existing key file at the old
        // default location is kept where it is (moving it would make the app create a new CA with a new thumbprint
        // and orphan every issued device certificate); it is re-protected in place and a warning asks for a move.
        var newDefault = defaultPath ?? DefaultPath();
        var oldDefault = legacyPath ?? LegacyDefaultRelativePath;
        if (!File.Exists(newDefault) && File.Exists(oldDefault))
            return new CertificateAuthoritySettings(oldDefault, password, legacyPassword, usesLegacyDefaultLocation: true);

        return new CertificateAuthoritySettings(newDefault, password, legacyPassword);
    }

    private static string DefaultPath()
    {
        var root = Environment.GetFolderPath(OperatingSystem.IsWindows()
            ? Environment.SpecialFolder.CommonApplicationData
            : Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "IAMSystem", "ca", "ca.pfx");
    }
}
