namespace DomainNotice.Agent.Logic;

/// <summary>
/// The server names the service principal the agent gets a Kerberos ticket for (Welcome.KerberosServicePrincipal).
/// The ticket is issued to the computer account, so a server that could pick any SPN could make the agent mint a
/// ticket for another service of the domain (ldap/, HTTP/, cifs/ ...) and relay it. Only <c>DomainNotice/&lt;host&gt;</c>
/// is accepted. The host is not compared with the server address: installs reach the server by IP or alias.
/// </summary>
public static class KerberosSpnPolicy
{
    public const string ServiceClass = "DomainNotice";

    /// <summary>Longest SPN accepted (a host name is at most 255 characters).</summary>
    public const int MaxLength = 300;

    /// <summary>The trimmed SPN when it is <c>DomainNotice/&lt;host&gt;</c> (service class case-insensitive), otherwise null.</summary>
    public static string? Accept(string? servicePrincipal)
    {
        var spn = servicePrincipal?.Trim();
        if (string.IsNullOrEmpty(spn) || spn.Length > MaxLength) return null;
        var prefix = ServiceClass + "/";
        if (!spn.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

        var host = spn[prefix.Length..];
        if (host.Length == 0) return null;
        foreach (var c in host)
        {
            // No further component (service name), no realm, nothing that is not part of a host name or port.
            if (c is '/' or '@' or '\\' || char.IsWhiteSpace(c) || char.IsControl(c)) return null;
        }
        return spn;
    }

    /// <summary>A refused SPN as it may be logged: control characters replaced, at most 120 characters.</summary>
    public static string ForLog(string? servicePrincipal)
    {
        var text = new string((servicePrincipal ?? "").Select(c => char.IsControl(c) ? '?' : c).ToArray());
        return text.Length > 120 ? text[..120] + "..." : text;
    }
}
