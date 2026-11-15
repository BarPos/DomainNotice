using System.Linq.Expressions;
using DomainNotice.Console.Grid;
using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Data.Entities;
using DomainNotice.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DomainNotice.Console.Lists;

/// <summary>All grids of the Console (plan 9.3 table), by name. The CSV endpoint and the pages share these.</summary>
public static class ConsoleLists
{
    public static readonly ComputersList Computers = new();
    public static readonly NameHistoryList NameHistory = new();
    public static readonly UserHistoryList UserHistory = new();
    public static readonly IpHistoryList IpHistory = new();
    public static readonly AnnouncementsList Announcements = new();
    public static readonly ConfirmationsList Confirmations = new();
    public static readonly DeliveriesList Deliveries = new();
    public static readonly NamedAnswersList NamedAnswers = new();
    public static readonly AnonymousAnswersList AnonymousAnswers = new();
    public static readonly AgentUpdatesList AgentUpdates = new();
    public static readonly AuditList Audit = new();
    public static readonly DirectoryObjectsList DirectoryObjects = new();

    public static readonly IReadOnlyDictionary<string, IGridList> All = new IGridList[]
    {
        Computers, NameHistory, UserHistory, IpHistory, Announcements, Confirmations, Deliveries, NamedAnswers,
        AnonymousAnswers, AgentUpdates, Audit, DirectoryObjects,
    }.ToDictionary(l => l.Name, StringComparer.OrdinalIgnoreCase);

    public static GridSort<T> By<T, TKey>(Expression<Func<T, TKey>> selector, bool descending = false) => new(selector, descending);

    /// <summary>
    /// The computer ids whose history belongs to the computer in scope "computer" (itself plus records it replaced),
    /// via <see cref="IComputerAdminService.GetHistoryChainAsync"/>. Null = no computer scope.
    /// </summary>
    public static async ValueTask<List<Guid>?> ComputerChainAsync(GridContext ctx)
    {
        if (ctx.GetGuid("computer") is not { } id) return null;
        var admin = ctx.Services?.GetService<IComputerAdminService>();
        var chain = admin is null ? [] : await admin.GetHistoryChainAsync(id);
        var list = chain.ToList();
        if (!list.Contains(id)) list.Add(id);
        return list;
    }
}

// ---------------------------------------------------------------------------------------------- computers

public sealed class ComputersList : GridList<Computer, ComputerRow>
{
    public const string ScopeNode = "node";
    public override string Name => "computers";
    public override IReadOnlyList<string> ScopeKeys => [ScopeNode];
    public override string DefaultSort => "name";
    public override object RowKey(ComputerRow row) => row.Id;

    public override IReadOnlyList<GridColumn<ComputerRow>> Columns { get; } =
    [
        new("status", "Status", r => r.Status, ColumnKind.Status, "online"),
        new("name", "Name", r => r.Name, SortField: "name"),
        new("remoteIp", "Remote IP", r => r.RemoteIp, SortField: "remoteIp"),
        new("localIps", "Local IPs", r => r.LocalIps),
        new("users", "Current users", r => r.Users),
        new("lastSeen", "Last seen", r => r.LastSeenAtUtc, ColumnKind.Date, "lastSeen"),
        new("version", "Agent version", r => r.AgentVersion, SortField: "version"),
        new("os", "OS", r => r.OsVersion, SortField: "os", DefaultVisible: false),
        new("ou", "OU", r => r.Ou, SortField: "ou"),
        new("adLink", "AD link", r => GridField.Humanize(r.AdLinkState.ToString()), SortField: "adLink"),
        new("replaced", "Replaced", r => r.Replaced, ColumnKind.Bool, "replaced", DefaultVisible: false),
        new("revoked", "Revoked", r => r.Revoked, ColumnKind.Bool, "revoked", DefaultVisible: false),
        new("enrolled", "Enrolled", r => r.EnrolledAtUtc, ColumnKind.Date, "enrolled", DefaultVisible: false),
    ];

    public override IReadOnlyList<GridField<Computer>> Fields(GridContext ctx)
    {
        var db = ctx.Db;
        Expression<Func<Computer, string?>> dn = c =>
            db.DirectoryObjects.Where(d => d.ObjectGuid == c.DirectoryObjectGuid).Select(d => d.DistinguishedName).FirstOrDefault();
        return
        [
            GridField.Text<Computer>("name", "Name", c => c.Name, search: true),
            GridField.CustomText<Computer>("ip", "IP (remote or local)",
                v => c => (c.RemoteIp != null && c.RemoteIp.ToLower().Contains(v)) || c.LocalIps.Any(ip => ip.ToLower().Contains(v)),
                search: true),
            GridField.Text<Computer>("remoteIp", "Remote IP", c => c.RemoteIp),
            GridField.CustomText<Computer>("user", "Current user",
                v => c => c.Sessions.Any(s => s.UserKey.Contains(v)), search: true),
            GridField.Bool<Computer>("online", "Online", c => c.Online, "Online", "Offline"),
            GridField.Date<Computer>("lastSeen", "Last seen", c => c.LastSeenAtUtc),
            GridField.Text<Computer>("version", "Agent version", c => c.AgentVersion),
            GridField.Text<Computer>("os", "OS", c => c.OsVersion, search: true),
            GridField.CustomText<Computer>("ou", "OU",
                v => c => db.DirectoryObjects.Any(d => d.ObjectGuid == c.DirectoryObjectGuid && d.DistinguishedNameLower.Contains(v)),
                sortSelector: dn),
            GridField.Bool<Computer>("inDirectory", "In directory", c => c.DirectoryObjectGuid != null, "In AD", "Not in AD"),
            GridField.Enum<Computer, AdLinkState>("adLink", "AD link state", c => c.AdLinkState),
            GridField.Bool<Computer>("replaced", "Replaced", c => c.ReplacedByComputerId != null),
            GridField.Bool<Computer>("revoked", "Revoked", c => c.RevokedAtUtc != null),
            GridField.Date<Computer>("enrolled", "Enrolled", c => c.EnrolledAtUtc),
        ];
    }

