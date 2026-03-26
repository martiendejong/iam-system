using Fido2NetLib;
using Fido2NetLib.Objects;

namespace IAM.Core.Services;

public interface IPasskeyService
{
    /// <summary>
    /// Begin passkey registration (generate challenge)
    /// </summary>
    Task<CredentialCreateOptions> BeginRegistrationAsync(
        Guid userId,
        string username,
        string displayName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Complete passkey registration (verify and store credential)
    /// </summary>
    Task<bool> CompleteRegistrationAsync(
        Guid userId,
        string credentialName,
        AuthenticatorAttestationRawResponse attestationResponse,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Begin passkey authentication (generate challenge)
    /// </summary>
    Task<AssertionOptions> BeginAuthenticationAsync(
        string username,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Complete passkey authentication (verify assertion and return user)
    /// </summary>
    Task<Guid?> CompleteAuthenticationAsync(
        AuthenticatorAssertionRawResponse assertionResponse,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all credentials for a user
    /// </summary>
    Task<List<CredentialDto>> GetUserCredentialsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete a credential
    /// </summary>
    Task<bool> DeleteCredentialAsync(
        Guid userId,
        Guid credentialId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rename a credential
    /// </summary>
    Task<bool> RenameCredentialAsync(
        Guid userId,
        Guid credentialId,
        string newName,
        CancellationToken cancellationToken = default);
}

public class CredentialDto
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public string? DeviceType { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public bool IsBackupEligible { get; set; }
    public string[]? Transports { get; set; }
}
