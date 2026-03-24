namespace IAM.SDK.DotNet.Models;

/// <summary>
/// Request to begin passkey registration
/// </summary>
public class BeginPasskeyRegistrationRequest
{
    public required string Username { get; set; }
    public required string DisplayName { get; set; }
}

/// <summary>
/// Response containing credential creation options
/// </summary>
public class PasskeyRegistrationOptionsResponse
{
    public required object Options { get; set; } // CredentialCreateOptions from Fido2
}

/// <summary>
/// Request to complete passkey registration
/// </summary>
public class CompletePasskeyRegistrationRequest
{
    public required string CredentialName { get; set; }
    public required object AttestationResponse { get; set; } // AuthenticatorAttestationRawResponse from Fido2
}

/// <summary>
/// Request to begin passkey authentication
/// </summary>
public class BeginPasskeyAuthenticationRequest
{
    public required string Username { get; set; }
}

/// <summary>
/// Response containing assertion options
/// </summary>
public class PasskeyAuthenticationOptionsResponse
{
    public required object Options { get; set; } // AssertionOptions from Fido2
}

/// <summary>
/// Complete passkey authentication request (body contains AuthenticatorAssertionRawResponse)
/// </summary>
public class CompletePasskeyAuthenticationRequest
{
    public required object AssertionResponse { get; set; } // AuthenticatorAssertionRawResponse from Fido2
}

/// <summary>
/// Response from completing passkey authentication
/// </summary>
public class PasskeyAuthenticationResponse
{
    public required Guid UserId { get; set; }
    public required string Message { get; set; }
    public string? Token { get; set; } // JWT token when implemented
}

/// <summary>
/// Registered passkey credential
/// </summary>
public class PasskeyCredentialDto
{
    public required Guid Id { get; set; }
    public required string Name { get; set; }
    public string? DeviceType { get; set; }
    public required DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public required bool IsBackupEligible { get; set; }
    public string[]? Transports { get; set; }
}

/// <summary>
/// Request to rename a passkey
/// </summary>
public class RenamePasskeyRequest
{
    public required string NewName { get; set; }
}

/// <summary>
/// Generic success response
/// </summary>
public class SuccessResponse
{
    public required string Message { get; set; }
}
