using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace IAM.SDK.DotNet;

/// <summary>
/// IAM device authentication client for IoT devices.
/// Supports X.509 certificate and HMAC-SHA256 authentication,
/// resource/MQTT authorization, and automatic heartbeat.
/// </summary>
public class IamDeviceClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly IamDeviceClientOptions _options;
    private string? _accessToken;
    private DateTime _tokenExpiry;
    private Timer? _heartbeatTimer;
    private bool _disposed;

    /// <summary>
    /// Create a new device client with explicit options
    /// </summary>
    public IamDeviceClient(IamDeviceClientOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(options.DeviceId))
            throw new ArgumentException("DeviceId is required", nameof(options));

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(options.BaseUrl),
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds)
        };
    }

    /// <summary>
    /// Create a new device client from DI container
    /// </summary>
    public IamDeviceClient(HttpClient httpClient, IOptions<IamDeviceClientOptions> options)
    {
        _options = options.Value;
        _httpClient = httpClient;

        _httpClient.BaseAddress = new Uri(_options.BaseUrl);
        _httpClient.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
    }

    /// <summary>
    /// Whether the device is currently authenticated with a valid token
    /// </summary>
    public bool IsAuthenticated => _accessToken != null && _tokenExpiry > DateTime.UtcNow;

    /// <summary>
    /// The current access token, or null if not authenticated or token is expired
    /// </summary>
    public string? AccessToken => IsAuthenticated ? _accessToken : null;

    /// <summary>
    /// Authenticate using an X.509 certificate in PEM format
    /// </summary>
    /// <param name="certificatePem">The X.509 certificate PEM string</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Device authentication response with token and permissions</returns>
    public async Task<DeviceAuthResponse> AuthenticateWithCertificateAsync(
        string certificatePem,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(certificatePem))
            throw new ArgumentException("Certificate PEM is required", nameof(certificatePem));

        var response = await _httpClient.PostAsJsonAsync("/api/device-auth/certificate",
            new { certificatePem, deviceId = _options.DeviceId }, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<DeviceAuthResponse>(cancellationToken);
        if (result == null)
            throw new InvalidOperationException("Failed to parse device auth response");

        StoreToken(result);
        return result;
    }

    /// <summary>
    /// Authenticate using HMAC-SHA256 with a shared secret.
    /// Generates a timestamped nonce-based signature to prevent replay attacks.
    /// </summary>
    /// <param name="sharedSecret">The shared secret key</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Device authentication response with token and permissions</returns>
    public async Task<DeviceAuthResponse> AuthenticateWithHmacAsync(
        string sharedSecret,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sharedSecret))
            throw new ArgumentException("Shared secret is required", nameof(sharedSecret));

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var nonce = Guid.NewGuid().ToString("N")[..16];
        var message = $"{timestamp}:{nonce}:{_options.DeviceId}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(sharedSecret));
        var hash = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(message)));
        var hmacPassword = $"{timestamp}:{nonce}:{hash}";

        var response = await _httpClient.PostAsJsonAsync("/api/device-auth/hmac",
            new { deviceId = _options.DeviceId, hmacPassword }, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<DeviceAuthResponse>(cancellationToken);
        if (result == null)
            throw new InvalidOperationException("Failed to parse device auth response");

        StoreToken(result);
        return result;
    }

    /// <summary>
    /// Check if the device is authorized to perform an action on a resource
    /// </summary>
    /// <param name="resource">The resource identifier (e.g., "sensors/temperature")</param>
    /// <param name="action">The action to check (e.g., "read", "write")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Authorization response indicating whether the action is allowed</returns>
    public async Task<DeviceAuthorizeResponse> AuthorizeAsync(
        string resource,
        string action,
        CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();

        var response = await _httpClient.PostAsJsonAsync("/api/device-auth/authorize",
            new { deviceId = _options.DeviceId, resource, action }, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<DeviceAuthorizeResponse>(cancellationToken);
        if (result == null)
            throw new InvalidOperationException("Failed to parse authorization response");

        return result;
    }

    /// <summary>
    /// Check if the device is authorized to publish/subscribe to an MQTT topic
    /// </summary>
    /// <param name="topic">The MQTT topic (e.g., "devices/sensor-01/telemetry")</param>
    /// <param name="action">The MQTT action ("publish" or "subscribe")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Authorization response indicating whether the MQTT action is allowed</returns>
    public async Task<DeviceAuthorizeResponse> AuthorizeMqttAsync(
        string topic,
        string action,
        CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();

        var response = await _httpClient.PostAsJsonAsync("/api/device-auth/authorize-mqtt",
            new { deviceId = _options.DeviceId, topic, action }, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<DeviceAuthorizeResponse>(cancellationToken);
        if (result == null)
            throw new InvalidOperationException("Failed to parse MQTT authorization response");

        return result;
    }

    /// <summary>
    /// Send a heartbeat to indicate the device is still online
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task HeartbeatAsync(CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();

        var response = await _httpClient.PostAsJsonAsync("/api/device-auth/heartbeat",
            new { deviceId = _options.DeviceId }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Start sending automatic heartbeats at the specified interval
    /// </summary>
    /// <param name="intervalSeconds">Interval between heartbeats in seconds (default: 60)</param>
    public void StartHeartbeat(int intervalSeconds = 60)
    {
        if (intervalSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(intervalSeconds), "Interval must be positive");

        StopHeartbeat();

        _heartbeatTimer = new Timer(
            async _ =>
            {
                try
                {
                    await HeartbeatAsync();
                }
                catch
                {
                    // Swallow exceptions in background heartbeat to avoid crashing the device.
                    // The server will mark the device as offline after missing heartbeats.
                }
            },
            null,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(intervalSeconds));
    }

    /// <summary>
    /// Stop the automatic heartbeat timer
    /// </summary>
    public void StopHeartbeat()
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
    }

    private void StoreToken(DeviceAuthResponse result)
    {
        if (result.Success && result.AccessToken != null)
        {
            _accessToken = result.AccessToken;
            // Buffer 60 seconds before actual expiry to avoid edge-case failures
            _tokenExpiry = DateTime.UtcNow.AddSeconds(result.ExpiresIn - 60);
            UpdateAuthorizationHeader();
        }
    }

    private void UpdateAuthorizationHeader()
    {
        if (!string.IsNullOrEmpty(_accessToken))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _accessToken);
        }
    }

    private void EnsureAuthenticated()
    {
        if (!IsAuthenticated)
            throw new InvalidOperationException(
                "Device is not authenticated. Call AuthenticateWithCertificateAsync or AuthenticateWithHmacAsync first.");
    }

    /// <summary>
    /// Dispose the client and stop heartbeat
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _heartbeatTimer?.Dispose();
        _httpClient.Dispose();

        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Configuration options for the IAM device client
/// </summary>
public class IamDeviceClientOptions
{
    /// <summary>
    /// Base URL of the IAM API (e.g., "https://iam.example.com")
    /// </summary>
    public string BaseUrl { get; set; } = "https://localhost:5161";

    /// <summary>
    /// Unique device identifier registered in the IAM system
    /// </summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// Timeout for HTTP requests in seconds (default: 30)
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Heartbeat interval in seconds when auto-heartbeat is enabled (default: 60)
    /// </summary>
    public int HeartbeatIntervalSeconds { get; set; } = 60;
}

/// <summary>
/// Response from device authentication endpoints
/// </summary>
public class DeviceAuthResponse
{
    /// <summary>Whether authentication was successful</summary>
    public bool Success { get; set; }

    /// <summary>JWT access token for subsequent requests</summary>
    public string? AccessToken { get; set; }

    /// <summary>Token expiry time in seconds</summary>
    public int ExpiresIn { get; set; }

    /// <summary>The authenticated device ID</summary>
    public string? DeviceId { get; set; }

    /// <summary>Permissions granted to this device</summary>
    public List<string> Permissions { get; set; } = new();

    /// <summary>Error message if authentication failed</summary>
    public string? Error { get; set; }
}

/// <summary>
/// Response from device authorization endpoints
/// </summary>
public class DeviceAuthorizeResponse
{
    /// <summary>Whether the action is allowed</summary>
    public bool Allowed { get; set; }

    /// <summary>The permission that matched, if allowed</summary>
    public string? MatchedPermission { get; set; }

    /// <summary>Reason for denial, if not allowed</summary>
    public string? Reason { get; set; }
}
