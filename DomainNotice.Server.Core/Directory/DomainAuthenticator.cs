using System.DirectoryServices.Protocols;
using System.Text;
using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DomainNotice.Server.Directory;

public enum DomainAuthStatus
{
    Success = 0,
    /// <summary>Wrong user name or password (LDAP 49).</summary>
    InvalidCredentials = 1,
    /// <summary>Password correct, but in none of AdminGroups/ViewerGroups.</summary>
    NotAuthorized = 2,
    /// <summary>No DC reachable or another LDAP failure that is not the user's fault.</summary>
    Unavailable = 3,
    NotConfigured = 4,
}

/// <param name="Error">For the log and the audit log only; never shown to the person signing in.</param>
/// <param name="ObjectGuid">objectGUID of the user object (Success), so the Console can re-check the session
/// against the synced directory.</param>
public sealed record DomainAuthResult(DomainAuthStatus Status, string? UserName = null, string? DisplayName = null,
    string? Role = null, string? Error = null, Guid? ObjectGuid = null);

/// <summary>Domain sign-in: bind as the user, then map group membership to a Console role. Used by Accounts.</summary>
public interface IDomainAuthenticator
{
    /// <summary>Directory configured with allowed connection settings and at least one admin or viewer group.</summary>
    bool IsAvailable { get; }
    Task<DomainAuthResult> AuthenticateAsync(string userName, string password, CancellationToken ct);
}

/// <summary>Role rules of the domain sign-in, kept separate from LDAP so they can be tested.</summary>
public static class DomainRoles
{
    /// <summary>
    /// Admin when in any admin group; otherwise Viewer when in any viewer group (P4); otherwise null.
    /// Empty group lists are skipped (no LDAP query).
    /// </summary>
    public static async Task<string?> ResolveAsync(Func<Task<List<string>>> adminGroupDns, Func<Task<List<string>>> viewerGroupDns,
        Func<IReadOnlyCollection<string>, Task<bool>> isMemberOfAny)
    {
        var admin = await adminGroupDns();
        if (admin.Count > 0 && await isMemberOfAny(admin)) return ConsoleRoles.Admin;
        var viewer = await viewerGroupDns();
        if (viewer.Count > 0 && await isMemberOfAny(viewer)) return ConsoleRoles.Viewer;
        return null;
    }

    /// <summary>
    /// The DN of a group configured by name: <paramref name="matches"/> are the DNs of all groups whose sAMAccountName
    /// OR cn equals the name (one search). Accepted only when they are exactly one distinct group; otherwise null
    /// (missing or ambiguous), so a look-alike group (another group whose sAMAccountName equals the configured cn,
    /// or the other way round) never grants access. Configure groups by DN to rule this out completely.
    /// </summary>
    public static string? PickGroupDn(IReadOnlyList<string> matches)
    {
        var distinct = matches.Where(m => !string.IsNullOrWhiteSpace(m))
            .GroupBy(DistinguishedNames.Normalize).Select(g => g.First()).ToList();
        return distinct.Count == 1 ? distinct[0] : null;
    }

    /// <summary>
    /// Checks that the account that bound belongs to the one supported domain. <paramref name="whoAmI"/> is the
    /// "DOMAIN\sam" of the RFC 4532 "Who am I?" answer (required), <paramref name="netBiosName"/> the NetBIOS name of
    /// the domain of BaseDn, <paramref name="typed"/> what the person typed. Returns the sAMAccountName to look up, or
    /// the reason for refusing (for the audit log).
    /// </summary>
    public static (string? Sam, string? Refusal) CheckIdentity(string? whoAmI, string? netBiosName, string typed)
    {
        if (string.IsNullOrWhiteSpace(netBiosName)) return (null, "the NetBIOS name of the domain is not known");
        var typedPrefix = BindNames.DomainPrefix(typed);
        if (typedPrefix is not null && !string.Equals(typedPrefix.Trim(), netBiosName, StringComparison.OrdinalIgnoreCase))
            return (null, $"typed domain {typedPrefix} is not the configured domain {netBiosName}");
        if (string.IsNullOrWhiteSpace(whoAmI)) return (null, "the directory did not answer \"Who am I?\"");
        var domain = BindNames.DomainPrefix(whoAmI);
        var sam = BindNames.SamAccountName(whoAmI);
        if (domain is null || sam.Length == 0 || whoAmI.Count(c => c == '\\') != 1)
            return (null, $"unexpected \"Who am I?\" answer {whoAmI}");
        if (!string.Equals(domain.Trim(), netBiosName, StringComparison.OrdinalIgnoreCase))
            return (null, $"the account {whoAmI} is not in the configured domain {netBiosName}");
        return (sam, null);
    }
}

