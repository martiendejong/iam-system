using IAM.SDK.DotNet.Models;

namespace IAM.SDK.DotNet;

/// <summary>
/// IAM authentication client interface
/// </summary>
public interface IIamAuthClient
{
    /// <summary>
    /// Authenticate with email and password
    /// </summary>
    Task<LoginResponse> LoginAsync(string email, string password, CancellationToken cancellationToken = default);

    /// <summary>
    /// Refresh an expired access token
    /// </summary>
    Task<LoginResponse> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the current authenticated user
    /// </summary>
    Task<UserDto> GetCurrentUserAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Logout and invalidate tokens
    /// </summary>
    Task LogoutAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the current access token (if authenticated)
    /// </summary>
    string? GetAccessToken();

    /// <summary>
    /// Set the access token manually
    /// </summary>
    void SetAccessToken(string accessToken);
}
