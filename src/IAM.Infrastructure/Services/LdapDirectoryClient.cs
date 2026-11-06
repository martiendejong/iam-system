using System.DirectoryServices.Protocols;
using System.Net;
using IAM.Core.Services;

namespace IAM.Infrastructure.Services;

/// <summary>The real LDAP wire calls for directory sync (moved out of DirectorySyncService, task 4698).</summary>
public class LdapDirectoryClient : ILdapDirectoryClient
{
    public Task<IReadOnlyList<DirectoryEntry>> SearchAsync(
        string ldapUrl, string bindDn, string bindPassword, string searchBase, string filter,
        IReadOnlyCollection<string> attributes, CancellationToken ct = default)
    {
        using var connection = CreateConnection(ldapUrl);
        connection.Bind(new NetworkCredential(bindDn, bindPassword));

        var request = new SearchRequest(searchBase, filter, SearchScope.Subtree, attributes.ToArray());
        var response = (SearchResponse)connection.SendRequest(request);

        var entries = new List<DirectoryEntry>();
        foreach (SearchResultEntry entry in response.Entries)
        {
            var attrs = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (DirectoryAttribute attribute in entry.Attributes.Values)
            {
                var values = new List<string>(attribute.Count);
                for (var i = 0; i < attribute.Count; i++)
                    values.Add(attribute[i]?.ToString() ?? string.Empty);
                attrs[attribute.Name] = values;
            }
            entries.Add(new DirectoryEntry(entry.DistinguishedName, attrs));
        }

        return Task.FromResult<IReadOnlyList<DirectoryEntry>>(entries);
    }

    public Task<DirectoryConnectionInfo> TestAsync(
        string ldapUrl, string bindDn, string bindPassword, string searchBase, string filter, CancellationToken ct = default)
    {
        using var connection = CreateConnection(ldapUrl);
        connection.Bind(new NetworkCredential(bindDn, bindPassword));

        // Try a simple search to verify permissions
        var searchRequest = new SearchRequest(searchBase, filter, SearchScope.Subtree, "dn") { SizeLimit = 5 };
        var response = (SearchResponse)connection.SendRequest(searchRequest);

        // Try to detect server type from root DSE
        string? serverType = null;
        try
        {
            var rootDseRequest = new SearchRequest(
                "", "(objectClass=*)", SearchScope.Base, "vendorName", "vendorVersion", "isGlobalCatalogReady");
            var rootDseResponse = (SearchResponse)connection.SendRequest(rootDseRequest);
            if (rootDseResponse.Entries.Count > 0)
            {
                var entry = rootDseResponse.Entries[0];
                if (entry.Attributes["isGlobalCatalogReady"] != null)
                    serverType = "Active Directory";
                else if (entry.Attributes["vendorName"] != null)
                    serverType = entry.Attributes["vendorName"][0]?.ToString();
            }
        }
        catch
        {
            // Root DSE detection is optional
            serverType = "LDAP";
        }

        return Task.FromResult(new DirectoryConnectionInfo(response.Entries.Count, serverType ?? "LDAP"));
    }

    private static LdapConnection CreateConnection(string ldapUrl)
    {
        var uri = new Uri(ldapUrl);
        var port = uri.Port > 0 ? uri.Port : 636;
        var connection = new LdapConnection(new LdapDirectoryIdentifier(uri.Host, port))
        {
            AuthType = AuthType.Basic,
            Timeout = TimeSpan.FromSeconds(30)
        };

        connection.SessionOptions.ProtocolVersion = 3;
        // ldaps only (task 4698): the bind password and directory data never travel in clear text.
        connection.SessionOptions.SecureSocketLayer = true;

        return connection;
    }
}
