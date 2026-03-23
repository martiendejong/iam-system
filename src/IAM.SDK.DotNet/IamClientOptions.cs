namespace IAM.SDK.DotNet;

/// <summary>
/// Configuration options for the IAM client
/// </summary>
public class IamClientOptions
{
    /// <summary>
    /// Base URL of the IAM API (e.g., https://localhost:5001)
    /// </summary>
    public string ApiBaseUrl { get; set; } = "https://localhost:5001";

    /// <summary>
    /// Timeout for HTTP requests in seconds
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Whether to automatically refresh expired tokens
    /// </summary>
    public bool AutoRefreshTokens { get; set; } = true;

    /// <summary>
    /// Optional client ID for OAuth flows
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Optional client secret for OAuth flows
    /// </summary>
    public string? ClientSecret { get; set; }
}
