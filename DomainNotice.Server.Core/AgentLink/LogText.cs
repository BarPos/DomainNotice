using System.Text;

namespace DomainNotice.Server.AgentLink;

/// <summary>
/// Makes agent-supplied text safe for log messages: control characters (line breaks, escape sequences) become '?',
/// and long values are cut with "…" so an unauthenticated peer cannot write huge or forged log lines.
/// </summary>
internal static class LogText
{
    public const int DefaultMax = 100;

    public static string Clip(string? value, int max = DefaultMax)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var cut = value.Length > max;
        var span = cut ? value.AsSpan(0, max) : value.AsSpan();
        if (cut && span.Length > 0 && char.IsHighSurrogate(span[^1])) span = span[..^1]; // do not split a pair
        var sb = new StringBuilder(span.Length + 1);
        foreach (var c in span) sb.Append(char.IsControl(c) ? '?' : c);
        if (cut) sb.Append('…');
        return sb.ToString();
    }
}
