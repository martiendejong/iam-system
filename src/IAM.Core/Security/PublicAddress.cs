using System.Net;
using System.Net.Sockets;

namespace IAM.Core.Security;

/// <summary>
/// Decides whether an IP address is a public, internet-routable destination (task 4702). Default-deny:
/// loopback, unspecified, private (RFC 1918), shared/CGNAT, link-local (incl. the 169.254.169.254 cloud
/// metadata address), multicast, reserved, benchmarking and documentation ranges are all refused, for IPv4
/// and IPv6. IPv6 forms that embed an IPv4 address (IPv4-mapped, IPv4-compatible, NAT64, 6to4) are judged
/// on the embedded address.
/// </summary>
public static class PublicAddress
{
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsPublicV4(address.GetAddressBytes()),
            AddressFamily.InterNetworkV6 => IsPublicV6(address),
            _ => false
        };
    }

    private static bool IsPublicV4(byte[] b)
    {
        int a = b[0], c = b[1], d = b[2];
        return !(
            a == 0                                    // "this network", 0.0.0.0
            || a == 10                                // RFC 1918
            || (a == 100 && c >= 64 && c <= 127)      // 100.64.0.0/10 shared address space
            || a == 127                               // loopback
            || (a == 169 && c == 254)                 // link-local, incl. cloud metadata
            || (a == 172 && c >= 16 && c <= 31)       // RFC 1918
            || (a == 192 && c == 0 && (d == 0 || d == 2)) // 192.0.0.0/24 IETF, 192.0.2.0/24 TEST-NET-1
            || (a == 192 && c == 168)                 // RFC 1918
            || (a == 192 && c == 88 && d == 99)       // 6to4 relay anycast
            || (a == 198 && (c == 18 || c == 19))     // benchmarking
            || (a == 198 && c == 51 && d == 100)      // TEST-NET-2
            || (a == 203 && c == 0 && d == 113)       // TEST-NET-3
            || a >= 224);                             // multicast, reserved, broadcast
    }

    private static bool IsPublicV6(IPAddress address)
    {
        var b = address.GetAddressBytes();

        // :: and ::1, and ::a.b.c.d (deprecated IPv4-compatible): first 96 bits zero.
        if (b.Take(12).All(x => x == 0))
            return false;

        // 64:ff9b::/96 NAT64: judge the embedded IPv4 (last 4 bytes).
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b)
            return b.Skip(4).Take(8).All(x => x == 0) && IsPublicV4(new[] { b[12], b[13], b[14], b[15] });

        // 2002::/16 6to4: embedded IPv4 in bytes 2-5.
        if (b[0] == 0x20 && b[1] == 0x02)
            return IsPublicV4(new[] { b[2], b[3], b[4], b[5] });

        // 2001::/32 Teredo and 2001:db8::/32 documentation.
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00)
            return false;
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8)
            return false;

        // Only global unicast 2000::/3 is internet-routable; this also drops fc00::/7 unique local,
        // fe80::/10 link-local, fec0::/10 site-local and ff00::/8 multicast.
        return (b[0] & 0xe0) == 0x20;
    }
}
