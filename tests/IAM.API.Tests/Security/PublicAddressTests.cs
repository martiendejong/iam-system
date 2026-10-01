using System.Net;
using IAM.Core.Security;
using Xunit;

namespace IAM.API.Tests.Security;

/// <summary>Task 4702: the shared "is this a public internet address" rule behind the webhook SSRF guard.</summary>
public class PublicAddressTests
{
    [Theory]
    // IPv4: loopback, unspecified, private, CGNAT, link-local (incl. cloud metadata), multicast, reserved, doc ranges
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.254")]
    [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")]
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.0.1")]
    [InlineData("192.168.255.255")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.255")]
    [InlineData("169.254.169.254")]
    [InlineData("169.254.0.1")]
    [InlineData("192.0.0.1")]
    [InlineData("192.0.2.10")]
    [InlineData("198.18.0.1")]
    [InlineData("198.19.255.255")]
    [InlineData("198.51.100.7")]
    [InlineData("203.0.113.7")]
    [InlineData("224.0.0.1")]
    [InlineData("239.255.255.255")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    // IPv6
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("::2")]                       // IPv4-compatible
    [InlineData("::127.0.0.1")]
    [InlineData("::ffff:127.0.0.1")]          // IPv4-mapped loopback
    [InlineData("::ffff:10.0.0.1")]           // IPv4-mapped private
    [InlineData("::ffff:169.254.169.254")]    // IPv4-mapped metadata
    [InlineData("::ffff:192.168.1.1")]
    [InlineData("64:ff9b::7f00:1")]           // NAT64 of 127.0.0.1
    [InlineData("64:ff9b::a9fe:a9fe")]        // NAT64 of 169.254.169.254
    [InlineData("2002:7f00:1::")]             // 6to4 of 127.0.0.1
    [InlineData("2002:a9fe:a9fe::1")]         // 6to4 of 169.254.169.254
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2")] // Teredo
    [InlineData("2001:db8::1")]               // documentation
    [InlineData("fe80::1")]                   // link-local
    [InlineData("febf::1")]
    [InlineData("fec0::1")]                   // site-local
    [InlineData("fc00::1")]                   // unique local
    [InlineData("fd12:3456:789a::1")]
    [InlineData("ff02::1")]                   // multicast
    [InlineData("fd00:ec2::254")]             // AWS IPv6 metadata (unique local)
    public void IsPublic_False_ForInternalAndSpecialAddresses(string address)
    {
        Assert.False(PublicAddress.IsPublic(IPAddress.Parse(address)), address);
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    [InlineData("172.15.255.255")]            // just below 172.16/12
    [InlineData("172.32.0.1")]                // just above
    [InlineData("100.63.255.255")]            // just below CGNAT
    [InlineData("100.128.0.1")]               // just above
    [InlineData("169.253.1.1")]
    [InlineData("192.169.0.1")]
    [InlineData("198.20.0.1")]
    [InlineData("223.255.255.255")]
    [InlineData("::ffff:8.8.8.8")]            // IPv4-mapped public
    [InlineData("64:ff9b::808:808")]          // NAT64 of 8.8.8.8
    [InlineData("2002:808:808::1")]           // 6to4 of 8.8.8.8
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2a00:1450:4001:81b::200e")]
    public void IsPublic_True_ForPublicAddresses(string address)
    {
        Assert.True(PublicAddress.IsPublic(IPAddress.Parse(address)), address);
    }
}
