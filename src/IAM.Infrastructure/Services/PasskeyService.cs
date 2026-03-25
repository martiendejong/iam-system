using System.Linq;
using Fido2NetLib;
using Fido2NetLib.Objects;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace IAM.Infrastructure.Services;

public class PasskeyService : IPasskeyService
{
    private readonly IAMDbContext _context;
    private readonly IFido2 _fido2;
    private readonly IConfiguration _configuration;

    // In-memory storage for challenges (production should use distributed cache)
    private static readonly Dictionary<Guid, string> _registrationChallenges = new();
    private static readonly Dictionary<string, (string Challenge, Guid UserId)> _authenticationChallenges = new();

    public PasskeyService(IAMDbContext context, IFido2 fido2, IConfiguration configuration)
    {
        _context = context;
        _fido2 = fido2;
        _configuration = configuration;
    }

    public async Task<CredentialCreateOptions> BeginRegistrationAsync(
        Guid userId,
        string username,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        var user = await _context.Users
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user == null)
            throw new InvalidOperationException("User not found");

        // Get existing credentials for this user to exclude them
        var existingCredentials = await _context.Credentials
            .Where(c => c.UserId == userId)
            .Select(c => new PublicKeyCredentialDescriptor(c.CredentialId))
            .ToListAsync(cancellationToken);

        // Create Fido2 user
        var fido2User = new Fido2User
        {
            Id = userId.ToByteArray(),
            Name = username,
            DisplayName = displayName
        };

        // Authenticator selection criteria
        var authenticatorSelection = new AuthenticatorSelection
        {
            ResidentKey = ResidentKeyRequirement.Discouraged,
            UserVerification = UserVerificationRequirement.Preferred
        };

        // Extension options
        var extensions = new AuthenticationExtensionsClientInputs
        {
            CredProps = true
        };

