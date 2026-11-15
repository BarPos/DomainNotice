using System.Text;

namespace DomainNotice.Server.Directory;

/// <summary>LDAP search filters (RFC 4515) used by the sync and the domain sign-in.</summary>
public static class LdapFilters
{
    /// <summary>LDAP_MATCHING_RULE_IN_CHAIN: transitive (nested) membership in AD.</summary>
    public const string MatchingRuleInChain = "1.2.840.113556.1.4.1941";

    /// <summary>
    /// Escapes a value for use inside a filter assertion (RFC 4515 section 3): <c>\ * ( )</c> and NUL become
    /// <c>\XX</c>. Other characters, including non-ASCII, are left as UTF-8 text, which RFC 4515 allows.
    /// </summary>
    public static string Escape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append(@"\5c"); break;
                case '*': sb.Append(@"\2a"); break;
                case '(': sb.Append(@"\28"); break;
                case ')': sb.Append(@"\29"); break;
                case '\0': sb.Append(@"\00"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Everything the sync copies: the domain, OUs, containers, users, computers and groups.</summary>
    public const string SyncedObjects =
        "(|(objectClass=domainDNS)(objectClass=organizationalUnit)(objectClass=container)(objectClass=builtinDomain)" +
        "(objectClass=group)(objectClass=computer)(&(objectCategory=person)(objectClass=user)))";

    /// <summary><see cref="SyncedObjects"/> restricted to uSNChanged &gt; <paramref name="highestUsn"/> (LDAP has no "&gt;").</summary>
    public static string SyncedObjectsChangedAfter(long highestUsn) =>
        $"(&{SyncedObjects}(uSNChanged>={highestUsn + 1}))";

    public static string GroupByName(string name)
    {
        var n = Escape(name.Trim());
        return $"(&(objectClass=group)(|(cn={n})(sAMAccountName={n})))";
    }

    public static string UserBySamAccountName(string sam) =>
        $"(&(objectCategory=person)(objectClass=user)(sAMAccountName={Escape(sam.Trim())}))";

    public static string UserByUserPrincipalName(string upn) =>
        $"(&(objectCategory=person)(objectClass=user)(userPrincipalName={Escape(upn.Trim())}))";

    /// <summary>
    /// For a base-scope search on the user's DN: matches when the user is in any of the groups, nested groups included.
    /// </summary>
    public static string MemberOfAnyInChain(IEnumerable<string> groupDns)
    {
        var parts = groupDns.Select(dn => $"(memberOf:{MatchingRuleInChain}:={Escape(dn)})").ToList();
        if (parts.Count == 0) throw new ArgumentException("At least one group is required.", nameof(groupDns));
        return parts.Count == 1 ? parts[0] : "(|" + string.Concat(parts) + ")";
    }
}
