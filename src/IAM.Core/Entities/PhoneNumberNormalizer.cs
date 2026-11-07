using System.Text.RegularExpressions;

namespace IAM.Core.Entities;

/// <summary>One canonical phone format shared by SMS request, SMS verify and phone verification.</summary>
public static class PhoneNumberNormalizer
{
    private static readonly Regex E164 = new(@"^\+[1-9][0-9]{7,14}$", RegexOptions.Compiled);

    /// <summary>
    /// Strips spaces, dots, dashes and parentheses; turns a leading 00 into +. Returns null for empty
    /// input. The result is not guaranteed to be valid E.164: check <see cref="IsValid"/>.
    /// </summary>
    public static string? Normalize(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var chars = phone.Where(c => char.IsDigit(c) || c == '+').ToArray();
        var s = new string(chars);
        if (s.StartsWith("00")) s = "+" + s[2..];
        return s.Length == 0 ? null : s;
    }

    public static bool IsValid(string? normalized) => normalized != null && E164.IsMatch(normalized);
}