    protected override async ValueTask<IQueryable<Computer>> SourceAsync(GridContext ctx)
    {
        var db = ctx.Db;
        IQueryable<Computer> q = db.Computers.AsNoTracking();
        var node = ctx.Get(ScopeNode) ?? "all";
        var weekAgo = ctx.NowUtc.AddDays(-7);
        switch (node)
        {
            case "awaiting":
                q = q.Where(c => c.AdLinkState == AdLinkState.AwaitingApproval && c.RevokedAtUtc == null);
                break;
            case "notindir":
                q = q.Where(c => c.DirectoryObjectGuid == null && c.RevokedAtUtc == null && c.ReplacedByComputerId == null);
                break;
            case "offline7":
                q = q.Where(c => !c.Online && c.LastSeenAtUtc != null && c.LastSeenAtUtc < weekAgo && c.RevokedAtUtc == null);
                break;
            case "revoked":
                q = q.Where(c => c.RevokedAtUtc != null);
                break;
            default:
                if (node.StartsWith("ou:", StringComparison.Ordinal) && Guid.TryParse(node[3..], out var ouGuid))
                {
                    var ouDn = await db.DirectoryObjects.AsNoTracking().Where(d => d.ObjectGuid == ouGuid)
                        .Select(d => d.DistinguishedNameLower).FirstOrDefaultAsync();
                    if (ouDn is null) return q.Where(_ => false);
                    var suffix = "," + ouDn;
                    q = q.Where(c => db.DirectoryObjects.Any(d => d.ObjectGuid == c.DirectoryObjectGuid && d.DistinguishedNameLower.EndsWith(suffix)));
                }
                break;
        }
        return q;
    }

    protected override Expression<Func<Computer, ComputerRow>> Projection(GridContext ctx)
    {
        var db = ctx.Db;
        return c => new ComputerRow
        {
            Id = c.Id, Name = c.Name, RemoteIp = c.RemoteIp, LocalIps = c.LocalIps,
            Users = c.Sessions.OrderBy(s => s.UserName).Select(s => s.UserName).ToList(),
            Online = c.Online, LastSeenAtUtc = c.LastSeenAtUtc, AgentVersion = c.AgentVersion, OsVersion = c.OsVersion,
            DirectoryDn = db.DirectoryObjects.Where(d => d.ObjectGuid == c.DirectoryObjectGuid).Select(d => d.DistinguishedName).FirstOrDefault(),
            AdLinkState = c.AdLinkState, Replaced = c.ReplacedByComputerId != null, Revoked = c.RevokedAtUtc != null,
            EnrolledAtUtc = c.EnrolledAtUtc,
        };
    }

    protected override IReadOnlyList<GridSort<Computer>> TieBreakers(GridContext ctx) => [ConsoleLists.By<Computer, Guid>(c => c.Id)];
}

// ---------------------------------------------------------------------------------------------- history

public sealed class NameHistoryList : GridList<ComputerNameHistory, NameHistoryRow>
{
    public override string Name => "name-history";
    public override IReadOnlyList<string> ScopeKeys => ["computer"];
    public override string DefaultSort => "lastSeen";
    public override bool DefaultDescending => true;
    public override bool Selectable => false;
    public override object RowKey(NameHistoryRow row) => row.Id;

    public override IReadOnlyList<GridColumn<NameHistoryRow>> Columns { get; } =
    [
        new("computer", "Computer (current name)", r => r.ComputerName, SortField: "computer"),
        new("name", "Name", r => r.Name, SortField: "name"),
        new("firstSeen", "First seen", r => r.FirstSeenUtc, ColumnKind.Date, "firstSeen"),
        new("lastSeen", "Last seen", r => r.LastSeenUtc, ColumnKind.Date, "lastSeen"),
        new("current", "Current", r => r.IsCurrent, ColumnKind.Bool, "current"),
    ];

    public override IReadOnlyList<GridField<ComputerNameHistory>> Fields(GridContext ctx) =>
    [
        GridField.Text<ComputerNameHistory>("computer", "Computer", h => h.Computer!.Name, search: true),
        GridField.Text<ComputerNameHistory>("name", "Name", h => h.Name, search: true),
        GridField.Date<ComputerNameHistory>("firstSeen", "First seen", h => h.FirstSeenUtc),
        GridField.Date<ComputerNameHistory>("lastSeen", "Last seen", h => h.LastSeenUtc),
        GridField.Bool<ComputerNameHistory>("current", "Current", h => h.IsCurrent),
    ];

    protected override async ValueTask<IQueryable<ComputerNameHistory>> SourceAsync(GridContext ctx)
    {
        IQueryable<ComputerNameHistory> q = ctx.Db.ComputerNameHistory.AsNoTracking();
        if (await ConsoleLists.ComputerChainAsync(ctx) is { } chain) q = q.Where(h => chain.Contains(h.ComputerId));
        return q;
    }

