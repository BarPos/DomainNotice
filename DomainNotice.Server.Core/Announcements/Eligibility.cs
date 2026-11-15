using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Data.Entities;

namespace DomainNotice.Server.Announcements;

// Pure targeting/eligibility rules (plan 8.2, P2, D8, 3.4). No I/O: the data loader (TargetingDataLoader) builds the
// facts below from the database, and the delivery coordinator asks these functions what is due.

/// <summary>One target of an announcement, with the OU's lowercased DN resolved at load time.</summary>
public sealed record TargetRule(
    TargetKind Kind,
    Guid? DirectoryObjectGuid = null,
    Guid? ComputerId = null,
    string? Value = null,
    string? OrganizationalUnitDnLower = null);

/// <summary>What the eligibility check needs to know about an announcement.</summary>
public sealed record AnnouncementRule(
    Guid Id,
    bool IsDraft,
    DateTime StartUtc,
    DateTime EndUtc,
    int MaxDisplays,
    int RepeatIntervalMinutes,
    IReadOnlyList<TargetRule> Targets);

/// <summary>
/// The computer as the server knows it (identity from the TLS key, plan 3.4). The directory facts are set only when
/// the computer is linked to an AD object (MatchedByName or Proven).
/// </summary>
public sealed record ComputerFacts(
    Guid Id,
    AdLinkState LinkState,
    bool Revoked,
    bool Replaced,
    Guid? DirectoryObjectGuid,
    string? DistinguishedNameLower,
    IReadOnlySet<Guid> GroupGuids);

/// <summary>
/// The user as reported by the agent, plus the synced AD user objects that match the reported name (if any).
/// </summary>
public sealed record UserFacts(
    string UserName,
    IReadOnlySet<Guid> ObjectGuids,
    IReadOnlyList<string> DistinguishedNamesLower,
    IReadOnlySet<Guid> GroupGuids)
{
    public string UserKey => UserKeys.Normalize(UserName);

    public static UserFacts NameOnly(string userName) => new(userName, new HashSet<Guid>(), [], new HashSet<Guid>());
}

/// <summary>A user's history with one announcement, across all computers (D8).</summary>
public sealed record UserAnnouncementHistory(
    int ShownCount,
    DateTime? LastShownUtc,
    bool HasUnconfirmedShownElsewhere)
{
    public static readonly UserAnnouncementHistory None = new(0, null, false);
}

/// <summary>What kinds of targets a computer may receive, by its link state (plan 3.4, 4).</summary>
public enum ComputerReach
{
    /// <summary>Revoked, replaced, or awaiting approval with policy Nothing.</summary>
    Nothing = 0,
    /// <summary>Awaiting approval (default policy): Everyone and User targets only.</summary>
    EveryoneAndUser = 1,
    /// <summary>Not linked to AD (None / KeptSeparate): Everyone, User and Computer-by-ComputerId targets.</summary>
    Unlinked = 2,
    /// <summary>Linked to AD (MatchedByName / Proven): every target kind.</summary>
    Full = 3,
}

public static class Eligibility
{
    public static ComputerReach ReachOf(ComputerFacts computer, AwaitingApprovalPolicy policy)
    {
        if (computer.Revoked || computer.Replaced) return ComputerReach.Nothing;
        return computer.LinkState switch
        {
            AdLinkState.AwaitingApproval => policy == AwaitingApprovalPolicy.Nothing ? ComputerReach.Nothing : ComputerReach.EveryoneAndUser,
            AdLinkState.MatchedByName or AdLinkState.Proven => ComputerReach.Full,
            _ => ComputerReach.Unlinked,
        };
    }

    /// <summary>Draft, time window, display limit (shown deliveries per user across computers), repeat interval, audience.</summary>
    public static bool IsDue(AnnouncementRule rule, UserAnnouncementHistory history, ComputerFacts computer, UserFacts user,
        AwaitingApprovalPolicy policy, DateTime nowUtc) =>
        IsDeliverableAt(rule, nowUtc)
        && DisplayAllowed(rule, history, nowUtc)
        && !history.HasUnconfirmedShownElsewhere
        && AudienceMatches(rule.Targets, computer, user, policy);

    /// <summary>Not a draft and StartUtc ≤ now ≤ EndUtc.</summary>
    public static bool IsDeliverableAt(AnnouncementRule rule, DateTime nowUtc) =>
        !rule.IsDraft && rule.StartUtc <= nowUtc && nowUtc <= rule.EndUtc;

    public static bool IsDeliverableAt(Announcement a, DateTime nowUtc) => a.StatusAt(nowUtc) == AnnouncementStatus.Active;

    /// <summary>P2: shown count below MaxDisplays; when shown before and MaxDisplays &gt; 1, the repeat interval has passed since the last shown time.</summary>
    public static bool DisplayAllowed(AnnouncementRule rule, UserAnnouncementHistory history, DateTime nowUtc)
    {
        if (history.ShownCount >= rule.MaxDisplays) return false;
        if (history.ShownCount > 0 && history.LastShownUtc is { } last
            && nowUtc < last.AddMinutes(rule.RepeatIntervalMinutes))
            return false;
        return true;
    }