internal sealed class LdapDomainAuthenticator(
    LdapConnector connector,
    IOptions<DirectoryOptions> options,
    IHostEnvironment env,
    ILogger<LdapDomainAuthenticator> logger) : IDomainAuthenticator
{
    private const string WhoAmIOid = "1.3.6.1.4.1.4203.1.11.3";

    /// <summary>(domain DN, NetBIOS name) of the last successful lookup; read again when BaseDn's domain changes.</summary>
    private volatile Tuple<string, string>? _netBios;

    private DirectoryOptions O => options.Value;

    public bool IsAvailable =>
        O.IsConfigured && LdapSecurityPolicy.ValidateConnection(O, env.IsDevelopment()) is null
        && (O.AdminGroups.Any(g => !string.IsNullOrWhiteSpace(g)) || O.ViewerGroups.Any(g => !string.IsNullOrWhiteSpace(g)));

    /// <summary>
    /// Only one domain is supported: the domain of Directory:BaseDn. The account that bound is taken from "Who am I?"
    /// (never from the typed name) and must be in that domain (its NetBIOS name); a typed "DOMAIN\" prefix must name
    /// it too. Anything else fails closed.
    /// </summary>
    public async Task<DomainAuthResult> AuthenticateAsync(string userName, string password, CancellationToken ct)
    {
        if (!O.IsConfigured) return new(DomainAuthStatus.NotConfigured, Error: "Domain sign-in is not configured.");
        var configError = LdapSecurityPolicy.ValidateConnection(O, env.IsDevelopment());
        if (configError is not null) return new(DomainAuthStatus.NotConfigured, Error: configError);
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password))
            return new(DomainAuthStatus.InvalidCredentials, Error: "Enter your user name and password.");
        if (!BindNames.IsAcceptedSignInName(userName))
            return new(DomainAuthStatus.InvalidCredentials, Error: @"sign-in name refused (use name, DOMAIN\name or name@domain; no DNs)");

        LdapLease lease;
        try
        {
            lease = await connector.BindAsUserAsync(userName, password, ct);
        }
        catch (DirectoryConnectException e) when (e.InvalidCredentials)
        {
            return new(DomainAuthStatus.InvalidCredentials, Error: "The user name or password is incorrect.");
        }
        catch (DirectoryConnectException e)
        {
            return new(DomainAuthStatus.Unavailable, Error: e.Message);
        }

        await using (lease)
        {
            try
            {
                var conn = lease.Connection;
                var netBios = await GetNetBiosNameAsync(conn, ct);
                if (netBios is null)
                {
                    logger.LogError("Domain sign-in: the NetBIOS name of the domain of {BaseDn} could not be read (crossRef " +
                                    "under CN=Partitions of the configuration naming context). Domain sign-in fails until it can.", O.BaseDn);
                    return new(DomainAuthStatus.Unavailable, Error: "the NetBIOS name of the configured domain could not be read");
                }
                var ntName = await WhoAmIAsync(conn, ct);
                var (sam, refusal) = DomainRoles.CheckIdentity(ntName, netBios, userName);
                if (sam is null)
                {
                    logger.LogWarning("Domain sign-in refused for {User}: {Reason}.", userName.Trim(), refusal);
                    return new(DomainAuthStatus.NotAuthorized, ntName, Error: refusal);
                }

                var user = await FindUserAsync(conn, sam, ct);
                if (user is null)
                {
                    logger.LogWarning("Domain sign-in: {User} bound but the user object was not found under {BaseDn}.", ntName, O.BaseDn);
                    return new(DomainAuthStatus.NotAuthorized, ntName, Error: "the user object was not found under the configured directory base");
                }
                var display = Attr(user, "displayName");
                var guid = LdapValues.DecodeGuid(Bin(user, "objectGUID"));
                var name = $@"{netBios}\{Attr(user, "sAMAccountName") ?? sam}";

                var role = await DomainRoles.ResolveAsync(
                    () => ResolveGroupsAsync(conn, O.AdminGroups, ct),
                    () => ResolveGroupsAsync(conn, O.ViewerGroups, ct),
                    dns => IsMemberAsync(conn, user.DistinguishedName, dns, ct));
                if (role is not null) return new(DomainAuthStatus.Success, name, display, role, ObjectGuid: guid);

                logger.LogInformation("Domain sign-in: {User} is not in an admin or viewer group.", name);
                return new(DomainAuthStatus.NotAuthorized, name, display, Error: "not in an admin or viewer group");
            }
            catch (Exception e) when (e is LdapException or DirectoryOperationException)
            {
                logger.LogWarning(e, "Domain sign-in: directory lookup failed for {User}.", userName.Trim());
                return new(DomainAuthStatus.Unavailable, Error: "The directory could not be read: " + e.Message);
            }
        }
    }

    private async Task<string?> GetNetBiosNameAsync(LdapConnection conn, CancellationToken ct)
    {
        var domainDn = DistinguishedNames.Normalize(DomainNetBiosName.DomainDn(O.BaseDn));
        if (_netBios is { } cached && cached.Item1 == domainDn) return cached.Item2;
        var name = await DomainNetBiosName.ReadAsync(conn, O.BaseDn.Trim(), ct);
        if (name is not null)
        {
            _netBios = Tuple.Create(domainDn, name);
            logger.LogInformation("Directory domain {Domain}: NetBIOS name {NetBios}.", domainDn, name);
        }
        return name;
    }

    /// <summary>
    /// RFC 4532 "Who am I?": AD answers "u:DOMAIN\sam", the account the bind actually used. Required: null when the
    /// server does not answer it or answers in another form, and sign-in then fails.
    /// </summary>
    private async Task<string?> WhoAmIAsync(LdapConnection conn, CancellationToken ct)
    {
        try
        {
            var resp = (ExtendedResponse)await LdapConnector.SendAsync(conn, new ExtendedRequest(WhoAmIOid), ct);
            return BindNames.ParseAuthzId(resp.ResponseValue is { Length: > 0 } v ? Encoding.UTF8.GetString(v) : null).NtName;
        }
        catch (Exception e) when (e is LdapException or DirectoryOperationException)
        {
            logger.LogWarning(e, "The directory did not answer \"Who am I?\"; domain sign-in fails.");
            return null;
        }
    }

    /// <summary>The user object with this sAMAccountName under BaseDn (unique within the domain), or null.</summary>
    private async Task<SearchResultEntry?> FindUserAsync(LdapConnection conn, string sam, CancellationToken ct)
    {
        var resp = (SearchResponse)await LdapConnector.SendAsync(conn,
            new SearchRequest(O.BaseDn.Trim(), LdapFilters.UserBySamAccountName(sam), SearchScope.Subtree,
                "distinguishedName", "sAMAccountName", "displayName", "objectGUID"), ct);
        return resp.Entries.Count == 1 ? resp.Entries[0] : null;
    }

    /// <summary>
    /// Group names may be DNs (used as given) or names. A name is looked up with one search matching sAMAccountName
    /// OR cn under BaseDn and is used only when exactly one group matches; missing and ambiguous names grant nothing
    /// and are logged. A look-alike group (for example one whose sAMAccountName equals the configured cn, created by
    /// someone with delegated rights) therefore blocks the name instead of taking it over. DNs are recommended.
    /// </summary>
    private async Task<List<string>> ResolveGroupsAsync(LdapConnection conn, IEnumerable<string> names, CancellationToken ct)
    {
        var dns = new List<string>();
        foreach (var raw in names)
        {
            var name = raw?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            if (DistinguishedNames.LooksLikeDn(name))
            {
                dns.Add(name);
                continue;
            }
            var matches = await SearchDnsAsync(conn, LdapFilters.GroupByName(name), ct);
            var dn = DomainRoles.PickGroupDn(matches);
            if (dn is not null) dns.Add(dn);
            else if (matches.Count == 0) logger.LogWarning("Directory group {Group} was not found.", name);
            else logger.LogWarning("Directory group name {Group} is ambiguous ({Count} groups match its sAMAccountName or cn) " +
                                   "and grants nothing. Configure the group by its DN.", name, matches.Count);
        }
        return dns;
    }

    private async Task<List<string>> SearchDnsAsync(LdapConnection conn, string filter, CancellationToken ct)
    {
        var resp = (SearchResponse)await LdapConnector.SendAsync(conn,
            new SearchRequest(O.BaseDn.Trim(), filter, SearchScope.Subtree, "distinguishedName"), ct);
        return resp.Entries.Cast<SearchResultEntry>().Select(e => e.DistinguishedName).ToList();
    }

    private static async Task<bool> IsMemberAsync(LdapConnection conn, string userDn, IReadOnlyCollection<string> groupDns, CancellationToken ct)
    {
        var resp = (SearchResponse)await LdapConnector.SendAsync(conn,
            new SearchRequest(userDn, LdapFilters.MemberOfAnyInChain(groupDns), SearchScope.Base, "distinguishedName"), ct);
        return resp.Entries.Count > 0;
    }

    private static string? Attr(SearchResultEntry e, string name)
    {
        if (!e.Attributes.Contains(name)) return null;
        var v = e.Attributes[name].GetValues(typeof(string));
        return v.Length == 0 ? null : (string)v[0];
    }

    /// <summary>Binary attributes must be read as byte[] (the string indexer may decode them as UTF-8).</summary>
    private static byte[]? Bin(SearchResultEntry e, string name)
    {
        if (!e.Attributes.Contains(name)) return null;
        var v = e.Attributes[name].GetValues(typeof(byte[]));
        return v.Length == 0 ? null : (byte[])v[0];
    }
}