    protected override Expression<Func<ComputerNameHistory, NameHistoryRow>> Projection(GridContext ctx) => h => new NameHistoryRow
    {
        Id = h.Id, ComputerId = h.ComputerId, ComputerName = h.Computer!.Name, Name = h.Name,
        FirstSeenUtc = h.FirstSeenUtc, LastSeenUtc = h.LastSeenUtc, IsCurrent = h.IsCurrent,
    };

    protected override IReadOnlyList<GridSort<ComputerNameHistory>> TieBreakers(GridContext ctx) =>
        [ConsoleLists.By<ComputerNameHistory, long>(h => h.Id, true)];
}

public sealed class UserHistoryList : GridList<UserLogonHistory, UserHistoryRow>
{
    public override string Name => "user-history";
    public override IReadOnlyList<string> ScopeKeys => ["computer"];
    public override string DefaultSort => "logon";
    public override bool DefaultDescending => true;
    public override bool Selectable => false;
    public override object RowKey(UserHistoryRow row) => row.Id;

    public override IReadOnlyList<GridColumn<UserHistoryRow>> Columns { get; } =
    [
        new("computer", "Computer", r => r.ComputerName, SortField: "computer"),
        new("user", "User", r => r.UserName, SortField: "user"),
        new("logon", "Logon", r => r.LogonAtUtc, ColumnKind.Date, "logon"),
        new("logoff", "Logoff", r => r.LogoffAtUtc, ColumnKind.Date, "logoff"),
        new("session", "Session", r => r.SessionId, ColumnKind.Number, DefaultVisible: false),
    ];

    public override IReadOnlyList<GridField<UserLogonHistory>> Fields(GridContext ctx) =>
    [
        GridField.Text<UserLogonHistory>("computer", "Computer", h => h.Computer!.Name, search: true),
        GridField.Text<UserLogonHistory>("user", "User", h => h.UserName, search: true),
        GridField.Date<UserLogonHistory>("logon", "Logon", h => h.LogonAtUtc),
        GridField.Date<UserLogonHistory>("logoff", "Logoff", h => h.LogoffAtUtc),
        GridField.Bool<UserLogonHistory>("loggedOn", "Still logged on", h => h.LogoffAtUtc == null, sortable: false),
    ];

    protected override async ValueTask<IQueryable<UserLogonHistory>> SourceAsync(GridContext ctx)
    {
        IQueryable<UserLogonHistory> q = ctx.Db.UserLogonHistory.AsNoTracking();
        if (await ConsoleLists.ComputerChainAsync(ctx) is { } chain) q = q.Where(h => chain.Contains(h.ComputerId));
        return q;
    }

    protected override Expression<Func<UserLogonHistory, UserHistoryRow>> Projection(GridContext ctx) => h => new UserHistoryRow
    {
        Id = h.Id, ComputerId = h.ComputerId, ComputerName = h.Computer!.Name, UserName = h.UserName, SessionId = h.SessionId,
        LogonAtUtc = h.LogonAtUtc, LogoffAtUtc = h.LogoffAtUtc,
    };

    protected override IReadOnlyList<GridSort<UserLogonHistory>> TieBreakers(GridContext ctx) =>
        [ConsoleLists.By<UserLogonHistory, long>(h => h.Id, true)];
}

public sealed class IpHistoryList : GridList<ComputerIpHistory, IpHistoryRow>
{
    public override string Name => "ip-history";
    public override IReadOnlyList<string> ScopeKeys => ["computer"];
    public override string DefaultSort => "lastSeen";
    public override bool DefaultDescending => true;
    public override bool Selectable => false;
    public override object RowKey(IpHistoryRow row) => row.Id;

    public override IReadOnlyList<GridColumn<IpHistoryRow>> Columns { get; } =
    [
        new("computer", "Computer", r => r.ComputerName, SortField: "computer"),
        new("kind", "Kind", r => r.Kind.ToString(), SortField: "kind"),
        new("address", "Address", r => r.Address, SortField: "address"),
        new("firstSeen", "First seen", r => r.FirstSeenUtc, ColumnKind.Date, "firstSeen"),
        new("lastSeen", "Last seen", r => r.LastSeenUtc, ColumnKind.Date, "lastSeen"),
        new("current", "Current", r => r.IsCurrent, ColumnKind.Bool, "current"),
    ];

    public override IReadOnlyList<GridField<ComputerIpHistory>> Fields(GridContext ctx) =>
    [
        GridField.Text<ComputerIpHistory>("computer", "Computer", h => h.Computer!.Name, search: true),
        GridField.Text<ComputerIpHistory>("address", "Address", h => h.Address, search: true),
        GridField.Enum<ComputerIpHistory, IpAddressKind>("kind", "Kind", h => h.Kind),
        GridField.Date<ComputerIpHistory>("firstSeen", "First seen", h => h.FirstSeenUtc),
        GridField.Date<ComputerIpHistory>("lastSeen", "Last seen", h => h.LastSeenUtc),
        GridField.Bool<ComputerIpHistory>("current", "Current", h => h.IsCurrent),
    ];

    protected override async ValueTask<IQueryable<ComputerIpHistory>> SourceAsync(GridContext ctx)
    {
        IQueryable<ComputerIpHistory> q = ctx.Db.ComputerIpHistory.AsNoTracking();
        if (await ConsoleLists.ComputerChainAsync(ctx) is { } chain) q = q.Where(h => chain.Contains(h.ComputerId));
        return q;
    }

