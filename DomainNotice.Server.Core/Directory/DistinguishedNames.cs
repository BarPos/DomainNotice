using System.Text;

namespace DomainNotice.Server.Directory;

/// <summary>Distinguished-name helpers (RFC 4514 escaping aware). Comparisons are case-insensitive, as in AD.</summary>
public static class DistinguishedNames
{
    /// <summary>Splits a DN into its RDNs ("CN=a\,b", "OU=x", ...), respecting backslash escapes and quotes. Each part is trimmed.</summary>
    public static IReadOnlyList<string> Split(string? dn)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(dn)) return result;
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < dn.Length; i++)
        {
            var c = dn[i];
            if (c == '\\' && i + 1 < dn.Length)
            {
                sb.Append(c).Append(dn[++i]);
                continue;
            }
            if (c == '"') quoted = !quoted;
            if ((c == ',' || c == ';') && !quoted)
            {
                result.Add(sb.ToString().Trim());
                sb.Clear();
                continue;
            }
            sb.Append(c);
        }
        result.Add(sb.ToString().Trim());
        return result;
    }

    /// <summary>The parent DN, or null for a single-RDN name.</summary>
    public static string? Parent(string? dn)
    {
        if (string.IsNullOrWhiteSpace(dn)) return null;
        var quoted = false;
        for (var i = 0; i < dn.Length; i++)
        {
            var c = dn[i];
            if (c == '\\') { i++; continue; }
            if (c == '"') quoted = !quoted;
            if ((c == ',' || c == ';') && !quoted)
            {
                var rest = dn[(i + 1)..].Trim();
                return rest.Length == 0 ? null : rest;
            }
        }
        return null;
    }

    /// <summary>The value of the first RDN with escapes removed ("CN=Smith\, John,OU=x" → "Smith, John").</summary>
    public static string FirstRdnValue(string? dn)
    {
        var parts = Split(dn);
        if (parts.Count == 0) return "";
        var rdn = parts[0];
        var eq = rdn.IndexOf('=');
        return Unescape(eq < 0 ? rdn : rdn[(eq + 1)..]).Trim();
    }

    /// <summary>Normalised form for comparison: RDNs lowercased, spaces around separators removed.</summary>
    public static string Normalize(string? dn) =>
        string.Join(",", Split(dn).Select(NormalizeRdn));

    /// <summary>True when <paramref name="dn"/> equals <paramref name="baseDn"/> or lies below it (whole RDNs only).</summary>
    public static bool IsUnderBase(string? dn, string? baseDn)
    {
        if (string.IsNullOrWhiteSpace(dn) || string.IsNullOrWhiteSpace(baseDn)) return false;
        if (dn.Contains('\0') || baseDn.Contains('\0')) return false;
        var d = Split(dn).Select(NormalizeRdn).ToList();
        var b = Split(baseDn).Select(NormalizeRdn).ToList();
        if (b.Count == 0 || d.Count < b.Count || d.Any(p => p.Length == 0) || b.Any(p => p.Length == 0)) return false;
        for (var i = 1; i <= b.Count; i++)
            if (!string.Equals(d[^i], b[^i], StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>"dc=example,dc=local" → "example.local". Only DC components count.</summary>
    public static string DomainFromBaseDn(string? baseDn)
    {
        var labels = new List<string>();
        foreach (var part in Split(baseDn))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            if (part[..eq].Trim().Equals("dc", StringComparison.OrdinalIgnoreCase))
            {
                var v = Unescape(part[(eq + 1)..]).Trim();
                if (v.Length > 0) labels.Add(v);
            }
        }
        return string.Join('.', labels);
    }

    /// <summary>True when the text looks like a DN (has a type=value first RDN) rather than a plain name.</summary>
    public static bool LooksLikeDn(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var first = Split(value)[0];
        var eq = first.IndexOf('=');
        if (eq <= 0) return false;
        var type = first[..eq].Trim();
        return type.Length > 0 && type.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-' || ch == '.');
    }

    private static string NormalizeRdn(string rdn)
    {
        var eq = rdn.IndexOf('=');
        if (eq < 0) return rdn.Trim().ToLowerInvariant();
        return rdn[..eq].Trim().ToLowerInvariant() + "=" + rdn[(eq + 1)..].Trim().ToLowerInvariant();
    }

    /// <summary>Removes RFC 4514 escapes (\, and \XX hex pairs as UTF-8).</summary>
    public static string Unescape(string value)
    {
        if (!value.Contains('\\')) return value.Trim('"');
        var bytes = new List<byte>();
        var sb = new StringBuilder();
        void FlushBytes()
        {
            if (bytes.Count == 0) return;
            sb.Append(Encoding.UTF8.GetString(bytes.ToArray()));
            bytes.Clear();
        }
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\\' && i + 2 < value.Length && Uri.IsHexDigit(value[i + 1]) && Uri.IsHexDigit(value[i + 2]))
            {
                bytes.Add(Convert.ToByte(value.Substring(i + 1, 2), 16));
                i += 2;
                continue;
            }
            FlushBytes();
            if (c == '\\' && i + 1 < value.Length)
            {
                sb.Append(value[++i]);
                continue;
            }
            sb.Append(c);
        }
        FlushBytes();
        return sb.ToString();
    }
}
