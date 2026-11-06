

namespace IAM.Core.Services;

/// <summary>
/// Limits for a client certificate the IAM certificate authority issues (task 5148): how long it may live, which RSA key
/// sizes are accepted, and which free-text name fields are accepted. The caller
/// of the issue endpoint used to choose all of these without limit, and the name fields were pasted into the subject
/// as-is, so ", OU=x" inside a name added a name part of the attacker's choosing.
/// </summary>
public static class CertificateIssuingRules
{
    public const int DefaultValidityDays = 365;
    public const int MaxValidityDays = 825;
    public const int MaxNameLength = 64;

    public static readonly IReadOnlyList<int> AllowedKeySizes = new[] { 2048, 3072, 4096 };

    /// <summary>The first problem with the requested limits and names, or null when the request is acceptable.</summary>
    public static string? Check(int validityDays, int keySizeBits, string? commonName, string? organization, string? organizationalUnit)
    {
        if (validityDays < 1 || validityDays > MaxValidityDays)
            return $"ValidityDays must be between 1 and {MaxValidityDays}";

        if (!AllowedKeySizes.Contains(keySizeBits))
            return $"KeySizeBits must be one of {string.Join(", ", AllowedKeySizes)}";

        return CheckName("CommonName", commonName, required: true)
            ?? CheckName("Organization", organization, required: false)
            ?? CheckName("OrganizationalUnit", organizationalUnit, required: false);
    }

    private static string? CheckName(string field, string? value, bool required)
    {
        if (string.IsNullOrWhiteSpace(value))
            return required ? $"{field} is required" : null;

        if (value.Length > MaxNameLength)
            return $"{field} must be at most {MaxNameLength} characters";

        // Line breaks and other control characters have no place in a name part (and are not representable safely).
        if (value.Any(char.IsControl))
            return $"{field} must not contain control characters";

        return null;
    }
}
