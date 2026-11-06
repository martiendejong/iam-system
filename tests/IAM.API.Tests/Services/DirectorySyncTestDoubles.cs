using System.Net;
using IAM.Core.Services;

namespace IAM.API.Tests.Services;

/// <summary>Fake DNS: host name -> addresses; unknown hosts do not resolve.</summary>
public class FakeHostResolver : IHostResolver
{
    public const string PublicHost = "ldap.public-directory.test";
    public const string PrivateHost = "ldap.internal-directory.test";
    public const string MixedHost = "ldap.mixed-directory.test";

    private readonly Dictionary<string, IPAddress[]> _map = new(StringComparer.OrdinalIgnoreCase)
    {
        [PublicHost] = new[] { IPAddress.Parse("93.184.216.34") },
        [PrivateHost] = new[] { IPAddress.Parse("10.0.0.5") },
        [MixedHost] = new[] { IPAddress.Parse("93.184.216.34"), IPAddress.Parse("10.0.0.5") },
    };

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct = default) =>
        Task.FromResult(_map.TryGetValue(host, out var addresses) ? addresses : Array.Empty<IPAddress>());
}

/// <summary>Fake directory server: returns the configured entries and records what it was asked.</summary>
public class FakeLdapDirectoryClient : ILdapDirectoryClient
{
    public List<DirectoryEntry> Entries { get; set; } = new();
    public List<(string Url, string BindDn, string BindPassword, string Filter)> Calls { get; } = new();

    public Task<IReadOnlyList<DirectoryEntry>> SearchAsync(
        string ldapUrl, string bindDn, string bindPassword, string searchBase, string filter,
        IReadOnlyCollection<string> attributes, CancellationToken ct = default)
    {
        Calls.Add((ldapUrl, bindDn, bindPassword, filter));
        return Task.FromResult<IReadOnlyList<DirectoryEntry>>(Entries.ToList());
    }

    public Task<DirectoryConnectionInfo> TestAsync(
        string ldapUrl, string bindDn, string bindPassword, string searchBase, string filter, CancellationToken ct = default)
    {
        Calls.Add((ldapUrl, bindDn, bindPassword, filter));
        return Task.FromResult(new DirectoryConnectionInfo(Entries.Count, "TestLDAP"));
    }

    public static DirectoryEntry Person(string email, string? phone = null, string? firstName = "Dir", string? lastName = "User", params string[] memberOf)
    {
        var attrs = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["mail"] = new[] { email },
            ["givenName"] = new[] { firstName ?? "" },
            ["sn"] = new[] { lastName ?? "" },
        };
        if (phone != null) attrs["telephoneNumber"] = new[] { phone };
        if (memberOf.Length > 0) attrs["memberOf"] = memberOf;
        return new DirectoryEntry($"cn={email},ou=people,dc=example,dc=test", attrs);
    }
}
