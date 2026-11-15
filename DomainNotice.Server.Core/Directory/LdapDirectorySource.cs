using System.DirectoryServices.Protocols;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using DomainNotice.Server.Options;
using Microsoft.Extensions.Options;

namespace DomainNotice.Server.Directory;

/// <summary>Decoding of AD attribute values.</summary>
public static class LdapValues
{
    /// <summary>objectGUID bytes are in the same layout as <see cref="Guid(byte[])"/>.</summary>
    public static Guid? DecodeGuid(byte[]? bytes) => bytes is { Length: 16 } ? new Guid(bytes) : null;

    /// <summary>Binary SID → "S-1-5-21-...". Null when malformed.</summary>
    public static string? DecodeSid(byte[]? b)
    {
        if (b is null || b.Length < 8) return null;
        int revision = b[0], count = b[1];
        if (b.Length < 8 + 4 * count) return null;
        long authority = 0;
        for (var i = 2; i < 8; i++) authority = (authority << 8) | b[i];
        var sb = new StringBuilder("S-").Append(revision).Append('-').Append(authority);
        for (var i = 0; i < count; i++)
            sb.Append('-').Append(BitConverter.ToUInt32(b, 8 + 4 * i).ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    public static bool IsDisabled(int? userAccountControl) => userAccountControl is { } u && (u & 0x2) != 0;
}

/// <summary>The real <see cref="ILdapSource"/>: paged searches over a pooled service-account connection.</summary>
internal sealed class LdapDirectorySource(LdapConnector connector, IOptions<DirectoryOptions> options) : ILdapSource
{
    public const int PageSize = 500;

    internal static readonly string[] Attributes =
    [
        "objectGUID", "objectClass", "distinguishedName", "cn", "ou", "name", "sAMAccountName", "displayName",
        "userPrincipalName", "dNSHostName", "objectSid", "userAccountControl", "member", "uSNChanged",
    ];

    public async Task<ILdapSourceSession> OpenAsync(CancellationToken ct) =>
        new Session(await connector.LeaseServiceAsync(ct), options.Value.BaseDn.Trim());

    private sealed class Session(LdapLease lease, string baseDn) : ILdapSourceSession
    {
        public string Host => lease.Host;

        public async Task<LdapRootDse> ReadRootDseAsync(CancellationToken ct)
        {
            var resp = (SearchResponse)await Send(new SearchRequest("", "(objectClass=*)", SearchScope.Base,
                "highestCommittedUSN", "dnsHostName"), ct);
            var e = resp.Entries.Cast<SearchResultEntry>().FirstOrDefault()
                ?? throw new DirectoryConnectException("The rootDSE could not be read.");
            var usn = long.TryParse(Str(e, "highestCommittedUSN"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var u) ? u : 0;
            return new LdapRootDse(usn, Str(e, "dnsHostName"));
        }

        public async IAsyncEnumerable<LdapDirectoryEntry> SearchObjectsAsync(long? usnChangedAfter, [EnumeratorCancellation] CancellationToken ct)
        {
            var filter = usnChangedAfter is { } usn ? LdapFilters.SyncedObjectsChangedAfter(usn) : LdapFilters.SyncedObjects;
            var request = new SearchRequest(baseDn, filter, SearchScope.Subtree, Attributes);
            var page = new PageResultRequestControl(PageSize) { IsCritical = true };
            request.Controls.Add(page);
            while (true)
            {
                var resp = (SearchResponse)await Send(request, ct);
                foreach (SearchResultEntry e in resp.Entries)
                {
                    var entry = await DecodeAsync(e, ct);
                    if (entry is not null) yield return entry;
                }
                var pr = resp.Controls.OfType<PageResultResponseControl>().FirstOrDefault();
                if (pr is null || pr.Cookie is null || pr.Cookie.Length == 0) yield break;
                page.Cookie = pr.Cookie;
            }
        }

        private async Task<DirectoryResponse> Send(DirectoryRequest r, CancellationToken ct)
        {
            try
            {
                return await LdapConnector.SendAsync(lease.Connection, r, ct);
            }
            catch
            {
                lease.Broken = true;
                throw;
            }
        }

        private async Task<LdapDirectoryEntry?> DecodeAsync(SearchResultEntry e, CancellationToken ct)
        {
            var guid = LdapValues.DecodeGuid(Bin(e, "objectGUID"));
            if (guid is null) return null;
            var members = await ReadMembersAsync(e, ct);
            return new LdapDirectoryEntry
            {
                ObjectGuid = guid.Value,
                DistinguishedName = e.DistinguishedName,
                ObjectClasses = Strs(e, "objectClass"),
                Cn = Str(e, "cn"),
                Ou = Str(e, "ou"),
                Name = Str(e, "name"),
                SamAccountName = Str(e, "sAMAccountName"),
                DisplayName = Str(e, "displayName"),
                UserPrincipalName = Str(e, "userPrincipalName"),
                DnsHostName = Str(e, "dNSHostName"),
                ObjectSid = LdapValues.DecodeSid(Bin(e, "objectSid")),
                UserAccountControl = int.TryParse(Str(e, "userAccountControl"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var uac) ? uac : null,
                Members = members,
                UsnChanged = long.TryParse(Str(e, "uSNChanged"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var usn) ? usn : 0,
            };
        }

        /// <summary>
        /// AD returns large multi-valued attributes in ranges ("member;range=0-1499"). Follow the ranges with base
        /// searches until the last one ("member;range=N-*"), so big groups are not truncated.
        /// </summary>
        private async Task<IReadOnlyList<string>> ReadMembersAsync(SearchResultEntry e, CancellationToken ct)
        {
            var members = new List<string>();
            string? rangedName = null;
            foreach (string name in e.Attributes.AttributeNames)
            {
                if (name.Equals("member", StringComparison.OrdinalIgnoreCase)) members.AddRange(Strs(e, name));
                else if (name.StartsWith("member;range=", StringComparison.OrdinalIgnoreCase))
                {
                    members.AddRange(Strs(e, name));
                    rangedName = name;
                }
            }
            var guard = 0;
            while (rangedName is not null && !rangedName.EndsWith("-*", StringComparison.Ordinal) && guard++ < 10_000)
            {
                var range = rangedName["member;range=".Length..];
                var dash = range.IndexOf('-');
                if (dash < 0 || !int.TryParse(range[(dash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var end)) break;
                var resp = (SearchResponse)await Send(new SearchRequest(e.DistinguishedName, "(objectClass=*)", SearchScope.Base,
                    $"member;range={end + 1}-*"), ct);
                var next = resp.Entries.Cast<SearchResultEntry>().FirstOrDefault();
                rangedName = null;
                if (next is null) break;
                foreach (string name in next.Attributes.AttributeNames)
                {
                    if (!name.StartsWith("member;range=", StringComparison.OrdinalIgnoreCase)) continue;
                    members.AddRange(Strs(next, name));
                    rangedName = name;
                }
            }
            return members;
        }

        public ValueTask DisposeAsync() => lease.DisposeAsync();
    }

    private static string? Str(SearchResultEntry e, string name)
    {
        if (!e.Attributes.Contains(name)) return null;
        var values = e.Attributes[name].GetValues(typeof(string));
        return values.Length == 0 ? null : (string)values[0];
    }

    private static IReadOnlyList<string> Strs(SearchResultEntry e, string name) =>
        e.Attributes.Contains(name) ? e.Attributes[name].GetValues(typeof(string)).Cast<string>().ToList() : [];

    /// <summary>Binary attributes must be read as byte[]: the indexer returns a string when the bytes happen to be valid UTF-8.</summary>
    private static byte[]? Bin(SearchResultEntry e, string name)
    {
        if (!e.Attributes.Contains(name)) return null;
        var values = e.Attributes[name].GetValues(typeof(byte[]));
        return values.Length == 0 ? null : (byte[])values[0];
    }
}
