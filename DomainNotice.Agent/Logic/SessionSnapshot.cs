using System.Globalization;
using DomainNotice.Shared.Protocol;

namespace DomainNotice.Agent.Logic;

public sealed record SessionDiff(
    IReadOnlyList<SessionInfo> Added,
    IReadOnlyList<SessionInfo> Removed,
    IReadOnlyList<SessionInfo> StateChanged)
{
    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0 && StateChanged.Count == 0;
}

/// <summary>Compares two lists of Windows sessions (as reported in Hello/Status).</summary>
public static class SessionSnapshot
{
    /// <summary>
    /// A session is "the same" when id and user match. A changed user under the same id (logoff, then another
    /// logon that reused the id) counts as removed + added.
    /// </summary>
    public static SessionDiff Diff(IReadOnlyList<SessionInfo> previous, IReadOnlyList<SessionInfo> current)
    {
        var added = new List<SessionInfo>();
        var removed = new List<SessionInfo>();
        var changed = new List<SessionInfo>();

        foreach (var now in current)
        {
            var before = previous.FirstOrDefault(p => SameSession(p, now));
            if (before is null) added.Add(now);
            else if (before.State != now.State || before.LogonTimeUtc != now.LogonTimeUtc) changed.Add(now);
        }

        foreach (var before in previous)
        {
            if (!current.Any(c => SameSession(before, c))) removed.Add(before);
        }

        return new SessionDiff(added, removed, changed);
    }

    public static bool SameSession(SessionInfo a, SessionInfo b) =>
        a.SessionId == b.SessionId && PendingDeliveryStore.SameUser(a.UserName, b.UserName);

    /// <summary>Users that had at least one session before and have none now.</summary>
    public static IReadOnlyList<string> UsersGone(IReadOnlyList<SessionInfo> previous, IReadOnlyList<SessionInfo> current) =>
        previous.Select(p => p.UserName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(user => !current.Any(c => PendingDeliveryStore.SameUser(c.UserName, user)))
            .ToList();

    /// <summary>Builds DOMAIN\user from the WTS parts; null when there is no user (login screen, services).</summary>
    public static string? FormatUser(string? domain, string? user)
    {
        if (string.IsNullOrWhiteSpace(user)) return null;
        return string.IsNullOrWhiteSpace(domain) ? user.Trim() : domain.Trim() + "\\" + user.Trim();
    }
}

/// <summary>Formats the OS version for Hello/Enroll from the values in HKLM\...\Windows NT\CurrentVersion.</summary>
public static class OsVersionText
{
    public static string Format(string? productName, string? displayVersion, string? currentBuild, int? ubr, Version osVersion)
    {
        var name = string.IsNullOrWhiteSpace(productName) ? "Windows" : productName.Trim();
        var build = int.TryParse(currentBuild, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) ? b : osVersion.Build;
        // ProductName still says "Windows 10" on Windows 11 (build 22000 and later).
        if (build >= 22000 && name.StartsWith("Windows 10", StringComparison.OrdinalIgnoreCase))
            name = "Windows 11" + name["Windows 10".Length..];

        var text = name;
        if (!string.IsNullOrWhiteSpace(displayVersion)) text += " " + displayVersion.Trim();
        var version = $"{osVersion.Major}.{osVersion.Minor}.{build}";
        if (ubr is { } u and > 0) version += "." + u.ToString(CultureInfo.InvariantCulture);
        return $"{text} ({version})";
    }
}