    protected override Expression<Func<ComputerIpHistory, IpHistoryRow>> Projection(GridContext ctx) => h => new IpHistoryRow
    {
        Id = h.Id, ComputerId = h.ComputerId, ComputerName = h.Computer!.Name, Kind = h.Kind, Address = h.Address,
        FirstSeenUtc = h.FirstSeenUtc, LastSeenUtc = h.LastSeenUtc, IsCurrent = h.IsCurrent,
    };

    protected override IReadOnlyList<GridSort<ComputerIpHistory>> TieBreakers(GridContext ctx) =>
        [ConsoleLists.By<ComputerIpHistory, long>(h => h.Id, true)];
}

// ---------------------------------------------------------------------------------------------- announcements

public sealed class AnnouncementsList : GridList<Announcement, AnnouncementRow>
{
    public override string Name => "announcements";
    public override string DefaultSort => "start";
    public override bool DefaultDescending => true;
    public override object RowKey(AnnouncementRow row) => row.Id;

    public static readonly IReadOnlyList<GridOption> StatusOptions =
        Enum.GetValues<AnnouncementStatus>().Select(s => new GridOption(s.ToString(), s.ToString())).ToList();

    public override IReadOnlyList<GridColumn<AnnouncementRow>> Columns { get; } =
    [
        new("title", "Title", r => r.Title, SortField: "title"),
        new("status", "Status", r => r.StatusAt(DateTime.UtcNow).ToString(), SortField: "status"),
        new("start", "Start", r => r.StartUtc, ColumnKind.Date, "start"),
        new("end", "End", r => r.EndUtc, ColumnKind.Date, "end"),
        new("target", "Target", r => r.TargetSummary),
        new("anonymous", "Anonymous", r => r.AnonymousAnswers, ColumnKind.Bool, "anonymous"),
        new("rate", "Confirmation rate", r => r.Rate, ColumnKind.Percent, "rate"),
        new("confirmed", "Confirmed / delivered", r => $"{r.ConfirmedUsers} / {r.DeliveredUsers}", DefaultVisible: false),
        new("updated", "Last change", r => r.UpdatedAtUtc, ColumnKind.Date, "updated", DefaultVisible: false),
        new("updatedBy", "Changed by", r => r.UpdatedBy, DefaultVisible: false),
    ];

    public override IReadOnlyList<GridField<Announcement>> Fields(GridContext ctx)
    {
        var db = ctx.Db;
        Expression<Func<Announcement, double>> rate = a =>
            db.Deliveries.Where(d => d.AnnouncementId == a.Id).Select(d => d.UserKey).Distinct().Count() == 0
                ? 0.0
                : (double)db.Confirmations.Where(c => c.AnnouncementId == a.Id).Select(c => c.UserKey).Distinct().Count()
                  / db.Deliveries.Where(d => d.AnnouncementId == a.Id).Select(d => d.UserKey).Distinct().Count();
        // Status order: draft, scheduled, active, ended (by start for non-drafts).
        Expression<Func<Announcement, int>> statusSort = a => a.IsDraft ? 0 : 1;
        return
        [
            GridField.Text<Announcement>("title", "Title", a => a.Title, search: true),
            GridField.Choice<Announcement>("status", "Status", StatusOptions, (value, now) => value switch
            {
                nameof(AnnouncementStatus.Draft) => a => a.IsDraft,
                nameof(AnnouncementStatus.Scheduled) => a => !a.IsDraft && a.StartUtc > now,
                nameof(AnnouncementStatus.Active) => a => !a.IsDraft && a.StartUtc <= now && a.EndUtc >= now,
                nameof(AnnouncementStatus.Ended) => a => !a.IsDraft && a.EndUtc < now,
                _ => null,
            }, statusSort),
            GridField.Date<Announcement>("start", "Start", a => a.StartUtc),
            GridField.Date<Announcement>("end", "End", a => a.EndUtc),
            GridField.CustomText<Announcement>("target", "Target",
                v => a => a.Targets.Any(t => t.DisplayName.ToLower().Contains(v) || (t.Value != null && t.Value.ToLower().Contains(v)))
                          || ("everyone".Contains(v) && !a.Targets.Any()),
                search: true),
            GridField.Bool<Announcement>("anonymous", "Anonymous answers", a => a.AnonymousAnswers),
            GridField.Number<Announcement>("rate", "Confirmation rate (0–1)", rate),
            GridField.Date<Announcement>("updated", "Last change", a => a.UpdatedAtUtc),
        ];
    }

    protected override ValueTask<IQueryable<Announcement>> SourceAsync(GridContext ctx) =>
        ValueTask.FromResult<IQueryable<Announcement>>(ctx.Db.Announcements.AsNoTracking());

    protected override Expression<Func<Announcement, AnnouncementRow>> Projection(GridContext ctx)
    {
        var db = ctx.Db;
        return a => new AnnouncementRow
        {
            Id = a.Id, Title = a.Title, IsDraft = a.IsDraft, StartUtc = a.StartUtc, EndUtc = a.EndUtc,
            Targets = a.Targets.OrderBy(t => t.DisplayName).Select(t => t.DisplayName != "" ? t.DisplayName : (t.Value ?? "")).ToList(),
            AnonymousAnswers = a.AnonymousAnswers,
            DeliveredUsers = db.Deliveries.Where(d => d.AnnouncementId == a.Id).Select(d => d.UserKey).Distinct().Count(),
            ConfirmedUsers = db.Confirmations.Where(c => c.AnnouncementId == a.Id).Select(c => c.UserKey).Distinct().Count(),
            Rate = db.Deliveries.Where(d => d.AnnouncementId == a.Id).Select(d => d.UserKey).Distinct().Count() == 0
                ? 0.0
                : (double)db.Confirmations.Where(c => c.AnnouncementId == a.Id).Select(c => c.UserKey).Distinct().Count()
                  / db.Deliveries.Where(d => d.AnnouncementId == a.Id).Select(d => d.UserKey).Distinct().Count(),
            UpdatedAtUtc = a.UpdatedAtUtc, UpdatedBy = a.UpdatedBy,
        };
    }

