namespace DomainNotice.Server.Directory;

/// <summary>One directory object as read from LDAP, already decoded (GUID, SID, numbers).</summary>
public sealed class LdapDirectoryEntry
{
    public Guid ObjectGuid { get; init; }
    public string DistinguishedName { get; init; } = "";
    public IReadOnlyList<string> ObjectClasses { get; init; } = [];
    public string? Cn { get; init; }
    public string? Ou { get; init; }
    public string? Name { get; init; }
    public string? SamAccountName { get; init; }
    public string? DisplayName { get; init; }
    public string? UserPrincipalName { get; init; }
    public string? DnsHostName { get; init; }
    /// <summary>String form, S-1-5-21-...</summary>
    public string? ObjectSid { get; init; }
    public int? UserAccountControl { get; init; }
    /// <summary>Group members as DNs (all ranges already retrieved).</summary>
    public IReadOnlyList<string> Members { get; init; } = [];
    public long UsnChanged { get; init; }
}

public sealed record LdapRootDse(long HighestCommittedUsn, string? DnsHostName);

/// <summary>Where the sync reads from. The real one is LDAP; tests use a fake.</summary>
public interface ILdapSource
{
    /// <summary>Connects and binds with the service account (trying Directory:Hosts in order).</summary>
    Task<ILdapSourceSession> OpenAsync(CancellationToken ct);
}

public interface ILdapSourceSession : IAsyncDisposable
{
    /// <summary>The configured host that answered.</summary>
    string Host { get; }
    Task<LdapRootDse> ReadRootDseAsync(CancellationToken ct);
    /// <summary>
    /// Paged subtree search under BaseDn for <see cref="LdapFilters.SyncedObjects"/>; when <paramref name="usnChangedAfter"/>
    /// is set, only objects with uSNChanged greater than it. Throws if any page fails.
    /// </summary>
    IAsyncEnumerable<LdapDirectoryEntry> SearchObjectsAsync(long? usnChangedAfter, CancellationToken ct);
    /// <summary>NetBIOS name of the domain of BaseDn (<see cref="DomainNetBiosName"/>); null when it cannot be read.</summary>
    Task<string?> ReadNetBiosNameAsync(CancellationToken ct) => Task.FromResult<string?>(null);
}
