namespace DomainNotice.Server.Directory;

/// <summary>User-name forms for LDAP binds and lookups.</summary>
public static class BindNames
{
    /// <summary>
    /// The name used for a simple bind. "user@domain", "DOMAIN\user" and DNs are used as given; a short name
    /// becomes "name@domain-from-BaseDn" (dc=example,dc=local → name@example.local).
    /// </summary>
    public static string Format(string userName, string? baseDn)
    {
        var trimmed = (userName ?? "").Trim();
        if (trimmed.Length == 0) return trimmed;
        if (trimmed.Contains('@') || trimmed.Contains('\\') || trimmed.Contains('=')) return trimmed;
        var domain = DistinguishedNames.DomainFromBaseDn(baseDn);
        return domain.Length == 0 ? trimmed : $"{trimmed}@{domain}";
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

    /// <summary>Key for rate limiting domain sign-ins: the same account in any spelling shares one key.</summary>
    public static string RateLimitKey(string userName) => SamAccountName(userName).ToLowerInvariant();

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