    protected override IReadOnlyList<GridSort<Announcement>> TieBreakers(GridContext ctx) =>
        [ConsoleLists.By<Announcement, DateTime>(a => a.StartUtc, true), ConsoleLists.By<Announcement, Guid>(a => a.Id)];
}

public sealed class ConfirmationsList : GridList<Confirmation, ConfirmationRow>
{
    public override string Name => "confirmations";
    public override IReadOnlyList<string> ScopeKeys => ["announcement", "computer"];
    public override string DefaultSort => "time";
    public override bool DefaultDescending => true;
    public override bool Selectable => false;
    public override object RowKey(ConfirmationRow row) => row.Id;

    public override IReadOnlyList<GridColumn<ConfirmationRow>> Columns { get; } =
    [
        new("announcement", "Announcement", r => r.AnnouncementTitle, SortField: "announcement"),
        new("computer", "Computer", r => r.ComputerName, SortField: "computer"),
        new("user", "User", r => r.UserName, SortField: "user"),
        new("button", "Button", r => r.ButtonLabel, SortField: "button"),
        new("time", "Time", r => r.ConfirmedAtUtc, ColumnKind.Date, "time"),
    ];

    public override IReadOnlyList<GridField<Confirmation>> Fields(GridContext ctx) =>
    [
        GridField.Text<Confirmation>("announcement", "Announcement", c => c.Announcement!.Title, search: true),
        GridField.Text<Confirmation>("computer", "Computer", c => c.ComputerName, search: true),
        GridField.Text<Confirmation>("user", "User", c => c.UserName, search: true),
        GridField.Text<Confirmation>("button", "Button", c => c.ButtonLabel),
        GridField.Date<Confirmation>("time", "Time", c => c.ConfirmedAtUtc),
    ];

    protected override async ValueTask<IQueryable<Confirmation>> SourceAsync(GridContext ctx)
    {
        IQueryable<Confirmation> q = ctx.Db.Confirmations.AsNoTracking();
        if (ctx.GetGuid("announcement") is { } aid) q = q.Where(c => c.AnnouncementId == aid);
        if (await ConsoleLists.ComputerChainAsync(ctx) is { } chain) q = q.Where(c => chain.Contains(c.ComputerId));
        return q;
    }

    protected override Expression<Func<Confirmation, ConfirmationRow>> Projection(GridContext ctx) => c => new ConfirmationRow
    {
        Id = c.Id, AnnouncementId = c.AnnouncementId, AnnouncementTitle = c.Announcement!.Title, ComputerId = c.ComputerId,
        ComputerName = c.ComputerName, UserName = c.UserName, ButtonLabel = c.ButtonLabel, ConfirmedAtUtc = c.ConfirmedAtUtc,
    };

    protected override IReadOnlyList<GridSort<Confirmation>> TieBreakers(GridContext ctx) => [ConsoleLists.By<Confirmation, Guid>(c => c.Id)];
}

public sealed class DeliveriesList : GridList<Delivery, DeliveryRow>
{
    public override string Name => "deliveries";
    public override IReadOnlyList<string> ScopeKeys => ["announcement", "computer"];
    public override string DefaultSort => "sent";
    public override bool DefaultDescending => true;
    public override bool Selectable => false;
    public override object RowKey(DeliveryRow row) => row.Id;

    public override IReadOnlyList<GridColumn<DeliveryRow>> Columns { get; } =
    [
        new("announcement", "Announcement", r => r.AnnouncementTitle, SortField: "announcement"),
        new("computer", "Computer", r => r.ComputerName, SortField: "computer"),
        new("user", "User", r => r.UserName, SortField: "user"),
        new("sent", "Sent", r => r.SentAtUtc, ColumnKind.Date, "sent"),
        new("shown", "Shown", r => r.ShownAtUtc, ColumnKind.Date, "shown"),
        new("confirmed", "Confirmed", r => r.ConfirmedAtUtc, ColumnKind.Date, "confirmedAt"),
        new("withdrawn", "Withdrawn", r => r.WithdrawnAtUtc, ColumnKind.Date, "withdrawn", DefaultVisible: false),
    ];

    public override IReadOnlyList<GridField<Delivery>> Fields(GridContext ctx) =>
    [
        GridField.Text<Delivery>("announcement", "Announcement", d => d.Announcement!.Title, search: true),
        GridField.Text<Delivery>("computer", "Computer", d => d.Computer!.Name, search: true),
        GridField.Text<Delivery>("user", "User", d => d.UserName, search: true),
        GridField.Date<Delivery>("sent", "Sent", d => d.SentAtUtc),
        GridField.Date<Delivery>("shown", "Shown", d => d.ShownAtUtc),
        GridField.Date<Delivery>("confirmedAt", "Confirmed", d => d.ConfirmedAtUtc),
        GridField.Bool<Delivery>("confirmed", "Confirmed", d => d.ConfirmedAtUtc != null, sortable: false),
        GridField.Date<Delivery>("withdrawn", "Withdrawn", d => d.WithdrawnAtUtc),
    ];