        // Create credential options
        var options = _fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = fido2User,
            ExcludeCredentials = existingCredentials,
            AuthenticatorSelection = authenticatorSelection,
            AttestationPreference = AttestationConveyancePreference.None,
            Extensions = extensions
        });

        // Store challenge for verification
        _registrationChallenges[userId] = Convert.ToBase64String(options.Challenge);

        return options;
    }

    public async Task<bool> CompleteRegistrationAsync(
        Guid userId,
        string credentialName,
        AuthenticatorAttestationRawResponse attestationResponse,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Get stored challenge
            if (!_registrationChallenges.TryGetValue(userId, out var challengeBase64))
                throw new InvalidOperationException("No registration challenge found for user");

            // Verify attestation using callback pattern
            var success = await _fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = attestationResponse,
                OriginalOptions = new CredentialCreateOptions
                {
                    Challenge = Convert.FromBase64String(challengeBase64),
                    Rp = new PublicKeyCredentialRpEntity(
                        _configuration["Fido2:ServerDomain"] ?? "localhost",
                        _configuration["Fido2:ServerName"] ?? "IAM System",
                        null),
                    User = new Fido2User
                    {
                        Id = userId.ToByteArray(),
                        Name = "",
                        DisplayName = ""
                    },
                    PubKeyCredParams = new List<PubKeyCredParam>
                    {
                        new PubKeyCredParam(COSE.Algorithm.ES256),
                        new PubKeyCredParam(COSE.Algorithm.RS256)
                    },
                    Timeout = 60000
                },
                IsCredentialIdUniqueToUserCallback = async (args, ct) =>
                {
                    // Check if credential ID already exists
                    var exists = await _context.Credentials
                        .AnyAsync(c => c.CredentialId == args.CredentialId, ct);
                    return !exists;
                }
            }, cancellationToken);

            if (success == null)
                return false;

            // Convert transports to string array
            var transports = attestationResponse.Response.Transports?
                .Select(t => t.ToString())
                .ToArray();

            // Store credential in database
            var credential = new Credential
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                CredentialId = success.Id ?? throw new InvalidOperationException("Credential ID is null"),
                PublicKey = success.PublicKey,
                SignCounter = success.SignCount,
                CredType = success.Type.ToString(),
                AaGuid = success.AaGuid,
                Name = credentialName,
                UserAgent = attestationResponse.Response.ClientDataJson != null
                    ? System.Text.Encoding.UTF8.GetString(attestationResponse.Response.ClientDataJson).Substring(0, Math.Min(500, System.Text.Encoding.UTF8.GetString(attestationResponse.Response.ClientDataJson).Length))
                    : null,
                DeviceType = DetermineDeviceType(success.AaGuid),
                IsBackupEligible = false, // Not available in v4
                IsBackedUp = false, // Not available in v4
                Transports = transports,
                CreatedAt = DateTime.UtcNow,
                AttestationFormat = success.AttestationFormat,
                AttestationCertificate = null // Not available in v4
            };

            _context.Credentials.Add(credential);
            await _context.SaveChangesAsync(cancellationToken);

            // Clean up challenge
            _registrationChallenges.Remove(userId);

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<AssertionOptions> BeginAuthenticationAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        // Find user by email/username
        var user = await _context.Users
            .FirstOrDefaultAsync(u => u.Email == username, cancellationToken);

        if (user == null)
            throw new InvalidOperationException("User not found");

        // Get user's credentials
        var credentials = await _context.Credentials
            .Where(c => c.UserId == user.Id)
            .Select(c => new PublicKeyCredentialDescriptor(c.CredentialId))
            .ToListAsync(cancellationToken);

        if (!credentials.Any())
            throw new InvalidOperationException("No credentials registered for this user");

        // Create assertion options
        var extensions = new AuthenticationExtensionsClientInputs
        {
            UserVerificationMethod = true
        };

        var options = _fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = credentials,
            UserVerification = UserVerificationRequirement.Preferred,
            Extensions = extensions
        });

        // Store challenge for verification
        _authenticationChallenges[username] = (Convert.ToBase64String(options.Challenge), user.Id);

        return options;
    }

    public async Task<Guid?> CompleteAuthenticationAsync(
        AuthenticatorAssertionRawResponse assertionResponse,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Find credential - load all and filter in memory (credential IDs are small, won't scale for many users)
            // In production, consider adding a hash column to the database for efficient lookups
            var allCredentials = await _context.Credentials
                .Include(c => c.User)
                .ToListAsync(cancellationToken);

            var credential = allCredentials
                .FirstOrDefault(c => CompareByteArrays(c.CredentialId, Convert.FromBase64String(assertionResponse.Id)));

            if (credential == null)
                return null;

            // Get stored challenge
            if (!_authenticationChallenges.TryGetValue(credential.User!.Email, out var challengeData))
                return null;

            var (challengeBase64, expectedUserId) = challengeData;

            if (credential.UserId != expectedUserId)
                return null;

            // Verify assertion
            var options = new AssertionOptions
            {
                Challenge = Convert.FromBase64String(challengeBase64),
                RpId = _configuration["Fido2:ServerDomain"] ?? "localhost",
                AllowCredentials = new List<PublicKeyCredentialDescriptor>
                {
                    new PublicKeyCredentialDescriptor(credential.CredentialId)
                }
            };

            var success = await _fido2.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = assertionResponse,
                OriginalOptions = options,
                StoredPublicKey = credential.PublicKey,
                StoredSignatureCounter = credential.SignCounter,
                IsUserHandleOwnerOfCredentialIdCallback = async (args, ct) =>
                {
                    // Verify user handle matches
                    if (args.UserHandle != null)
                    {
                        var userIdBytes = credential.UserId.ToByteArray();
                        return args.UserHandle.SequenceEqual(userIdBytes);
                    }
                    return true;
                }
            }, cancellationToken);

            if (success.SignCount < 0)
                return null;

            // Update credential (sign counter, last used)
            credential.SignCounter = success.SignCount;
            credential.LastUsedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);

            // Clean up challenge
            _authenticationChallenges.Remove(credential.User.Email);

            return credential.UserId;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<List<CredentialDto>> GetUserCredentialsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return await _context.Credentials
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new CredentialDto
            {
                Id = c.Id,
                Name = c.Name ?? "Unnamed Passkey",
                DeviceType = c.DeviceType,
                CreatedAt = c.CreatedAt,
                LastUsedAt = c.LastUsedAt,
                IsBackupEligible = c.IsBackupEligible,
                Transports = c.Transports
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> DeleteCredentialAsync(
        Guid userId,
        Guid credentialId,
        CancellationToken cancellationToken = default)
    {
        var credential = await _context.Credentials
            .FirstOrDefaultAsync(c => c.Id == credentialId && c.UserId == userId, cancellationToken);

        if (credential == null)
            return false;

        _context.Credentials.Remove(credential);
        await _context.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<bool> RenameCredentialAsync(
        Guid userId,
        Guid credentialId,
        string newName,
        CancellationToken cancellationToken = default)
    {
        var credential = await _context.Credentials
            .FirstOrDefaultAsync(c => c.Id == credentialId && c.UserId == userId, cancellationToken);

        if (credential == null)
            return false;

        credential.Name = newName;
        await _context.SaveChangesAsync(cancellationToken);

        return true;
    }

    private static string? DetermineDeviceType(Guid aaguid)
    {
        // Common AAGUID mappings (partial list)
        // In production, maintain a comprehensive database of AAGUIDs
        var knownDevices = new Dictionary<Guid, string>
        {
            // Apple devices
            { Guid.Parse("00000000-0000-0000-0000-000000000000"), "platform" }, // Generic platform authenticator
            { Guid.Parse("adce0002-35bc-c60a-648b-0b25f1f05503"), "phone" }, // Touch ID (iPhone)
            { Guid.Parse("08987058-cadc-4b81-b6e1-30de50dcbe96"), "computer" }, // Touch ID (Mac)

            // YubiKey
            { Guid.Parse("2fc0579f-8113-47ea-b116-bb5a8db9202a"), "security-key" }, // YubiKey 5 NFC
            { Guid.Parse("c1f9a0bc-1dd2-404a-b27f-8e29047a43fd"), "security-key" }, // YubiKey 5Ci
            { Guid.Parse("ee882879-721c-4913-9775-3dfcce97072a"), "security-key" }, // YubiKey 5 Series

            // Windows Hello
            { Guid.Parse("6028b017-b1d4-4c02-b4b3-afcdafc96bb2"), "computer" }, // Windows Hello Hardware
            { Guid.Parse("dd4ec289-e01d-41c9-bb89-70fa845d4bf2"), "computer" }, // Windows Hello Software
        };

        return knownDevices.TryGetValue(aaguid, out var deviceType) ? deviceType : "unknown";
    }

    private static bool CompareByteArrays(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length != b.Length)
            return false;

        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
                return false;
        }

        return true;
    }
}
