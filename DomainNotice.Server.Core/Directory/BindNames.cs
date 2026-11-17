namespace DomainNotice.Server.Directory;

/// <summary>User-name forms for LDAP binds and lookups.</summary>
public static class BindNames
{
    /// <summary>
    /// The name used for a simple bind. "user@domain", "DOMAIN\user" and DNs are used as given; a short name
    /// becomes "name@domain-from-BaseDn" (dc=example,dc=local → name@example.local). Console sign-in refuses DNs
    /// before this is called (<see cref="IsAcceptedSignInName"/>); the service account (Directory:BindUser) may be one.
    /// </summary>
    public static string Format(string userName, string? baseDn)
    {
        var trimmed = (userName ?? "").Trim();
        if (trimmed.Length == 0) return trimmed;
        if (trimmed.Contains('@') || trimmed.Contains('\\') || trimmed.Contains('=')) return trimmed;
        var domain = DistinguishedNames.DomainFromBaseDn(baseDn);
        return domain.Length == 0 ? trimmed : $"{trimmed}@{domain}";
    }

    /// <summary>
    /// Console sign-in accepts "sam", "DOMAIN\sam" and "user@domain" only. DN-form names (anything with '=') are
    /// refused: they could be re-spelled endlessly to get fresh rate-limit budgets.
    /// </summary>
    public static bool IsAcceptedSignInName(string? userName)
    {
        var trimmed = (userName ?? "").Trim();
        if (trimmed.Length == 0 || trimmed.Contains('=') || trimmed.Any(char.IsControl)) return false;
        var slash = trimmed.IndexOf('\\');
        if (slash >= 0 && (slash == 0 || slash == trimmed.Length - 1 || trimmed.IndexOf('\\', slash + 1) >= 0)) return false;
        return true;
    }

    /// <summary>"DOMAIN\user" → user, "user@domain" → user, "user" → user.</summary>
    public static string SamAccountName(string userName)
    {
        var trimmed = (userName ?? "").Trim();
        var slash = trimmed.LastIndexOf('\\');
        if (slash >= 0 && slash < trimmed.Length - 1) return trimmed[(slash + 1)..];
        var at = trimmed.IndexOf('@');
        if (at > 0) return trimmed[..at];
        return trimmed;
    }

    /// <summary>The "DOMAIN" of "DOMAIN\user"; null for other forms.</summary>
    public static string? DomainPrefix(string userName)
    {
        var trimmed = (userName ?? "").Trim();
        var slash = trimmed.IndexOf('\\');
        return slash > 0 ? trimmed[..slash] : null;
    }

    /// <summary>
    /// Key for rate limiting domain sign-ins. "CORP\alice" and "alice" share one key (the domain prefix is dropped;
    /// only one domain is supported). A UPN keeps its own key, since its prefix need not be the sAMAccountName.
    /// </summary>
    public static string RateLimitKey(string userName)
    {
        var trimmed = (userName ?? "").Trim();
        if (trimmed.Contains('@')) return "upn:" + trimmed.ToLowerInvariant();
        return SamAccountName(trimmed).ToLowerInvariant();
    }

    /// <summary>Parses an RFC 4532 "Who am I?" authzId ("u:DOMAIN\user" or "dn:CN=...").</summary>
    public static (string? NtName, string? Dn) ParseAuthzId(string? authzId)
    {
        if (string.IsNullOrWhiteSpace(authzId)) return (null, null);
        var v = authzId.Trim();
        if (v.StartsWith("u:", StringComparison.OrdinalIgnoreCase)) return (v[2..], null);
        if (v.StartsWith("dn:", StringComparison.OrdinalIgnoreCase)) return (null, v[3..]);
        return (null, null);
    }
}
