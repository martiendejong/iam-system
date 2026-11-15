namespace IAM.API;

/// <summary>
/// Deployment-shape settings that used to be hardcoded for the /auth deployment
/// (maendeleo.martiendejong.nl/auth). Every default reproduces that deployment exactly; a standalone
/// host-root deployment (for example a customer IAM at https://tn-auth.example.nl) overrides them.
/// </summary>
public static class DeploymentSettings
{
    public const string LoginPathConfigKey = "Ui:LoginPath";
    public const string DefaultLoginPath = "/auth/login";
    public const string CorsConfigSection = "Cors:AllowedOrigins";

    /// <summary>The CORS origins used when Cors:AllowedOrigins is not configured.</summary>
    public static readonly string[] DefaultCorsOrigins =
    {
        "http://localhost:5173",
        "https://localhost:5173",
        "https://maendeleo.martiendejong.nl",
        "https://knowledge.prospergenics.com"
    };

    /// <summary>
    /// Cors:AllowedOrigins (env Cors__AllowedOrigins__0, __1, ...) when it holds at least one non-blank entry:
    /// then exactly that list, the defaults are NOT appended. Blank entries are dropped so an empty
    /// compose pass-through (Cors__AllowedOrigins__0=) falls back to the defaults.
    /// </summary>
    public static string[] ResolveCorsOrigins(IConfiguration configuration)
    {
        var configured = (configuration.GetSection(CorsConfigSection).Get<string[]>() ?? Array.Empty<string>())
            .Where(o => !string.IsNullOrWhiteSpace(o))
            .Select(o => o.Trim().TrimEnd('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return configured.Length > 0 ? configured : DefaultCorsOrigins;
    }

    /// <summary>
    /// Ui:LoginPath: the browser-visible path of the SPA login page that /connect/authorize redirects
    /// unauthenticated users to. Default "/auth/login"; "/login" at a host root. Only a local absolute
    /// path is accepted (starts with a single '/'), anything else falls back to the default so the
    /// setting can never turn the authorize endpoint into an open redirect.
    /// </summary>
    public static string ResolveLoginPath(IConfiguration configuration)
    {
        var value = configuration[LoginPathConfigKey]?.Trim();
        if (string.IsNullOrEmpty(value)
            || !value.StartsWith('/')
            || value.StartsWith("//")
            || value.StartsWith("/\\")
            || value.Contains('?')
            || value.Contains('#'))
            return DefaultLoginPath;
        return value.Length > 1 ? value.TrimEnd('/') : DefaultLoginPath;
    }
}
