using System.Net;

namespace IAM.Infrastructure.Services;

/// <summary>CIDR membership for IPv4 and IPv6. IPv4-mapped IPv6 addresses compare as IPv4.</summary>
public static class CidrMatcher
{
    public static bool Contains(string? ipAddress, string? cidr)
    {
        if (string.IsNullOrWhiteSpace(ipAddress) || string.IsNullOrWhiteSpace(cidr)) return false;
        if (!IPAddress.TryParse(ipAddress.Trim(), out var ip)) return false;

        var parts = cidr.Trim().Split('/');
        if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var network)) return false;

        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (network.IsIPv4MappedToIPv6) network = network.MapToIPv4();
        if (ip.AddressFamily != network.AddressFamily) return false;

        var ipBytes = ip.GetAddressBytes();
        var netBytes = network.GetAddressBytes();
        var maxPrefix = ipBytes.Length * 8;

        var prefix = maxPrefix;
        if (parts.Length == 2 && (!int.TryParse(parts[1], out prefix) || prefix < 0 || prefix > maxPrefix))
            return false;

        for (var i = 0; i < ipBytes.Length; i++)
        {
            var bits = Math.Min(8, Math.Max(0, prefix - i * 8));
            var mask = (byte)(0xFF << (8 - bits));
            if ((ipBytes[i] & mask) != (netBytes[i] & mask)) return false;
        }
        return true;
    }
}
