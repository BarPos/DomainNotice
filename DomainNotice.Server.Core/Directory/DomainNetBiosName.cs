using System.DirectoryServices.Protocols;

namespace DomainNotice.Server.Directory;

/// <summary>
/// The NetBIOS name ("CORP") of the one supported domain: the domain of Directory:BaseDn. Read from the domain's
/// crossRef object (CN=Partitions,&lt;configurationNamingContext&gt;, nCName = the domain DN, attribute nETBIOSName),
/// which any authenticated user may read. Domain sign-in compares it with the "Who am I?" answer; the sync stores it
/// (<see cref="Abstractions.SettingKeys.DirectoryNetBiosName"/>) for targeting.
/// </summary>
public static class DomainNetBiosName
{
    /// <summary>
    /// The domain DN of a BaseDn: its trailing DC= components ("OU=Staff,DC=corp,DC=test" → "DC=corp,DC=test").
    /// Empty when BaseDn has no DC components.
    /// </summary>
    public static string DomainDn(string? baseDn)
    {
        var parts = DistinguishedNames.Split(baseDn);
        var dcs = new List<string>();
        for (var i = parts.Count - 1; i >= 0; i--)
        {
            var p = parts[i];
            var eq = p.IndexOf('=');
            if (eq <= 0 || !p[..eq].Trim().Equals("dc", StringComparison.OrdinalIgnoreCase)) break;
            dcs.Insert(0, p);
        }
        return string.Join(",", dcs);
    }

    /// <summary>
    /// The nETBIOSName of the crossRef whose nCName equals <paramref name="domainDn"/> exactly (normalised, not a
    /// suffix match, so a child domain never matches). Null when there is not exactly one such crossRef.
    /// </summary>
    public static string? Pick(IEnumerable<(string? NcName, string? NetBiosName)> crossRefs, string domainDn)
    {
        if (string.IsNullOrWhiteSpace(domainDn)) return null;
        var want = DistinguishedNames.Normalize(domainDn);
        var names = crossRefs
            .Where(c => !string.IsNullOrWhiteSpace(c.NcName) && !string.IsNullOrWhiteSpace(c.NetBiosName)
                        && DistinguishedNames.Normalize(c.NcName) == want)
            .Select(c => c.NetBiosName!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return names.Count == 1 ? names[0] : null;
    }

    /// <summary>Reads the NetBIOS name over a bound connection. Null when it cannot be determined.</summary>
    internal static async Task<string?> ReadAsync(LdapConnection conn, string baseDn, CancellationToken ct)
    {
        var root = (SearchResponse)await LdapConnector.SendAsync(conn,
            new SearchRequest("", "(objectClass=*)", SearchScope.Base, "configurationNamingContext", "defaultNamingContext"), ct);
        var rootEntry = root.Entries.Cast<SearchResultEntry>().FirstOrDefault();
        if (rootEntry is null) return null;
        var configNc = Str(rootEntry, "configurationNamingContext");
        if (string.IsNullOrWhiteSpace(configNc)) return null;
        var domainDn = DomainDn(baseDn);
        if (domainDn.Length == 0) domainDn = Str(rootEntry, "defaultNamingContext") ?? "";
        if (domainDn.Length == 0) return null;

        var resp = (SearchResponse)await LdapConnector.SendAsync(conn,
            new SearchRequest("CN=Partitions," + configNc, "(&(objectClass=crossRef)(nETBIOSName=*))", SearchScope.OneLevel,
                "nCName", "nETBIOSName"), ct);
        return Pick(resp.Entries.Cast<SearchResultEntry>().Select(e => (Str(e, "nCName"), Str(e, "nETBIOSName"))), domainDn);
    }

    private static string? Str(SearchResultEntry e, string name)
    {
        if (!e.Attributes.Contains(name)) return null;
        var v = e.Attributes[name].GetValues(typeof(string));
        return v.Length == 0 ? null : (string)v[0];
    }
}
