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

    // ========== Passkey Methods ==========

    /// <summary>
    /// Begin passkey registration (requires authentication)
    /// </summary>
    Task<PasskeyRegistrationOptionsResponse> BeginPasskeyRegistrationAsync(
        string username,
        string displayName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Complete passkey registration (requires authentication)
    /// </summary>
    Task<SuccessResponse> CompletePasskeyRegistrationAsync(
        string credentialName,
        object attestationResponse,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Begin passkey authentication (no authentication required)
    /// </summary>
    Task<PasskeyAuthenticationOptionsResponse> BeginPasskeyAuthenticationAsync(
        string username,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Complete passkey authentication and receive token (no authentication required)
    /// </summary>
    Task<PasskeyAuthenticationResponse> CompletePasskeyAuthenticationAsync(
        object assertionResponse,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all registered passkeys for the current user (requires authentication)
    /// </summary>
    Task<List<PasskeyCredentialDto>> GetPasskeyCredentialsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete a passkey (requires authentication)
    /// </summary>
    Task<SuccessResponse> DeletePasskeyCredentialAsync(Guid credentialId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rename a passkey (requires authentication)
    /// </summary>
    Task<SuccessResponse> RenamePasskeyCredentialAsync(
        Guid credentialId,
        string newName,
        CancellationToken cancellationToken = default);
}