    protected override async ValueTask<IQueryable<Delivery>> SourceAsync(GridContext ctx)
    {
        IQueryable<Delivery> q = ctx.Db.Deliveries.AsNoTracking();
        if (ctx.GetGuid("announcement") is { } aid) q = q.Where(d => d.AnnouncementId == aid);
        if (await ConsoleLists.ComputerChainAsync(ctx) is { } chain) q = q.Where(d => chain.Contains(d.ComputerId));
        return q;
    }

    protected override Expression<Func<Delivery, DeliveryRow>> Projection(GridContext ctx) => d => new DeliveryRow
    {
        Id = d.Id, AnnouncementId = d.AnnouncementId, AnnouncementTitle = d.Announcement!.Title, ComputerId = d.ComputerId,
        ComputerName = d.Computer!.Name, UserName = d.UserName, SentAtUtc = d.SentAtUtc, ShownAtUtc = d.ShownAtUtc,
        ConfirmedAtUtc = d.ConfirmedAtUtc, WithdrawnAtUtc = d.WithdrawnAtUtc,
    };

    protected override IReadOnlyList<GridSort<Delivery>> TieBreakers(GridContext ctx) => [ConsoleLists.By<Delivery, Guid>(d => d.Id)];
}

// ---------------------------------------------------------------------------------------------- answers

/// <summary>Named answers: linked to their confirmation, so user, computer and time are available.</summary>
public sealed class NamedAnswersList : GridList<AnswerSelection, NamedAnswerRow>
{
    public override string Name => "answers";
    public override IReadOnlyList<string> ScopeKeys => ["announcement"];
    public override string DefaultSort => "time";
    public override bool DefaultDescending => true;
    public override bool Selectable => false;
    public override object RowKey(NamedAnswerRow row) => row.Id;

    public override IReadOnlyList<GridColumn<NamedAnswerRow>> Columns { get; } =
    [
        new("announcement", "Announcement", r => r.AnnouncementTitle, SortField: "announcement"),
        new("question", "Question", r => r.Question, SortField: "question"),
        new("option", "Option", r => r.Option, SortField: "option"),
        new("user", "User", r => r.UserName, SortField: "user"),
        new("computer", "Computer", r => r.ComputerName, SortField: "computer"),
        new("time", "Time", r => r.ConfirmedAtUtc, ColumnKind.Date, "time"),
    ];

    public override IReadOnlyList<GridField<AnswerSelection>> Fields(GridContext ctx) =>
    [
        GridField.Text<AnswerSelection>("announcement", "Announcement", s => s.Answer!.AnswerSet!.Announcement!.Title, search: true),
        GridField.Text<AnswerSelection>("question", "Question", s => s.Answer!.QuestionPrompt, search: true),
        GridField.Text<AnswerSelection>("option", "Option", s => s.OptionLabel, search: true),
        GridField.Text<AnswerSelection>("user", "User", s => s.Answer!.AnswerSet!.Confirmation!.UserName, search: true),
        GridField.Text<AnswerSelection>("computer", "Computer", s => s.Answer!.AnswerSet!.Confirmation!.ComputerName, search: true),
        GridField.Date<AnswerSelection>("time", "Time", s => s.Answer!.AnswerSet!.Confirmation!.ConfirmedAtUtc),
    ];

    protected override ValueTask<IQueryable<AnswerSelection>> SourceAsync(GridContext ctx)
    {
        var q = ctx.Db.AnswerSelections.AsNoTracking().Where(s => s.Answer!.AnswerSet!.ConfirmationId != null);
        if (ctx.GetGuid("announcement") is { } aid) q = q.Where(s => s.Answer!.AnswerSet!.AnnouncementId == aid);
        return ValueTask.FromResult(q);
    }

    protected override Expression<Func<AnswerSelection, NamedAnswerRow>> Projection(GridContext ctx) => s => new NamedAnswerRow
    {
        Id = s.Id, AnnouncementId = s.Answer!.AnswerSet!.AnnouncementId, AnnouncementTitle = s.Answer.AnswerSet.Announcement!.Title,
        Question = s.Answer.QuestionPrompt, Option = s.OptionLabel,
        UserName = s.Answer.AnswerSet.Confirmation!.UserName, ComputerName = s.Answer.AnswerSet.Confirmation.ComputerName,
        ConfirmedAtUtc = s.Answer.AnswerSet.Confirmation.ConfirmedAtUtc,
    };

    protected override IReadOnlyList<GridSort<AnswerSelection>> TieBreakers(GridContext ctx) =>
        [ConsoleLists.By<AnswerSelection, string>(s => s.Answer!.QuestionPrompt), ConsoleLists.By<AnswerSelection, Guid>(s => s.Id)];
}

/// <summary>
/// Anonymous answers (plan 8.4). Only announcement, question and option exist as fields and columns — never user,
/// computer or time — and rows are ordered by content only (title, question, option), never by id or storage order.
/// The row type has no identity at all.
/// </summary>
public sealed class AnonymousAnswersList : GridList<AnswerSelection, AnonymousAnswerRow>
{
    public override string Name => "anonymous-answers";
    public override IReadOnlyList<string> ScopeKeys => ["announcement"];
    public override string DefaultSort => "announcement";
    public override bool Selectable => false;
    public override object RowKey(AnonymousAnswerRow row) => row;

