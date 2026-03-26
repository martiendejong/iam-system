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

    // ========== Passkey Methods ==========

    public async Task<PasskeyRegistrationOptionsResponse> BeginPasskeyRegistrationAsync(
        string username,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        var request = new BeginPasskeyRegistrationRequest
        {
            Username = username,
            DisplayName = displayName
        };

        var response = await _httpClient.PostAsJsonAsync("/api/passkey/register/begin", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var options = await response.Content.ReadFromJsonAsync<PasskeyRegistrationOptionsResponse>(cancellationToken);
        if (options == null)
            throw new InvalidOperationException("Failed to parse passkey registration options");

        return options;
    }

    public async Task<SuccessResponse> CompletePasskeyRegistrationAsync(
        string credentialName,
        object attestationResponse,
        CancellationToken cancellationToken = default)
    {
        var request = new CompletePasskeyRegistrationRequest
        {
            CredentialName = credentialName,
            AttestationResponse = attestationResponse
        };

        var response = await _httpClient.PostAsJsonAsync("/api/passkey/register/complete", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<SuccessResponse>(cancellationToken);
        if (result == null)
            throw new InvalidOperationException("Failed to parse passkey registration result");

        return result;
    }

    public async Task<PasskeyAuthenticationOptionsResponse> BeginPasskeyAuthenticationAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        var request = new BeginPasskeyAuthenticationRequest
        {
            Username = username
        };

        var response = await _httpClient.PostAsJsonAsync("/api/passkey/authenticate/begin", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var options = await response.Content.ReadFromJsonAsync<PasskeyAuthenticationOptionsResponse>(cancellationToken);
        if (options == null)
            throw new InvalidOperationException("Failed to parse passkey authentication options");

        return options;
    }

    public async Task<PasskeyAuthenticationResponse> CompletePasskeyAuthenticationAsync(
        object assertionResponse,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/passkey/authenticate/complete", assertionResponse, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<PasskeyAuthenticationResponse>(cancellationToken);
        if (result == null)
            throw new InvalidOperationException("Failed to parse passkey authentication result");

        // Store token if provided
        if (!string.IsNullOrEmpty(result.Token))
        {
            _accessToken = result.Token;
            UpdateAuthorizationHeader();
        }

        return result;
    }

    public async Task<List<PasskeyCredentialDto>> GetPasskeyCredentialsAsync(CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync("/api/passkey/credentials", cancellationToken);
        response.EnsureSuccessStatusCode();

        var credentials = await response.Content.ReadFromJsonAsync<List<PasskeyCredentialDto>>(cancellationToken);
        if (credentials == null)
            throw new InvalidOperationException("Failed to parse passkey credentials");

        return credentials;
    }

    public async Task<SuccessResponse> DeletePasskeyCredentialAsync(
        Guid credentialId,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.DeleteAsync($"/api/passkey/credentials/{credentialId}", cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<SuccessResponse>(cancellationToken);
        if (result == null)
            throw new InvalidOperationException("Failed to parse delete result");

        return result;
    }

    public async Task<SuccessResponse> RenamePasskeyCredentialAsync(
        Guid credentialId,
        string newName,
        CancellationToken cancellationToken = default)
    {
        var request = new RenamePasskeyRequest
        {
            NewName = newName
        };

        var response = await _httpClient.PatchAsJsonAsync($"/api/passkey/credentials/{credentialId}", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<SuccessResponse>(cancellationToken);
        if (result == null)
            throw new InvalidOperationException("Failed to parse rename result");

        return result;
    }
}
