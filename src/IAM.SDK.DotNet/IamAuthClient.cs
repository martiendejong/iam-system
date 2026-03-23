using System.Net.Http.Headers;
using System.Net.Http.Json;
using IAM.SDK.DotNet.Models;
using Microsoft.Extensions.Options;

namespace IAM.SDK.DotNet;

/// <summary>
/// IAM authentication client implementation
/// </summary>
public class IamAuthClient : IIamAuthClient
{
    private readonly HttpClient _httpClient;
    private readonly IamClientOptions _options;
    private string? _accessToken;
    private string? _refreshToken;

    public IamAuthClient(HttpClient httpClient, IOptions<IamClientOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;

        _httpClient.BaseAddress = new Uri(_options.ApiBaseUrl);
        _httpClient.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
    }

    public async Task<LoginResponse> LoginAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        var request = new LoginRequest { Email = email, Password = password };
        var response = await _httpClient.PostAsJsonAsync("/api/auth/login", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var loginResponse = await response.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken);
        if (loginResponse == null)
            throw new InvalidOperationException("Failed to parse login response");

        _accessToken = loginResponse.AccessToken;
        _refreshToken = loginResponse.RefreshToken;

        UpdateAuthorizationHeader();

        return loginResponse;
    }

    public async Task<LoginResponse> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        var request = new RefreshTokenRequest { RefreshToken = refreshToken };
        var response = await _httpClient.PostAsJsonAsync("/api/auth/refresh", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var loginResponse = await response.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken);
        if (loginResponse == null)
            throw new InvalidOperationException("Failed to parse refresh response");

        _accessToken = loginResponse.AccessToken;
        _refreshToken = loginResponse.RefreshToken;

        UpdateAuthorizationHeader();

        return loginResponse;
    }

    public async Task<UserDto> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync("/api/auth/me", cancellationToken);
        response.EnsureSuccessStatusCode();

        var user = await response.Content.ReadFromJsonAsync<UserDto>(cancellationToken);
        if (user == null)
            throw new InvalidOperationException("Failed to parse user response");

        return user;
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        await _httpClient.PostAsync("/api/auth/logout", null, cancellationToken);

        _accessToken = null;
        _refreshToken = null;
        _httpClient.DefaultRequestHeaders.Authorization = null;
    }

    public string? GetAccessToken() => _accessToken;

    public void SetAccessToken(string accessToken)
    {
        _accessToken = accessToken;
        UpdateAuthorizationHeader();
    }

    private void UpdateAuthorizationHeader()
    {
        if (!string.IsNullOrEmpty(_accessToken))
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        }
    }
}
