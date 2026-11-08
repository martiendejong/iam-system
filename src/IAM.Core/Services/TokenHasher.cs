using System.Security.Cryptography;
using System.Text;

namespace IAM.Core.Services;

/// <summary>
/// One-way hash for one-time tokens that are e-mailed or handed out but must not be stored raw
/// (magic links, invitations). SHA-256 over the UTF-8 bytes, lowercase hex (64 chars).
/// </summary>
public static class TokenHasher
{
    public static string Sha256Hex(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
