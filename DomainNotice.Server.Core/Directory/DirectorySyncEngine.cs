using System.Globalization;
using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Data;
using DomainNotice.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DomainNotice.Server.Directory;

/// <summary>
/// Copies the directory into DirectoryObjects / DirectoryMemberships / DirectoryTransitiveMemberships (plan 7 item 5).
/// <list type="bullet">
/// <item>Objects are upserted by objectGUID, so renames and moves keep the row (and stored targets).</item>
/// <item>Full sync: every object under BaseDn; objects not seen are marked Deleted and lose their memberships.</item>
/// <item>Incremental: uSNChanged &gt; the highest USN stored for the answering DC (rootDSE dnsHostName). No stored
/// value for that DC (first run, DC switch) → full. When an OU/container is renamed or moved, its descendants keep
/// their uSNChanged, so their DN columns are rewritten here.</item>
/// <item>Direct memberships are rebuilt for every group that was read; member DNs that are not synced objects
/// (outside BaseDn, foreign security principals) are skipped. primaryGroupID membership (e.g. Domain Users) is not
/// in <c>member</c> and is not included.</item>
/// <item>The transitive table is rebuilt with a recursive CTE (UNION, so cycles terminate). It contains every
/// (group, member) pair reachable through nested groups, nested groups themselves included; a group is never
/// listed as its own member, even inside a cycle.</item>
/// <item>All table changes of one run are in one transaction, so readers never see a half-built state.</item>
/// </list>
/// </summary>
internal sealed class DirectorySyncEngine(
    IDbContextFactory<AppDbContext> dbFactory,
    ILdapSource source,
    ISettingsStore settings,
    TimeProvider time,
    ILogger<DirectorySyncEngine> logger)
{
    public const string SyncActor = "directory-sync";

    public async Task<DirectorySyncRun> RunAsync(DirectorySyncKind requested, string? requestedBy, CancellationToken ct)
    {
        var run = new DirectorySyncRun
        {
            Kind = requested,
            StartedAtUtc = time.GetUtcNow().UtcDateTime,
            RequestedBy = requestedBy,
        };
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            db.DirectorySyncRuns.Add(run);
            await db.SaveChangesAsync(ct);
        }

        try
        {
            await using var session = await source.OpenAsync(ct);
            var root = await session.ReadRootDseAsync(ct);
            var hostKey = (string.IsNullOrWhiteSpace(root.DnsHostName) ? session.Host : root.DnsHostName).Trim().ToLowerInvariant();
            run.Host = hostKey;

            long? since = null;
            if (requested == DirectorySyncKind.Incremental)
            {
                since = await GetIncrementalStartAsync(hostKey, root.HighestCommittedUsn, ct);
                if (since is null) run.Kind = DirectorySyncKind.Full;
            }

            var entries = new List<LdapDirectoryEntry>();
            await foreach (var e in session.SearchObjectsAsync(since, ct)) entries.Add(e);

            run.ObjectsChanged = await ApplyAsync(entries, run.Kind == DirectorySyncKind.Full, run.StartedAtUtc, ct);
            run.HighestUsn = root.HighestCommittedUsn;
            await settings.SetAsync(SettingKeys.DirectoryHighestUsnPrefix + hostKey,
                root.HighestCommittedUsn.ToString(CultureInfo.InvariantCulture), SyncActor, ct);
            run.Success = true;
            logger.LogInformation("Directory {Kind} sync from {Host}: {Count} objects changed, highest USN {Usn}.",
                run.Kind, hostKey, run.ObjectsChanged, run.HighestUsn);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            run.Success = false;
            run.Error = Truncate(e is DirectoryConnectException ? e.Message : $"{e.GetType().Name}: {e.Message}", 4000);
            logger.LogWarning(e, "Directory {Kind} sync failed.", run.Kind);
        }
        finally
        {
            run.FinishedAtUtc = time.GetUtcNow().UtcDateTime;
            if (run.Error is null && !run.Success) run.Error = "Cancelled.";
            await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
            db.DirectorySyncRuns.Update(run);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        return run;
    }

    /// <summary>Highest USN to continue from, or null when a full sync is needed.</summary>
    private async Task<long?> GetIncrementalStartAsync(string hostKey, long currentHighest, CancellationToken ct)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var anyFull = await db.DirectorySyncRuns.AnyAsync(r => r.Success && r.Kind == DirectorySyncKind.Full, ct);
            if (!anyFull) return null;
        }
        var stored = await settings.GetAsync(SettingKeys.DirectoryHighestUsnPrefix + hostKey, ct);
        if (!long.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var usn)) return null;
        if (usn > currentHighest) return null; // DC restored from backup or a different DC with the same name
        return usn;
    }

    internal static DirectoryObjectKind? Classify(IReadOnlyList<string> objectClasses)
    {
        bool Has(string c) => objectClasses.Any(x => string.Equals(x?.Trim(), c, StringComparison.OrdinalIgnoreCase));
        if (Has("computer")) return DirectoryObjectKind.Computer;
        if (Has("group")) return DirectoryObjectKind.Group;
        if (Has("domainDNS")) return DirectoryObjectKind.Domain;
        if (Has("organizationalUnit")) return DirectoryObjectKind.OrganizationalUnit;
        if (Has("container") || Has("builtinDomain")) return DirectoryObjectKind.Container;
        if (Has("user") && !Has("contact")) return DirectoryObjectKind.User;
        return null;
    }

    private static bool IsContainerKind(DirectoryObjectKind k) =>
        k is DirectoryObjectKind.Domain or DirectoryObjectKind.OrganizationalUnit or DirectoryObjectKind.Container;

    /// <summary>Returns the number of objects inserted, updated or marked deleted.</summary>
    internal async Task<int> ApplyAsync(IReadOnlyList<LdapDirectoryEntry> raw, bool full, DateTime nowUtc, CancellationToken ct)
    {
        // Last one wins if the source returned an object twice.
        var entries = new Dictionary<Guid, (LdapDirectoryEntry Entry, DirectoryObjectKind Kind)>();
        foreach (var e in raw)
        {
            if (e.ObjectGuid == Guid.Empty || string.IsNullOrWhiteSpace(e.DistinguishedName)) continue;
            if (Classify(e.ObjectClasses) is not { } kind) continue;
            entries[e.ObjectGuid] = (e, kind);
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var ids = entries.Keys.ToList();
        var existing = full
            ? await db.DirectoryObjects.ToDictionaryAsync(o => o.ObjectGuid, ct)
            : await db.DirectoryObjects.Where(o => ids.Contains(o.ObjectGuid)).ToDictionaryAsync(o => o.ObjectGuid, ct);

        var changed = 0;
        var renamedContainers = new List<(string OldDn, string NewDn)>();
        foreach (var (guid, (e, kind)) in entries)
        {
            if (!existing.TryGetValue(guid, out var o))
            {
                o = new DirectoryObject { ObjectGuid = guid };
                db.DirectoryObjects.Add(o);
                existing[guid] = o;
            }
            else if (!full && IsContainerKind(o.Kind) && !o.Deleted
                     && !string.Equals(o.DistinguishedName, e.DistinguishedName, StringComparison.Ordinal))
            {
                renamedContainers.Add((o.DistinguishedName, e.DistinguishedName));
            }
            if (Copy(o, e, kind, nowUtc)) changed++;
        }

        if (full)
        {
            foreach (var o in existing.Values)
            {
                if (o.Deleted || entries.ContainsKey(o.ObjectGuid)) continue;
                o.Deleted = true;
                o.LastSyncedUtc = nowUtc;
                changed++;
            }
        }
        await db.SaveChangesAsync(ct);

        // Descendants of a renamed/moved OU keep their uSNChanged, so an incremental sync does not return them.
        foreach (var (oldDn, newDn) in renamedContainers)
            changed += await RewriteDescendantsAsync(db, oldDn, newDn, entries.Keys, nowUtc, ct);

        // DN → GUID of every live object, after this run's changes.
        var dnMap = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var row in await db.DirectoryObjects.Where(o => !o.Deleted)
                     .Select(o => new { o.ObjectGuid, o.DistinguishedNameLower }).ToListAsync(ct))
            dnMap[row.DistinguishedNameLower] = row.ObjectGuid;

        // Parents.
        foreach (var guid in entries.Keys)
        {
            var o = existing[guid];
            var parentDn = DistinguishedNames.Parent(o.DistinguishedName)?.ToLowerInvariant();
            Guid? parent = parentDn is not null && dnMap.TryGetValue(parentDn, out var p) ? p : null;
            if (o.ParentGuid != parent) o.ParentGuid = parent;
        }
        await db.SaveChangesAsync(ct);

        // Direct memberships.
        var groups = entries.Where(kv => kv.Value.Kind == DirectoryObjectKind.Group).ToList();
        var membershipsChanged = full || groups.Count > 0;
        if (full)
        {
            await db.DirectoryMemberships.ExecuteDeleteAsync(ct);
        }
        else if (groups.Count > 0)
        {
            var groupIds = groups.Select(g => g.Key).ToList();
            await db.DirectoryMemberships.Where(m => groupIds.Contains(m.GroupGuid)).ExecuteDeleteAsync(ct);
        }
        var rows = new List<DirectoryMembership>();
        foreach (var (groupGuid, (e, _)) in groups)
        {
            var seen = new HashSet<Guid>();
            foreach (var memberDn in e.Members)
            {
                if (!dnMap.TryGetValue(memberDn.Trim().ToLowerInvariant(), out var memberGuid)) continue;
                if (memberGuid == groupGuid || !seen.Add(memberGuid)) continue;
                rows.Add(new DirectoryMembership { GroupGuid = groupGuid, MemberGuid = memberGuid });
            }
        }
        // Objects that changed kind away from Group lose their member rows.
        var noLongerGroups = entries.Where(kv => kv.Value.Kind != DirectoryObjectKind.Group).Select(kv => kv.Key).ToList();
        if (!full && noLongerGroups.Count > 0)
        {
            if (await db.DirectoryMemberships.Where(m => noLongerGroups.Contains(m.GroupGuid)).ExecuteDeleteAsync(ct) > 0)
                membershipsChanged = true;
        }
        // Deleted objects keep no memberships, as group or as member.
        var deletedCount = await db.DirectoryMemberships
            .Where(m => db.DirectoryObjects.Any(o => o.Deleted && (o.ObjectGuid == m.GroupGuid || o.ObjectGuid == m.MemberGuid)))
            .ExecuteDeleteAsync(ct);
        if (deletedCount > 0) membershipsChanged = true;
        db.DirectoryMemberships.AddRange(rows);
        await db.SaveChangesAsync(ct);

        if (membershipsChanged) await RebuildTransitiveAsync(db, ct);

        await tx.CommitAsync(ct);
        return changed;
    }

    private static bool Copy(DirectoryObject o, LdapDirectoryEntry e, DirectoryObjectKind kind, DateTime nowUtc)
    {
        var name = kind == DirectoryObjectKind.OrganizationalUnit
            ? First(e.Ou, e.Name, e.Cn, DistinguishedNames.FirstRdnValue(e.DistinguishedName))
            : First(e.Cn, e.Name, e.Ou, DistinguishedNames.FirstRdnValue(e.DistinguishedName));
        var enabled = kind is DirectoryObjectKind.User or DirectoryObjectKind.Computer ? !LdapValues.IsDisabled(e.UserAccountControl) : true;
        var sam = Blank(e.SamAccountName);
        var dirty = o.Kind != kind || o.DistinguishedName != e.DistinguishedName || o.Name != name || o.SamAccountName != sam
                    || o.DisplayName != Blank(e.DisplayName) || o.UserPrincipalName != Blank(e.UserPrincipalName)
                    || o.DnsHostName != Blank(e.DnsHostName) || o.ObjectSid != Blank(e.ObjectSid) || o.Enabled != enabled
                    || o.UsnChanged != e.UsnChanged || o.Deleted || o.LastSyncedUtc == default;
        o.Kind = kind;
        o.DistinguishedName = e.DistinguishedName;
        o.DistinguishedNameLower = e.DistinguishedName.ToLowerInvariant();
        o.Name = name;
        o.SamAccountName = sam;
        o.SamAccountNameLower = sam?.ToLowerInvariant();
        o.DisplayName = Blank(e.DisplayName);
        o.UserPrincipalName = Blank(e.UserPrincipalName);
        o.DnsHostName = Blank(e.DnsHostName);
        o.ObjectSid = Blank(e.ObjectSid);
        o.Enabled = enabled;
        o.UsnChanged = e.UsnChanged;
        o.Deleted = false;
        o.LastSyncedUtc = nowUtc;
        return dirty;
    }

    private static async Task<int> RewriteDescendantsAsync(AppDbContext db, string oldDn, string newDn,
        IEnumerable<Guid> alreadyUpdated, DateTime nowUtc, CancellationToken ct)
    {
        var suffix = "," + oldDn.ToLowerInvariant();
        var skip = alreadyUpdated.ToHashSet();
        var descendants = await db.DirectoryObjects
            .Where(o => !o.Deleted && o.DistinguishedNameLower.EndsWith(suffix))
            .ToListAsync(ct);
        var n = 0;
        foreach (var d in descendants)
        {
            if (skip.Contains(d.ObjectGuid)) continue;
            var prefix = d.DistinguishedName[..^oldDn.Length];
            d.DistinguishedName = prefix + newDn;
            d.DistinguishedNameLower = d.DistinguishedName.ToLowerInvariant();
            d.LastSyncedUtc = nowUtc;
            n++;
        }
        await db.SaveChangesAsync(ct);
        return n;
    }

    private static async Task RebuildTransitiveAsync(AppDbContext db, CancellationToken ct)
    {
        var (direct, dGroup, dMember) = Names<DirectoryMembership>(db);
        var (transitive, tGroup, tMember) = Names<DirectoryTransitiveMembership>(db);
        // Identifiers come from the EF model, not from input.
        var delete = $"""DELETE FROM "{transitive}";""";
        var insert = $"""
            WITH RECURSIVE closure(group_guid, member_guid) AS (
                SELECT "{dGroup}", "{dMember}" FROM "{direct}"
                UNION
                SELECT c.group_guid, d."{dMember}"
                FROM closure c JOIN "{direct}" d ON d."{dGroup}" = c.member_guid
            )
            INSERT INTO "{transitive}" ("{tGroup}", "{tMember}")
            SELECT group_guid, member_guid FROM closure WHERE group_guid <> member_guid;
            """;
        await db.Database.ExecuteSqlRawAsync(delete, ct);
        await db.Database.ExecuteSqlRawAsync(insert, ct);
    }

    private static (string Table, string Group, string Member) Names<T>(AppDbContext db)
    {
        var et = db.Model.FindEntityType(typeof(T))!;
        var table = et.GetTableName()!;
        var store = Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(table, et.GetSchema());
        return (table, et.FindProperty("GroupGuid")!.GetColumnName(store)!, et.FindProperty("MemberGuid")!.GetColumnName(store)!);
    }

    private static string First(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";

    private static string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