    public override IReadOnlyList<GridColumn<AnonymousAnswerRow>> Columns { get; } =
    [
        new("announcement", "Announcement", r => r.AnnouncementTitle, SortField: "announcement"),
        new("question", "Question", r => r.Question, SortField: "question"),
        new("option", "Option", r => r.Option, SortField: "option"),
    ];

    public override IReadOnlyList<GridField<AnswerSelection>> Fields(GridContext ctx) =>
    [
        GridField.Text<AnswerSelection>("announcement", "Announcement", s => s.Answer!.AnswerSet!.Announcement!.Title, search: true),
        GridField.Text<AnswerSelection>("question", "Question", s => s.Answer!.QuestionPrompt, search: true),
        GridField.Text<AnswerSelection>("option", "Option", s => s.OptionLabel, search: true),
    ];

    protected override ValueTask<IQueryable<AnswerSelection>> SourceAsync(GridContext ctx)
    {
        var q = ctx.Db.AnswerSelections.AsNoTracking().Where(s => s.Answer!.AnswerSet!.ConfirmationId == null);
        if (ctx.GetGuid("announcement") is { } aid) q = q.Where(s => s.Answer!.AnswerSet!.AnnouncementId == aid);
        return ValueTask.FromResult(q);
    }

    protected override Expression<Func<AnswerSelection, AnonymousAnswerRow>> Projection(GridContext ctx) =>
        s => new AnonymousAnswerRow(s.Answer!.AnswerSet!.AnnouncementId, s.Answer.AnswerSet.Announcement!.Title,
            s.Answer.QuestionPrompt, s.OptionLabel);

    /// <summary>Content only. No Id, no physical order.</summary>
    protected override IReadOnlyList<GridSort<AnswerSelection>> TieBreakers(GridContext ctx) =>
    [
        ConsoleLists.By<AnswerSelection, string>(s => s.Answer!.AnswerSet!.Announcement!.Title),
        ConsoleLists.By<AnswerSelection, string>(s => s.Answer!.QuestionPrompt),
        ConsoleLists.By<AnswerSelection, string>(s => s.OptionLabel),
    ];
}

// ---------------------------------------------------------------------------------------------- agent updates, audit, directory

public sealed class AgentUpdatesList : GridList<AgentUpdateTask, AgentUpdateRow>
{
    public override string Name => "agent-updates";
    public override string DefaultSort => "created";
    public override bool DefaultDescending => true;
    public override object RowKey(AgentUpdateRow row) => row.Id;

    public override IReadOnlyList<GridColumn<AgentUpdateRow>> Columns { get; } =
    [
        new("computer", "Computer", r => r.ComputerName, SortField: "computer"),
        new("from", "From", r => r.FromVersion, SortField: "from"),
        new("to", "To", r => r.ToVersion, SortField: "to"),
        new("state", "State", r => r.State.ToString(), SortField: "state"),
        new("requestedBy", "Requested by", r => r.RequestedBy, SortField: "requestedBy"),
        new("created", "Requested", r => r.CreatedAtUtc, ColumnKind.Date, "created"),
        new("updated", "Last change", r => r.UpdatedAtUtc, ColumnKind.Date, "updated"),
        new("finished", "Finished", r => r.FinishedAtUtc, ColumnKind.Date, "finished", DefaultVisible: false),
        new("bytes", "Bytes sent", r => r.BytesSent, ColumnKind.Number, DefaultVisible: false),
        new("error", "Error", r => r.Error),
    ];

    public override IReadOnlyList<GridField<AgentUpdateTask>> Fields(GridContext ctx) =>
    [
        GridField.Text<AgentUpdateTask>("computer", "Computer", t => t.Computer!.Name, search: true),
        GridField.Text<AgentUpdateTask>("from", "From version", t => t.FromVersion),
        GridField.Text<AgentUpdateTask>("to", "To version", t => t.ToVersion),
        GridField.Enum<AgentUpdateTask, AgentUpdateState>("state", "State", t => t.State),
        GridField.Text<AgentUpdateTask>("requestedBy", "Requested by", t => t.RequestedBy, search: true),
        GridField.Date<AgentUpdateTask>("created", "Requested", t => t.CreatedAtUtc),
        GridField.Date<AgentUpdateTask>("updated", "Last change", t => t.UpdatedAtUtc),
        GridField.Date<AgentUpdateTask>("finished", "Finished", t => t.FinishedAtUtc),
        GridField.Text<AgentUpdateTask>("error", "Error", t => t.Error, search: true),
    ];

    protected override ValueTask<IQueryable<AgentUpdateTask>> SourceAsync(GridContext ctx) =>
        ValueTask.FromResult<IQueryable<AgentUpdateTask>>(ctx.Db.AgentUpdateTasks.AsNoTracking());

    protected override Expression<Func<AgentUpdateTask, AgentUpdateRow>> Projection(GridContext ctx) => t => new AgentUpdateRow
    {
        Id = t.Id, ComputerId = t.ComputerId, ComputerName = t.Computer!.Name, FromVersion = t.FromVersion, ToVersion = t.ToVersion,
        State = t.State, RequestedBy = t.RequestedBy, CreatedAtUtc = t.CreatedAtUtc, UpdatedAtUtc = t.UpdatedAtUtc,
        FinishedAtUtc = t.FinishedAtUtc, BytesSent = t.BytesSent, Error = t.Error,
    };

    protected override IReadOnlyList<GridSort<AgentUpdateTask>> TieBreakers(GridContext ctx) => [ConsoleLists.By<AgentUpdateTask, Guid>(t => t.Id)];
}

