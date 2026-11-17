using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Data;
using DomainNotice.Server.Data.Entities;
using DomainNotice.Server.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DomainNotice.Server.Announcements;

/// <summary>Builds the facts for <see cref="Eligibility"/> from the database (synced directory tables, deliveries).</summary>
internal sealed class TargetingDataLoader(IOptions<DirectoryOptions> directoryOptions, ISettingsStore settings,
    ILogger<TargetingDataLoader> logger)
{
    private const long NetBiosCacheMs = 60_000;
    private sealed record NetBiosCache(string Name, long ReadAt);
    private volatile NetBiosCache? _netBios;
    private int _netBiosMissingLogged;

    public bool DirectoryConfigured => directoryOptions.Value.IsConfigured;

    public async Task<AwaitingApprovalPolicy> GetAwaitingApprovalPolicyAsync(CancellationToken ct)
    {
        var raw = await settings.GetAsync(SettingKeys.AwaitingApprovalReceives, ct);
        return Enum.TryParse<AwaitingApprovalPolicy>(raw, ignoreCase: true, out var p) && Enum.IsDefined(p)
            ? p
            : AwaitingApprovalPolicy.EveryoneAndUser;
    }

    /// <summary>Active announcements (not draft, StartUtc ≤ now ≤ EndUtc) with content and targets, ordered by StartUtc, CreatedAtUtc.</summary>
    public async Task<List<LoadedAnnouncement>> LoadActiveAsync(AppDbContext db, DateTime nowUtc, CancellationToken ct, Guid? onlyId = null)
    {
        var query = db.Announcements.AsNoTracking().AsSplitQuery()
            .Include(a => a.Buttons)
            .Include(a => a.Questions).ThenInclude(q => q.Options)
            .Include(a => a.Targets)
            .Where(a => !a.IsDraft && a.StartUtc <= nowUtc && a.EndUtc >= nowUtc);
        if (onlyId is { } id) query = query.Where(a => a.Id == id);
        var list = await query.OrderBy(a => a.StartUtc).ThenBy(a => a.CreatedAtUtc).ThenBy(a => a.Id).ToListAsync(ct);
        return await ToLoadedAsync(db, list, ct);
    }

    public async Task<List<LoadedAnnouncement>> ToLoadedAsync(AppDbContext db, List<Announcement> list, CancellationToken ct)
    {
        var ouGuids = list.SelectMany(a => a.Targets)
            .Where(t => t.Kind == TargetKind.OrganizationalUnit && t.DirectoryObjectGuid is not null)
            .Select(t => t.DirectoryObjectGuid!.Value).Distinct().ToList();
        var ouDns = ouGuids.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.DirectoryObjects.AsNoTracking().Where(o => ouGuids.Contains(o.ObjectGuid))
                .ToDictionaryAsync(o => o.ObjectGuid, o => o.DistinguishedNameLower, ct);

        return list.Select(a => new LoadedAnnouncement(a, new AnnouncementRule(
            a.Id, a.IsDraft, a.StartUtc, a.EndUtc, a.MaxDisplays, a.RepeatIntervalMinutes,
            a.Targets.Select(t => new TargetRule(t.Kind, t.DirectoryObjectGuid, t.ComputerId, t.Value,
                t.Kind == TargetKind.OrganizationalUnit && t.DirectoryObjectGuid is { } g && ouDns.TryGetValue(g, out var dn)
                    ? dn : null)).ToList()))).ToList();
    }

    public async Task<ComputerFacts?> LoadComputerAsync(AppDbContext db, Guid computerId, CancellationToken ct)
    {
        var c = await db.Computers.AsNoTracking().Where(x => x.Id == computerId)
            .Select(x => new { x.Id, x.AdLinkState, x.RevokedAtUtc, x.ReplacedByComputerId, x.DirectoryObjectGuid })
            .FirstOrDefaultAsync(ct);
        if (c is null) return null;

        string? dn = null;
        var groups = new HashSet<Guid>();
        if (c.AdLinkState is AdLinkState.MatchedByName or AdLinkState.Proven && c.DirectoryObjectGuid is { } adGuid)
        {
            dn = await db.DirectoryObjects.AsNoTracking().Where(o => o.ObjectGuid == adGuid)
                .Select(o => o.DistinguishedNameLower).FirstOrDefaultAsync(ct);
            groups = await LoadGroupsAsync(db, [adGuid], ct);
        }
        return new ComputerFacts(c.Id, c.AdLinkState, c.RevokedAtUtc is not null, c.ReplacedByComputerId is not null,
            c.DirectoryObjectGuid, dn, groups);
    }

    /// <summary>
    /// The reported user matched to synced AD users. Only one domain is supported, so:
    /// <list type="bullet">
    /// <item>"DOMAIN\user" matches by sAMAccountName only when DOMAIN is the stored NetBIOS name of the directory's
    /// domain (case-insensitive). A local Windows account ("PC01\ceo") or another domain gets no directory facts, even
    /// when a domain user has the same name. While the NetBIOS name is not known yet (no sync), nobody gets them.</item>
    /// <item>"user@domain" matches by userPrincipalName.</item>
    /// <item>A bare "user" (no domain) gets no directory facts.</item>
    /// </list>
    /// Without a configured directory, the user has no directory facts (User targets then match by name).
    /// </summary>
    public async Task<UserFacts> LoadUserAsync(AppDbContext db, string userName, CancellationToken ct)
    {
        if (!DirectoryConfigured) return UserFacts.NameOnly(userName);
        var full = userName.Trim().ToLowerInvariant();
        if (full.Length == 0) return UserFacts.NameOnly(userName);

        var users = db.DirectoryObjects.AsNoTracking().Where(o => o.Kind == DirectoryObjectKind.User && !o.Deleted);
        var slash = full.IndexOf('\\');
        if (slash >= 0)
        {
            var sam = full[(slash + 1)..];
            if (slash == 0 || sam.Length == 0 || sam.Contains('\\')) return UserFacts.NameOnly(userName);
            var netBios = await GetNetBiosNameAsync(ct);
            if (netBios is null || !string.Equals(full[..slash], netBios, StringComparison.OrdinalIgnoreCase))
                return UserFacts.NameOnly(userName);
            users = users.Where(o => o.SamAccountNameLower == sam);
        }
        else if (full.Contains('@'))
            users = users.Where(o => o.UserPrincipalName != null && o.UserPrincipalName.ToLower() == full);
        else
            return UserFacts.NameOnly(userName);

        var objects = await users.Select(o => new { o.ObjectGuid, o.DistinguishedNameLower }).ToListAsync(ct);
        if (objects.Count == 0) return UserFacts.NameOnly(userName);

        var guids = objects.Select(o => o.ObjectGuid).ToHashSet();
        var groups = await LoadGroupsAsync(db, guids.ToList(), ct);
        return new UserFacts(userName, guids, objects.Select(o => o.DistinguishedNameLower).ToList(), groups);
    }

    /// <summary>The stored NetBIOS name (written by the directory sync), cached for a minute once known.</summary>
    private async Task<string?> GetNetBiosNameAsync(CancellationToken ct)
    {
        var now = Environment.TickCount64;
        if (_netBios is { } cached && now - cached.ReadAt < NetBiosCacheMs) return cached.Name;
        var name = (await settings.GetAsync(SettingKeys.DirectoryNetBiosName, ct))?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            if (Interlocked.Exchange(ref _netBiosMissingLogged, 1) == 0)
                logger.LogWarning("The NetBIOS name of the directory's domain is not known yet (the directory sync stores it); " +
                                  "until then users reported by agents get no directory groups or OUs.");
            return null;
        }
        _netBios = new NetBiosCache(name, now);
        return name;
    }

    private static async Task<HashSet<Guid>> LoadGroupsAsync(AppDbContext db, List<Guid> members, CancellationToken ct)
    {
        // Transitive table is the source of truth; direct memberships are added defensively (same set if the sync
        // already includes them).
        var transitive = await db.DirectoryTransitiveMemberships.AsNoTracking()
            .Where(m => members.Contains(m.MemberGuid)).Select(m => m.GroupGuid).ToListAsync(ct);
        var direct = await db.DirectoryMemberships.AsNoTracking()
            .Where(m => members.Contains(m.MemberGuid)).Select(m => m.GroupGuid).ToListAsync(ct);
        return transitive.Concat(direct).ToHashSet();
    }

    /// <summary>Per (announcement, user key): shown count and last shown time across all computers (P2, D8).</summary>
    public static async Task<Dictionary<(Guid AnnouncementId, string UserKey), UserAnnouncementHistory>> LoadHistoryAsync(
        AppDbContext db, IReadOnlyCollection<Guid> announcementIds, IReadOnlyCollection<string> userKeys, Guid thisComputerId,
        CancellationToken ct)
    {
        var result = new Dictionary<(Guid, string), UserAnnouncementHistory>();
        if (announcementIds.Count == 0 || userKeys.Count == 0) return result;
        var ids = announcementIds.ToList();
        var keys = userKeys.ToList();
        var rows = await db.Deliveries.AsNoTracking()
            .Where(d => ids.Contains(d.AnnouncementId) && keys.Contains(d.UserKey)
                        && (d.ShownAtUtc != null))
            .Select(d => new { d.AnnouncementId, d.UserKey, d.ComputerId, d.ShownAtUtc, d.ConfirmedAtUtc, d.WithdrawnAtUtc })
            .ToListAsync(ct);
        foreach (var g in rows.GroupBy(r => (r.AnnouncementId, r.UserKey)))
        {
            result[g.Key] = new UserAnnouncementHistory(
                g.Count(),
                g.Max(r => r.ShownAtUtc),
                g.Any(r => r.ComputerId != thisComputerId && r.ConfirmedAtUtc == null && r.WithdrawnAtUtc == null));
        }
        return result;
    }
}

internal sealed record LoadedAnnouncement(Announcement Entity, AnnouncementRule Rule);
