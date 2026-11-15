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

public sealed record DomainAuthResult(DomainAuthStatus Status, string? UserName = null, string? DisplayName = null,
    string? Role = null, string? Error = null);

/// <summary>Domain sign-in: bind as the user, then map group membership to a Console role. Used by Accounts.</summary>
public interface IDomainAuthenticator
{
    /// <summary>Directory configured with allowed connection settings and at least one admin or viewer group.</summary>
    bool IsAvailable { get; }
    Task<DomainAuthResult> AuthenticateAsync(string userName, string password, CancellationToken ct);
}

internal sealed class LdapDomainAuthenticator(
    LdapConnector connector,
    IOptions<DirectoryOptions> options,
    IHostEnvironment env,
    ILogger<LdapDomainAuthenticator> logger) : IDomainAuthenticator
{
    private const string WhoAmIOid = "1.3.6.1.4.1.4203.1.11.3";

    private DirectoryOptions O => options.Value;

    public bool IsAvailable =>
        O.IsConfigured && LdapSecurityPolicy.ValidateConnection(O, env.IsDevelopment()) is null
        && (O.AdminGroups.Any(g => !string.IsNullOrWhiteSpace(g)) || O.ViewerGroups.Any(g => !string.IsNullOrWhiteSpace(g)));

    public async Task<DomainAuthResult> AuthenticateAsync(string userName, string password, CancellationToken ct)
    {
        if (!O.IsConfigured) return new(DomainAuthStatus.NotConfigured, Error: "Domain sign-in is not configured.");
        var configError = LdapSecurityPolicy.ValidateConnection(O, env.IsDevelopment());
        if (configError is not null) return new(DomainAuthStatus.NotConfigured, Error: configError);
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password))
            return new(DomainAuthStatus.InvalidCredentials, Error: "Enter your user name and password.");

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
                var (ntName, _) = await WhoAmIAsync(conn, ct);
                var user = await FindUserAsync(conn, userName, ntName, ct);
                if (user is null)
                {
                    logger.LogWarning("Domain sign-in: {User} bound but the user object was not found under {BaseDn}.", userName.Trim(), O.BaseDn);
                    return new(DomainAuthStatus.NotAuthorized, Error: "Your account was not found under the configured directory base.");
                }
                var sam = Attr(user, "sAMAccountName") ?? BindNames.SamAccountName(userName);
                var display = Attr(user, "displayName");
                var name = ntName ?? sam;

                var adminDns = await ResolveGroupsAsync(conn, O.AdminGroups, ct);
                if (adminDns.Count > 0 && await IsMemberAsync(conn, user.DistinguishedName, adminDns, ct))
                    return new(DomainAuthStatus.Success, name, display, ConsoleRoles.Admin);
                var viewerDns = await ResolveGroupsAsync(conn, O.ViewerGroups, ct);
                if (viewerDns.Count > 0 && await IsMemberAsync(conn, user.DistinguishedName, viewerDns, ct))
                    return new(DomainAuthStatus.Success, name, display, ConsoleRoles.Viewer);

                logger.LogInformation("Domain sign-in: {User} is not in an admin or viewer group.", name);
                return new(DomainAuthStatus.NotAuthorized, name, display, Error: "Your account is not in a DomainNotice admin or viewer group.");
            }
            catch (Exception e) when (e is LdapException or DirectoryOperationException)
            {
                logger.LogWarning(e, "Domain sign-in: directory lookup failed for {User}.", userName.Trim());
                return new(DomainAuthStatus.Unavailable, Error: "The directory could not be read: " + e.Message);
            }
        }
    }

    /// <summary>RFC 4532 "Who am I?" — AD answers "u:DOMAIN\sam", the authoritative account the bind used.</summary>
    private async Task<(string? NtName, string? Dn)> WhoAmIAsync(LdapConnection conn, CancellationToken ct)
    {
        try
        {
            var resp = (ExtendedResponse)await LdapConnector.SendAsync(conn, new ExtendedRequest(WhoAmIOid), ct);
            return BindNames.ParseAuthzId(resp.ResponseValue is { Length: > 0 } v ? Encoding.UTF8.GetString(v) : null);
        }
        catch (Exception e) when (e is LdapException or DirectoryOperationException)
        {
            logger.LogDebug(e, "Who am I? not supported; falling back to the typed name.");
            return (null, null);
        }
    }

    private async Task<SearchResultEntry?> FindUserAsync(LdapConnection conn, string typed, string? ntName, CancellationToken ct)
    {
        var filters = new List<string>();
        if (ntName is not null) filters.Add(LdapFilters.UserBySamAccountName(BindNames.SamAccountName(ntName)));
        else
        {
            if (typed.Contains('@')) filters.Add(LdapFilters.UserByUserPrincipalName(typed.Trim()));
            filters.Add(LdapFilters.UserBySamAccountName(BindNames.SamAccountName(typed)));
        }
        foreach (var f in filters)
        {
            var resp = (SearchResponse)await LdapConnector.SendAsync(conn,
                new SearchRequest(O.BaseDn.Trim(), f, SearchScope.Subtree, "distinguishedName", "sAMAccountName", "displayName"), ct);
            if (resp.Entries.Count == 1) return resp.Entries[0];
        }
        return null;
    }

    /// <summary>
    /// Group names may be DNs (used as given) or names. A name matches sAMAccountName (unique in the domain) first,
    /// then cn only when exactly one group has it; ambiguous names are ignored with a warning, so a same-named group
    /// in a delegated OU can never grant access.
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
            var bySam = await SearchDnsAsync(conn, $"(&(objectClass=group)(sAMAccountName={LdapFilters.Escape(name)}))", ct);
            if (bySam.Count == 1)
            {
                dns.Add(bySam[0]);
                continue;
            }
            var byCn = await SearchDnsAsync(conn, $"(&(objectClass=group)(cn={LdapFilters.Escape(name)}))", ct);
            if (byCn.Count == 1) dns.Add(byCn[0]);
            else if (byCn.Count == 0) logger.LogWarning("Directory group {Group} was not found.", name);
            else logger.LogWarning("Directory group name {Group} is ambiguous ({Count} groups); use its DN.", name, byCn.Count);
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
}