public sealed class AuditList : GridList<AuditLogEntry, AuditRow>
{
    public override string Name => "audit";
    public override string DefaultSort => "time";
    public override bool DefaultDescending => true;
    public override bool Selectable => false;
    public override object RowKey(AuditRow row) => row.Id;

    public override IReadOnlyList<GridColumn<AuditRow>> Columns { get; } =
    [
        new("time", "Time", r => r.AtUtc, ColumnKind.Date, "time"),
        new("actor", "Who", r => r.Actor, SortField: "actor"),
        new("action", "Action", r => r.Action, SortField: "action"),
        new("targetType", "Target type", r => r.TargetType, SortField: "targetType"),
        new("targetId", "Target", r => r.TargetId),
        new("details", "Details", r => r.Details),
        new("ip", "IP", r => r.RemoteIp, DefaultVisible: false),
    ];

    public override IReadOnlyList<GridField<AuditLogEntry>> Fields(GridContext ctx) =>
    [
        GridField.Date<AuditLogEntry>("time", "Time", e => e.AtUtc),
        GridField.Text<AuditLogEntry>("actor", "Who", e => e.Actor, search: true),
        GridField.Text<AuditLogEntry>("action", "Action", e => e.Action, search: true),
        GridField.Text<AuditLogEntry>("targetType", "Target type", e => e.TargetType),
        GridField.Text<AuditLogEntry>("targetId", "Target", e => e.TargetId, search: true),
        GridField.Text<AuditLogEntry>("details", "Details", e => e.Details, search: true, sortable: false),
        GridField.Text<AuditLogEntry>("ip", "IP", e => e.RemoteIp),
    ];

    protected override ValueTask<IQueryable<AuditLogEntry>> SourceAsync(GridContext ctx) =>
        ValueTask.FromResult<IQueryable<AuditLogEntry>>(ctx.Db.AuditLog.AsNoTracking());

    protected override Expression<Func<AuditLogEntry, AuditRow>> Projection(GridContext ctx) => e => new AuditRow
    {
        Id = e.Id, AtUtc = e.AtUtc, Actor = e.Actor, Action = e.Action, TargetType = e.TargetType, TargetId = e.TargetId,
        Details = e.Details, RemoteIp = e.RemoteIp,
    };

    protected override IReadOnlyList<GridSort<AuditLogEntry>> TieBreakers(GridContext ctx) => [ConsoleLists.By<AuditLogEntry, long>(e => e.Id, true)];
}

public sealed class DirectoryObjectsList : GridList<DirectoryObject, DirectoryObjectRow>
{
    public override string Name => "directory-objects";
    public override IReadOnlyList<string> ScopeKeys => ["parent"];
    public override string DefaultSort => "name";
    public override bool Selectable => false;
    public override object RowKey(DirectoryObjectRow row) => row.ObjectGuid;

    public override IReadOnlyList<GridColumn<DirectoryObjectRow>> Columns { get; } =
    [
        new("name", "Name", r => r.Name, SortField: "name"),
        new("kind", "Kind", r => GridField.Humanize(r.Kind.ToString()), SortField: "kind"),
        new("sam", "Account name", r => r.SamAccountName, SortField: "sam"),
        new("displayName", "Display name", r => r.DisplayName, DefaultVisible: false),
        new("dn", "Distinguished name", r => r.DistinguishedName, DefaultVisible: false),
        new("enabled", "Enabled", r => r.Enabled, ColumnKind.Bool, "enabled"),
        new("deleted", "Gone from AD", r => r.Deleted, ColumnKind.Bool, "deleted", DefaultVisible: false),
        new("synced", "Last synced", r => r.LastSyncedUtc, ColumnKind.Date, "synced"),
    ];

    public override IReadOnlyList<GridField<DirectoryObject>> Fields(GridContext ctx) =>
    [
        GridField.Text<DirectoryObject>("name", "Name", d => d.Name, search: true),
        GridField.Enum<DirectoryObject, DirectoryObjectKind>("kind", "Kind", d => d.Kind),
        GridField.Text<DirectoryObject>("sam", "Account name", d => d.SamAccountName, search: true),
        GridField.Text<DirectoryObject>("dn", "Distinguished name", d => d.DistinguishedName, search: true),
        GridField.Bool<DirectoryObject>("enabled", "Enabled", d => d.Enabled),
        GridField.Bool<DirectoryObject>("deleted", "Gone from AD", d => d.Deleted),
        GridField.Date<DirectoryObject>("synced", "Last synced", d => d.LastSyncedUtc),
    ];

    protected override ValueTask<IQueryable<DirectoryObject>> SourceAsync(GridContext ctx)
    {
        IQueryable<DirectoryObject> q = ctx.Db.DirectoryObjects.AsNoTracking();
        if (ctx.GetGuid("parent") is { } parent) q = q.Where(d => d.ParentGuid == parent);
        return ValueTask.FromResult(q);
    }

    protected override Expression<Func<DirectoryObject, DirectoryObjectRow>> Projection(GridContext ctx) => d => new DirectoryObjectRow
    {
        ObjectGuid = d.ObjectGuid, Kind = d.Kind, Name = d.Name, SamAccountName = d.SamAccountName, DisplayName = d.DisplayName,
        DistinguishedName = d.DistinguishedName, Enabled = d.Enabled, Deleted = d.Deleted, LastSyncedUtc = d.LastSyncedUtc,
    };

    protected override IReadOnlyList<GridSort<DirectoryObject>> TieBreakers(GridContext ctx) => [ConsoleLists.By<DirectoryObject, Guid>(d => d.ObjectGuid)];
}
