using System.Collections.Concurrent;
using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Data;
using DomainNotice.Server.Data.Entities;
using DomainNotice.Server.Directory;
using DomainNotice.Server.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DomainNotice.Server.Accounts;

/// <summary>
/// Re-checks Console sessions on every request and every minute for open circuits.
/// <list type="bullet">
/// <item><b>Every session</b> ends Console:SessionMaxHours (default 12) after sign-in, also while in use. Sessions
/// without a sign-in time (cookies from before 1.0.5) are ended, so everyone signs in once after the upgrade.</item>
/// <item><b>Local accounts</b> must still exist, be enabled, and have the password they signed in with (a reset in
/// the Console or with --reset-local-password ends their sessions).</item>
/// <item><b>Domain users</b> are checked against the synced directory tables (no LDAP bind is possible without the
/// password): the user object (by objectGUID) must exist, not be deleted, be enabled, and still be in an admin or
/// viewer group giving the same role, nested groups included. A changed role ends the session (sign in again).
/// The synced data is used only when a successful sync started after the sign-in: before that, the live LDAP check
/// at sign-in is newer, and a user just added to a group would otherwise be signed out at once. A configured group
/// DN that is not in the synced tables (outside Directory:BaseDn) cannot be evaluated; that part of the check is
/// skipped with a warning. Membership through the primary group (primaryGroupID) is not synced and does not count.</item>
/// <item>Fails open (keeps the session, logs a warning) only when the directory has never been synced or the database
/// cannot be read; fails closed when the user is deleted, disabled or in no admin/viewer group.</item>
/// </list>
/// Positive results are cached for a few seconds, since every HTTP request with the cookie is validated.
/// </summary>
internal sealed class ConsoleSessionValidator(
    IDbContextFactory<AppDbContext> dbFactory,
    IOptions<ConsoleOptions> consoleOptions,
    IOptions<DirectoryOptions> directoryOptions,
    TimeProvider time,
    ILogger<ConsoleSessionValidator> logger) : IConsoleSessionValidator
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(15);
    /// <summary>Tolerated clock difference for a sign-in time in the future.</summary>
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);
    private const int MaxCacheEntries = 2000;

    private readonly ConcurrentDictionary<ConsoleSession, DateTime> _validUntil = new();
    private readonly ConcurrentDictionary<string, byte> _warned = new(StringComparer.Ordinal);

    public async Task<bool> IsValidAsync(ConsoleSession session, CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (session.IssuedAtUtc is not { } issued) return false;
        if (now - issued >= consoleOptions.Value.SessionMaxLifetime || issued - now > ClockSkew) return false;
        if (session.Role is not (ConsoleRoles.Admin or ConsoleRoles.Viewer)) return false;
        if (_validUntil.TryGetValue(session, out var until) && now < until) return true;

        bool valid;
        try
        {
            valid = session.Source switch
            {
                SignInSource.Local => await LocalIsValidAsync(session, ct),
                SignInSource.Domain => await DomainIsValidAsync(session, issued, ct),
                _ => false,
            };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Database down: do not lock everyone out of read-only pages; the next request checks again.
            logger.LogWarning(e, "Could not re-check the Console session of {User}; keeping it for now.", session.UserName);
            return true;
        }

        if (valid)
        {
            if (_validUntil.Count > MaxCacheEntries) _validUntil.Clear();
            _validUntil[session] = now + CacheFor;
        }
        else
        {
            _validUntil.TryRemove(session, out _);
            logger.LogInformation("Console session of {User} ({Source}) ended: no longer valid.", session.UserName, session.Source);
        }
        return valid;
    }

    private async Task<bool> LocalIsValidAsync(ConsoleSession session, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(session.PasswordStamp)) return false;
        var normalized = LocalAccountService.Normalize(session.UserName);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var account = await db.LocalAccounts.AsNoTracking()
            .Where(a => a.NormalizedUserName == normalized)
            .Select(a => new { a.Disabled, a.PasswordHash })
            .FirstOrDefaultAsync(ct);
        return account is { Disabled: false }
               && string.Equals(LocalAccountService.PasswordStamp(account.PasswordHash), session.PasswordStamp, StringComparison.Ordinal);
    }

    private async Task<bool> DomainIsValidAsync(ConsoleSession session, DateTime issuedUtc, CancellationToken ct)
    {
        var o = directoryOptions.Value;
        if (!o.IsConfigured) return false; // domain sign-in was turned off
        if (session.DirectoryObjectGuid is not { } userGuid) return false;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var lastSyncStart = await db.DirectorySyncRuns.AsNoTracking().Where(r => r.Success)
            .MaxAsync(r => (DateTime?)r.StartedAtUtc, ct);
        if (lastSyncStart is null)
        {
            WarnOnce("never-synced", "The directory has never been synced; domain Console sessions are checked only for their lifetime until it is.");
            return true;
        }
        if (lastSyncStart < issuedUtc) return true; // the sign-in's own LDAP check is newer than the synced data

        var user = await db.DirectoryObjects.AsNoTracking().Where(x => x.ObjectGuid == userGuid)
            .Select(x => new { x.Kind, x.Deleted, x.Enabled }).FirstOrDefaultAsync(ct);
        if (user is null || user.Deleted || !user.Enabled || user.Kind != DirectoryObjectKind.User) return false;

        var admin = await ResolveGroupsAsync(db, o.AdminGroups, ct);
        if (await IsMemberAsync(db, userGuid, admin.Guids, ct)) return session.Role == ConsoleRoles.Admin;
        if (session.Role == ConsoleRoles.Admin)
        {
            if (!admin.SomeUnknown) return false;
            WarnOnce("admin-unknown", "An admin group DN is not in the synced directory (outside Directory:BaseDn?); " +
                                      "domain admin sessions cannot be re-checked against it.");
            return true;
        }
        var viewer = await ResolveGroupsAsync(db, o.ViewerGroups, ct);
        if (await IsMemberAsync(db, userGuid, viewer.Guids, ct)) return true;
        if (!viewer.SomeUnknown) return false;
        WarnOnce("viewer-unknown", "A viewer group DN is not in the synced directory (outside Directory:BaseDn?); " +
                                   "domain viewer sessions cannot be re-checked against it.");
        return true;
    }

    /// <summary>
    /// The synced groups for configured names, with the sign-in's rules: a DN is matched exactly; a name must match
    /// exactly one group by sAMAccountName or cn (missing or ambiguous names grant nothing, as at sign-in).
    /// <c>SomeUnknown</c>: a configured DN is not in the synced tables, so membership in it cannot be decided.
    /// </summary>
    internal static async Task<(List<Guid> Guids, bool SomeUnknown)> ResolveGroupsAsync(AppDbContext db, IEnumerable<string> names,
        CancellationToken ct)
    {
        var guids = new List<Guid>();
        var unknown = false;
        foreach (var raw in names)
        {
            var name = raw?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            if (DistinguishedNames.LooksLikeDn(name))
            {
                var cn = DistinguishedNames.FirstRdnValue(name).ToLowerInvariant();
                var want = DistinguishedNames.Normalize(name);
                var candidates = await db.DirectoryObjects.AsNoTracking()
                    .Where(g => g.Kind == DirectoryObjectKind.Group && !g.Deleted && g.Name.ToLower() == cn)
                    .Select(g => new { g.ObjectGuid, g.DistinguishedName }).ToListAsync(ct);
                var match = candidates.Where(c => DistinguishedNames.Normalize(c.DistinguishedName) == want).ToList();
                if (match.Count == 1) guids.Add(match[0].ObjectGuid);
                else unknown = true;
                continue;
            }
            var lower = name.ToLowerInvariant();
            var byName = await db.DirectoryObjects.AsNoTracking()
                .Where(g => g.Kind == DirectoryObjectKind.Group && !g.Deleted
                            && (g.SamAccountNameLower == lower || g.Name.ToLower() == lower))
                .Select(g => g.ObjectGuid).Distinct().ToListAsync(ct);
            if (byName.Count == 1) guids.Add(byName[0]);
        }
        return (guids, unknown);
    }

    private static async Task<bool> IsMemberAsync(AppDbContext db, Guid userGuid, List<Guid> groups, CancellationToken ct)
    {
        if (groups.Count == 0) return false;
        return await db.DirectoryTransitiveMemberships.AsNoTracking().AnyAsync(m => m.MemberGuid == userGuid && groups.Contains(m.GroupGuid), ct)
               || await db.DirectoryMemberships.AsNoTracking().AnyAsync(m => m.MemberGuid == userGuid && groups.Contains(m.GroupGuid), ct);
    }

    private void WarnOnce(string key, string message)
    {
        if (_warned.TryAdd(key, 0)) logger.LogWarning("{Message}", message);
    }
}
