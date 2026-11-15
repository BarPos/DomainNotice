using DomainNotice.Server.Data.Entities;
using DomainNotice.Shared;

namespace DomainNotice.Console.Lists;

// Row types projected from the database for one grid page. Kept flat; EF Core fills them with a member-init projection.

public sealed class ComputerRow
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public string? RemoteIp { get; init; }
    public List<string> LocalIps { get; init; } = [];
    public List<string> Users { get; init; } = [];
    public bool Online { get; init; }
    public DateTime? LastSeenAtUtc { get; init; }
    public string AgentVersion { get; init; } = "";
    public string OsVersion { get; init; } = "";
    /// <summary>DN of the linked AD computer object (the OU is its parent).</summary>
    public string? DirectoryDn { get; init; }
    public AdLinkState AdLinkState { get; init; }
    public bool Replaced { get; init; }
    public bool Revoked { get; init; }
    public DateTime EnrolledAtUtc { get; init; }

    public string? Ou => DnText.Parent(DirectoryDn);
    public bool Outdated => AppVersion.IsAgentOutdated(AgentVersion);
    public StatusKind Status => Revoked ? StatusKind.Revoked : Online ? StatusKind.Online : StatusKind.Offline;
}

public enum StatusKind { Online, Offline, Revoked }

public sealed class NameHistoryRow
{
    public long Id { get; init; }
    public Guid ComputerId { get; init; }
    public string ComputerName { get; init; } = "";
    public string Name { get; init; } = "";
    public DateTime FirstSeenUtc { get; init; }
    public DateTime LastSeenUtc { get; init; }
    public bool IsCurrent { get; init; }
}

public sealed class UserHistoryRow
{
    public long Id { get; init; }
    public Guid ComputerId { get; init; }
    public string ComputerName { get; init; } = "";
    public string UserName { get; init; } = "";
    public int SessionId { get; init; }
    public DateTime LogonAtUtc { get; init; }
    public DateTime? LogoffAtUtc { get; init; }
}

public sealed class IpHistoryRow
{
    public long Id { get; init; }
    public Guid ComputerId { get; init; }
    public string ComputerName { get; init; } = "";
    public IpAddressKind Kind { get; init; }
    public string Address { get; init; } = "";
    public DateTime FirstSeenUtc { get; init; }
    public DateTime LastSeenUtc { get; init; }
    public bool IsCurrent { get; init; }
}

public sealed class AnnouncementRow
{
    public Guid Id { get; init; }
    public string Title { get; init; } = "";
    public bool IsDraft { get; init; }
    public DateTime StartUtc { get; init; }
    public DateTime EndUtc { get; init; }
    public List<string> Targets { get; init; } = [];
    public bool AnonymousAnswers { get; init; }
    public int DeliveredUsers { get; init; }
    public int ConfirmedUsers { get; init; }
    public double Rate { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
    public string UpdatedBy { get; init; } = "";

    public AnnouncementStatus StatusAt(DateTime nowUtc) =>
        IsDraft ? AnnouncementStatus.Draft
        : nowUtc < StartUtc ? AnnouncementStatus.Scheduled
        : nowUtc <= EndUtc ? AnnouncementStatus.Active
        : AnnouncementStatus.Ended;

    public string TargetSummary => Targets.Count == 0 ? "Everyone" : string.Join(", ", Targets);
}

public sealed class ConfirmationRow
{
    public Guid Id { get; init; }
    public Guid AnnouncementId { get; init; }
    public string AnnouncementTitle { get; init; } = "";
    public Guid ComputerId { get; init; }
    public string ComputerName { get; init; } = "";
    public string UserName { get; init; } = "";
    public string ButtonLabel { get; init; } = "";
    public DateTime ConfirmedAtUtc { get; init; }
}

public sealed class DeliveryRow
{
    public Guid Id { get; init; }
    public Guid AnnouncementId { get; init; }
    public string AnnouncementTitle { get; init; } = "";
    public Guid ComputerId { get; init; }
    public string ComputerName { get; init; } = "";
    public string UserName { get; init; } = "";
    public DateTime SentAtUtc { get; init; }
    public DateTime? ShownAtUtc { get; init; }
    public DateTime? ConfirmedAtUtc { get; init; }
    public DateTime? WithdrawnAtUtc { get; init; }
}

/// <summary>A named answer: one selected option, with who/where/when through its confirmation.</summary>
public sealed class NamedAnswerRow
{
    public Guid Id { get; init; }
    public Guid AnnouncementId { get; init; }
    public string AnnouncementTitle { get; init; } = "";
    public string Question { get; init; } = "";
    public string Option { get; init; } = "";
    public string UserName { get; init; } = "";
    public string ComputerName { get; init; } = "";
    public DateTime ConfirmedAtUtc { get; init; }
}

/// <summary>
/// An anonymous answer (plan 8.4): one selected option. Deliberately has NO id, user, computer or time — nothing
/// that could link it to a confirmation or reveal storage order. Value equality: identical answers are identical rows.
/// </summary>
public sealed record AnonymousAnswerRow(Guid AnnouncementId, string AnnouncementTitle, string Question, string Option);

public sealed class AgentUpdateRow
{
    public Guid Id { get; init; }
    public Guid ComputerId { get; init; }
    public string ComputerName { get; init; } = "";
    public string FromVersion { get; init; } = "";
    public string ToVersion { get; init; } = "";
    public AgentUpdateState State { get; init; }
    public string RequestedBy { get; init; } = "";
    public DateTime CreatedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
    public DateTime? FinishedAtUtc { get; init; }
    public long BytesSent { get; init; }
    public string? Error { get; init; }

    public bool IsOpen => State is not (AgentUpdateState.Done or AgentUpdateState.Failed or AgentUpdateState.Cancelled);
}

public sealed class AuditRow
{
    public long Id { get; init; }
    public DateTime AtUtc { get; init; }
    public string Actor { get; init; } = "";
    public string Action { get; init; } = "";
    public string? TargetType { get; init; }
    public string? TargetId { get; init; }
    public string? Details { get; init; }
    public string? RemoteIp { get; init; }
}

public sealed class DirectoryObjectRow
{
    public Guid ObjectGuid { get; init; }
    public DirectoryObjectKind Kind { get; init; }
    public string Name { get; init; } = "";
    public string? SamAccountName { get; init; }
    public string? DisplayName { get; init; }
    public string DistinguishedName { get; init; } = "";
    public bool Enabled { get; init; }
    public bool Deleted { get; init; }
    public DateTime LastSyncedUtc { get; init; }
}

public static class DnText
{
    /// <summary>"CN=PC1,OU=Sales,DC=x" → "OU=Sales,DC=x". Handles escaped commas.</summary>
    public static string? Parent(string? dn)
    {
        if (string.IsNullOrEmpty(dn)) return null;
        for (var i = 0; i < dn.Length; i++)
        {
            if (dn[i] == '\\') { i++; continue; }
            if (dn[i] == ',') return dn[(i + 1)..];
        }
        return null;
    }
}