    /// <summary>No targets = everyone; otherwise any target matches (OR), limited by what the computer may receive.</summary>
    public static bool AudienceMatches(IReadOnlyList<TargetRule> targets, ComputerFacts computer, UserFacts user, AwaitingApprovalPolicy policy)
    {
        var reach = ReachOf(computer, policy);
        if (reach == ComputerReach.Nothing) return false;
        if (targets.Count == 0) return true;

        foreach (var target in targets)
        {
            if (!KindAllowed(target.Kind, target, reach)) continue;
            if (TargetMatches(target, computer, user)) return true;
        }
        return false;
    }

    private static bool KindAllowed(TargetKind kind, TargetRule target, ComputerReach reach) => reach switch
    {
        ComputerReach.Full => true,
        ComputerReach.Unlinked => kind == TargetKind.User || (kind == TargetKind.Computer && target.ComputerId is not null),
        ComputerReach.EveryoneAndUser => kind == TargetKind.User,
        _ => false,
    };

    public static bool TargetMatches(TargetRule target, ComputerFacts computer, UserFacts user)
    {
        switch (target.Kind)
        {
            case TargetKind.User:
                if (target.DirectoryObjectGuid is { } userGuid)
                    return user.ObjectGuids.Contains(userGuid);
                return !string.IsNullOrWhiteSpace(target.Value) && IdentityMatch.UserMatches(target.Value, user.UserName);

            case TargetKind.Computer:
                if (target.ComputerId is { } computerId && computerId == computer.Id)
                    return true;
                return target.DirectoryObjectGuid is { } adGuid
                       && IsLinked(computer)
                       && computer.DirectoryObjectGuid == adGuid;

            case TargetKind.UserGroup:
                return target.DirectoryObjectGuid is { } ug && user.GroupGuids.Contains(ug);

            case TargetKind.ComputerGroup:
                return target.DirectoryObjectGuid is { } cg && IsLinked(computer) && computer.GroupGuids.Contains(cg);

            case TargetKind.OrganizationalUnit:
                if (string.IsNullOrEmpty(target.OrganizationalUnitDnLower)) return false;
                foreach (var dn in user.DistinguishedNamesLower)
                    if (IsInSubtree(dn, target.OrganizationalUnitDnLower)) return true;
                return IsLinked(computer) && computer.DistinguishedNameLower is { } cdn
                       && IsInSubtree(cdn, target.OrganizationalUnitDnLower);

            default:
                return false;
        }
    }

    public static bool IsLinked(ComputerFacts computer) =>
        computer.LinkState is AdLinkState.MatchedByName or AdLinkState.Proven && computer.DirectoryObjectGuid is not null;

    /// <summary>True when <paramref name="dnLower"/> is the OU itself or inside its subtree. Both lowercased.</summary>
    public static bool IsInSubtree(string dnLower, string ouDnLower)
    {
        if (string.IsNullOrEmpty(dnLower) || string.IsNullOrEmpty(ouDnLower)) return false;
        return dnLower == ouDnLower || dnLower.EndsWith("," + ouDnLower, StringComparison.Ordinal);
    }

    /// <summary>
    /// Picks the first due announcement in order (StartUtc, then CreatedAtUtc — the caller passes them ordered).
    /// </summary>
    public static AnnouncementRule? FirstDue(IEnumerable<AnnouncementRule> ordered,
        Func<Guid, UserAnnouncementHistory> historyOf, ComputerFacts computer, UserFacts user,
        AwaitingApprovalPolicy policy, DateTime nowUtc)
    {
        foreach (var rule in ordered)
            if (IsDue(rule, historyOf(rule.Id), computer, user, policy, nowUtc))
                return rule;
        return null;
    }
}

/// <summary>Name matching when there is no directory (OpenNotifier's IdentityMatch).</summary>
public static class IdentityMatch
{
    /// <summary>"CORP\alice" → "alice", "alice@corp" → "alice", "alice" → "alice".</summary>
    public static string SamAccountName(string name)
    {
        var trimmed = name.Trim();
        var slash = trimmed.LastIndexOf('\\');
        if (slash >= 0 && slash < trimmed.Length - 1)
            return trimmed[(slash + 1)..];
        var at = trimmed.IndexOf('@');
        if (at > 0)
            return trimmed[..at];
        return trimmed;
    }

    /// <summary>Case-insensitive; a bare "alice" also matches "CORP\alice" and "alice@corp".</summary>
    public static bool UserMatches(string target, string reported)
    {
        target = target.Trim();
        reported = reported.Trim();
        if (target.Length == 0 || reported.Length == 0) return false;
        if (string.Equals(target, reported, StringComparison.OrdinalIgnoreCase)) return true;
        if (!target.Contains('\\') && !target.Contains('@'))
            return string.Equals(target, SamAccountName(reported), StringComparison.OrdinalIgnoreCase);
        return false;
    }
}
