namespace DomainNotice.Shared;

/// <summary>
/// One version for Server, Console, Agent and MSI (plan 11.1).
/// Directory.Build.props &lt;Version&gt; must stay the same; a test enforces it.
/// </summary>
public static class AppVersion
{
    public const string Current = "1.0.2";

    /// <summary>Oldest agent version the server still accepts. Raised only on purpose.</summary>
    public const string MinimumAgent = "1.0.0";

    public static Version CurrentVersion { get; } = Normalize(Version.Parse(Current));
    public static Version MinimumAgentVersion { get; } = Normalize(Version.Parse(MinimumAgent));

    /// <summary>Parses a plain <c>x.y.z</c> (or x.y / x.y.z.w) string. Pre-release suffixes are refused.</summary>
    public static bool TryParse(string? value, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        if (!Version.TryParse(text, out var parsed) || parsed is null) return false;
        version = Normalize(parsed);
        return true;
    }

    /// <summary>True for a plain x.y.z string (what update.sh also accepts).</summary>
    public static bool IsPlainTriplet(string? value) =>
        value is not null && System.Text.RegularExpressions.Regex.IsMatch(value, @"^\d{1,5}\.\d{1,5}\.\d{1,5}$");

    public static int Compare(string a, string b)
    {
        if (!TryParse(a, out var va)) throw new FormatException($"Bad version '{a}'.");
        if (!TryParse(b, out var vb)) throw new FormatException($"Bad version '{b}'.");
        return va.CompareTo(vb);
    }

    /// <summary>
    /// The server accepts agents of its own version or older, down to <see cref="MinimumAgent"/>.
    /// Newer agents are refused (plan 11.1).
    /// </summary>
    public static bool IsAgentSupported(string? agentVersion)
    {
        if (!TryParse(agentVersion, out var v)) return false;
        return v.CompareTo(CurrentVersion) <= 0 && v.CompareTo(MinimumAgentVersion) >= 0;
    }

    /// <summary>True when the agent is older than the server (an update is possible).</summary>
    public static bool IsAgentOutdated(string? agentVersion) =>
        TryParse(agentVersion, out var v) && v.CompareTo(CurrentVersion) < 0;

    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
}
