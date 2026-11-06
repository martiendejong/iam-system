namespace IAM.Core.Services;

/// <summary>One directory entry as returned by a search: its DN and its attributes (names are case-insensitive).</summary>
public sealed record DirectoryEntry(string DistinguishedName, IReadOnlyDictionary<string, IReadOnlyList<string>> Attributes)
{
    /// <summary>The first value of <paramref name="attributeName"/>, or null.</summary>
    public string? FirstValue(string? attributeName)
    {
        if (string.IsNullOrWhiteSpace(attributeName)) return null;
        return Attributes.TryGetValue(attributeName, out var values) && values.Count > 0 ? values[0] : null;
    }

    public IReadOnlyList<string> Values(string attributeName) =>
        Attributes.TryGetValue(attributeName, out var values) ? values : Array.Empty<string>();
}

public sealed record DirectoryConnectionInfo(int EntryCount, string ServerType);

/// <summary>
/// The LDAP wire calls behind directory sync (task 4698), so the sync loop and its tenant scoping can be
/// tested without a directory server. The URL has already been checked (ldaps, public address) by the caller.
/// </summary>
public interface ILdapDirectoryClient
{
    Task<IReadOnlyList<DirectoryEntry>> SearchAsync(
        string ldapUrl, string bindDn, string bindPassword, string searchBase, string filter,
        IReadOnlyCollection<string> attributes, CancellationToken ct = default);

    /// <summary>Binds and runs a small search; throws on failure.</summary>
    Task<DirectoryConnectionInfo> TestAsync(
        string ldapUrl, string bindDn, string bindPassword, string searchBase, string filter, CancellationToken ct = default);
}
