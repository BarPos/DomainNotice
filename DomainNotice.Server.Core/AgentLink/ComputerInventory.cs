using System.Net;
using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Data;
using DomainNotice.Server.Data.Entities;
using DomainNotice.Shared;
using DomainNotice.Shared.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DomainNotice.Server.AgentLink;

/// <summary>Result of applying a Hello or Status to the inventory.</summary>
internal sealed record InventoryUpdate(bool NameChanged, bool SessionsChanged, SessionsChange Change);

/// <summary>
/// Computer inventory and history (plan 6, P6): name, IPs, sessions and their history rows. Callers hold the
/// per-computer lock of <see cref="AgentHub"/>.
/// </summary>
internal sealed class ComputerInventory(IDbContextFactory<AppDbContext> dbFactory, TimeProvider time, ILogger<ComputerInventory> logger)
{
    private const int MaxLocalIps = 64;
    private const int MaxSessions = 256;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>Creates the Computer row (+ current name-history row) for a newly enrolled key.</summary>
    public async Task<Guid> CreateComputerAsync(string fingerprint, Shared.Protocol.Enroll enroll, IPAddress remoteIp, CancellationToken ct)
    {
        var now = Now;
        var name = Clip(enroll.ComputerName, 255);
        var computer = new Computer
        {
            Id = Guid.NewGuid(),
            Name = name,
            DnsName = Clip(enroll.DnsName, 255),
            AgentKeyFingerprint = fingerprint,
            EnrolledAtUtc = now,
            LastSeenAtUtc = now,
            AgentVersion = Clip(enroll.AgentVersion, 32),
            OsVersion = Clip(enroll.OsVersion, 255),
            RemoteIp = remoteIp.ToString(),
            AdLinkState = AdLinkState.None,
        };
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.Computers.Add(computer);
        if (name.Length > 0)
            db.ComputerNameHistory.Add(new ComputerNameHistory { ComputerId = computer.Id, Name = name, FirstSeenUtc = now, LastSeenUtc = now, IsCurrent = true });
        await db.SaveChangesAsync(ct);
        return computer.Id;
    }

    public Task<InventoryUpdate> ApplyHelloAsync(AgentConnection conn, Shared.Protocol.Hello hello, CancellationToken ct) =>
        ApplyAsync(conn, hello.ComputerName, hello.LocalIps, hello.Sessions, hello, ct);

    public Task<InventoryUpdate> ApplyStatusAsync(AgentConnection conn, Shared.Protocol.Status status, CancellationToken ct) =>
        ApplyAsync(conn, status.ComputerName, status.LocalIps, status.Sessions, null, ct);

    private async Task<InventoryUpdate> ApplyAsync(AgentConnection conn, string? reportedName, IReadOnlyList<string>? localIps,
        IReadOnlyList<SessionInfo>? sessions, Shared.Protocol.Hello? hello, CancellationToken ct)
    {
        var now = Now;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // Identity is the connection's computer id (from the key), never the reported name.
        var computer = await db.Computers.SingleAsync(c => c.Id == conn.ComputerId, ct);

        var nameChanged = ApplyName(db, computer, Clip(reportedName, 255), now, await db.ComputerNameHistory
            .Where(h => h.ComputerId == computer.Id && h.IsCurrent).ToListAsync(ct));

        var ips = NormalizeIps(localIps);
        computer.LocalIps = ips;
        computer.LastSeenAtUtc = now;
        computer.Online = true;
        if (hello is not null)
        {
            computer.AgentVersion = Clip(hello.AgentVersion, 32);
            computer.OsVersion = Clip(hello.OsVersion, 255);
            if (!string.IsNullOrWhiteSpace(hello.DnsName)) computer.DnsName = Clip(hello.DnsName, 255);
            computer.RemoteIp = conn.RemoteEndPoint.Address.ToString();
        }

        var ipRows = await db.ComputerIpHistory.Where(h => h.ComputerId == computer.Id && h.IsCurrent).ToListAsync(ct);
        ApplyIps(db, computer.Id, IpAddressKind.Local, ips, ipRows, now);
        if (computer.RemoteIp is { Length: > 0 } remote)
            ApplyIps(db, computer.Id, IpAddressKind.Remote, [remote], ipRows, now);

        var reported = SanitizeSessions(sessions);
        var existing = await db.ComputerSessions.Where(s => s.ComputerId == computer.Id).ToListAsync(ct);
        var diff = DiffSessions(existing, reported);
        await ApplySessionsAsync(db, computer.Id, diff, now, ct);

        await db.SaveChangesAsync(ct);
        conn.Sessions = reported;
        conn.LastSeenPersistedUtc = now;

        var change = new SessionsChange(reported, diff.LoggedOn, diff.LoggedOff.Select(ToInfo).ToList());
        return new InventoryUpdate(nameChanged, diff.Changed, change);
    }

    private static bool ApplyName(AppDbContext db, Computer computer, string name, DateTime now, List<ComputerNameHistory> current)
    {
        if (name.Length == 0)
        {
            foreach (var row in current) row.LastSeenUtc = now;
            return false;
        }
        var open = current.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
        var changed = !string.Equals(computer.Name, name, StringComparison.OrdinalIgnoreCase);
        foreach (var row in current.Where(r => !ReferenceEquals(r, open)))
        {
            row.IsCurrent = false;
            row.LastSeenUtc = now;
        }
        if (open is null)
            db.ComputerNameHistory.Add(new ComputerNameHistory { ComputerId = computer.Id, Name = name, FirstSeenUtc = now, LastSeenUtc = now, IsCurrent = true });
        else
        {
            open.LastSeenUtc = now;
            open.Name = name;
        }
        computer.Name = name;
        return changed;
    }

    private static void ApplyIps(AppDbContext db, Guid computerId, IpAddressKind kind, IReadOnlyList<string> addresses,
        List<ComputerIpHistory> currentRows, DateTime now)
    {
        var rows = currentRows.Where(r => r.Kind == kind).ToList();
        foreach (var row in rows)
        {
            row.LastSeenUtc = now;
            if (!addresses.Contains(row.Address, StringComparer.OrdinalIgnoreCase)) row.IsCurrent = false;
        }
        foreach (var address in addresses)
        {
            if (rows.Any(r => string.Equals(r.Address, address, StringComparison.OrdinalIgnoreCase))) continue;
            db.ComputerIpHistory.Add(new ComputerIpHistory
            {
                ComputerId = computerId, Kind = kind, Address = address, FirstSeenUtc = now, LastSeenUtc = now, IsCurrent = true,
            });
        }
    }

    private async Task ApplySessionsAsync(AppDbContext db, Guid computerId, SessionDiff diff, DateTime now, CancellationToken ct)
    {
        foreach (var gone in diff.LoggedOff)
        {
            db.ComputerSessions.Remove(gone);
            var open = await db.UserLogonHistory
                .Where(h => h.ComputerId == computerId && h.SessionId == gone.SessionId && h.UserKey == gone.UserKey && h.LogoffAtUtc == null)
                .ToListAsync(ct);
            foreach (var h in open) h.LogoffAtUtc = Max(now, h.LogonAtUtc);
        }
        // Removals and inserts may share a key (same session id, other user): flush removals first.
        if (diff.LoggedOff.Count > 0 && diff.LoggedOn.Count > 0) await db.SaveChangesAsync(ct);

        foreach (var (row, info) in diff.Kept)
        {
            row.State = info.State;
            row.LastSeenUtc = now;
            row.UserName = Clip(info.UserName, 512);
        }
        foreach (var info in diff.LoggedOn)
        {
            var userName = Clip(info.UserName, 512);
            var key = UserKeys.Normalize(userName);
            var logon = SaneLogonTime(info.LogonTimeUtc, now);
            db.ComputerSessions.Add(new ComputerSession
            {
                ComputerId = computerId, SessionId = info.SessionId, UserName = userName, UserKey = key,
                State = info.State, LogonAtUtc = logon, LastSeenUtc = now,
            });
            // A Windows logon that outlived an offline period starts its new row where the old one ended.
            var lastClosed = await db.UserLogonHistory
                .Where(h => h.ComputerId == computerId && h.SessionId == info.SessionId && h.UserKey == key && h.LogoffAtUtc != null)
                .OrderByDescending(h => h.LogoffAtUtc).Select(h => h.LogoffAtUtc).FirstOrDefaultAsync(ct);
            var historyLogon = lastClosed is { } closed && closed > logon ? Min(closed, now) : logon;
            db.UserLogonHistory.Add(new UserLogonHistory
            {
                ComputerId = computerId, SessionId = info.SessionId, UserName = userName, UserKey = key, LogonAtUtc = historyLogon,
            });
        }
    }

    /// <summary>
    /// Marks the computer offline if <paramref name="conn"/> is still its registered connection: sessions removed,
    /// open logon history closed with LogoffAtUtc = last seen, Online = false. Returns false when another
    /// connection took over (nothing changed).
    /// </summary>
    public async Task<bool> MarkOfflineAsync(AgentHub hub, AgentConnection conn, CancellationToken ct)
    {
        using var _ = await hub.LockAsync(conn.ComputerId, ct);
        if (!hub.TryRemove(conn)) return false;
        var lastSeen = conn.LastActivityUtc;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var computer = await db.Computers.FirstOrDefaultAsync(c => c.Id == conn.ComputerId, ct);
        if (computer is null) return true; // deleted meanwhile
        computer.Online = false;
        computer.LastSeenAtUtc = lastSeen;
        await CloseSessionsAsync(db, computer.Id, lastSeen, ct);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static async Task CloseSessionsAsync(AppDbContext db, Guid computerId, DateTime lastSeen, CancellationToken ct)
    {
        await db.ComputerSessions.Where(s => s.ComputerId == computerId).ExecuteDeleteAsync(ct);
        var open = await db.UserLogonHistory.Where(h => h.ComputerId == computerId && h.LogoffAtUtc == null).ToListAsync(ct);
        foreach (var h in open) h.LogoffAtUtc = Max(lastSeen, h.LogonAtUtc);
    }

    /// <summary>
    /// Server start after a crash or stop: no agent is connected yet, so every computer is offline, sessions are
    /// removed and open logon rows are closed at the computer's last-seen time.
    /// </summary>
    public async Task RecoverAfterRestartAsync(CancellationToken ct)
    {
        var now = Now;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var online = await db.Computers.Where(c => c.Online).ExecuteUpdateAsync(s => s.SetProperty(c => c.Online, false), ct);
        var sessions = await db.ComputerSessions.ExecuteDeleteAsync(ct);
        var open = await db.UserLogonHistory.Where(h => h.LogoffAtUtc == null)
            .Select(h => new { Row = h, h.Computer!.LastSeenAtUtc }).ToListAsync(ct);
        foreach (var x in open) x.Row.LogoffAtUtc = Max(x.LastSeenAtUtc ?? now, x.Row.LogonAtUtc);
        await db.SaveChangesAsync(ct);
        if (online + sessions + open.Count > 0)
            logger.LogInformation("Agent link start: {Online} computers set offline, {Sessions} sessions removed, {Open} logon rows closed",
                online, sessions, open.Count);
    }

    /// <summary>Writes LastSeenAtUtc for online connections whose activity advanced by at least <paramref name="minAge"/>.</summary>
    public async Task FlushLastSeenAsync(IEnumerable<AgentConnection> connections, TimeSpan minAge, CancellationToken ct)
    {
        foreach (var conn in connections)
        {
            var seen = conn.LastActivityUtc;
            if (seen - conn.LastSeenPersistedUtc < minAge) continue;
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await db.Computers.Where(c => c.Id == conn.ComputerId && c.Online)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastSeenAtUtc, seen), ct);
            conn.LastSeenPersistedUtc = seen;
        }
    }

    // ---------------------------------------------------------------- pure helpers

    internal sealed record SessionDiff(
        List<SessionInfo> LoggedOn,
        List<ComputerSession> LoggedOff,
        List<(ComputerSession Row, SessionInfo Info)> Kept,
        bool StateChanged)
    {
        public bool Changed => LoggedOn.Count > 0 || LoggedOff.Count > 0 || StateChanged;
    }

    /// <summary>
    /// A session is the same logon when its session id and user key match. A different user on the same session id
    /// is a logoff plus a logon. State changes (lock, unlock, disconnect) keep the history row.
    /// </summary>
    internal static SessionDiff DiffSessions(IReadOnlyList<ComputerSession> existing, IReadOnlyList<SessionInfo> reported)
    {
        var loggedOn = new List<SessionInfo>();
        var loggedOff = new List<ComputerSession>();
        var kept = new List<(ComputerSession, SessionInfo)>();
        var stateChanged = false;
        var byId = existing.ToDictionary(s => s.SessionId);
        foreach (var info in reported)
        {
            if (byId.Remove(info.SessionId, out var row))
            {
                if (row.UserKey == UserKeys.Normalize(info.UserName))
                {
                    stateChanged |= row.State != info.State;
                    kept.Add((row, info));
                    continue;
                }
                loggedOff.Add(row);
            }
            loggedOn.Add(info);
        }
        loggedOff.AddRange(byId.Values);
        return new SessionDiff(loggedOn, loggedOff, kept, stateChanged);
    }

    internal static List<SessionInfo> SanitizeSessions(IReadOnlyList<SessionInfo>? sessions)
    {
        var result = new Dictionary<int, SessionInfo>();
        foreach (var s in sessions ?? [])
        {
            if (s is null || string.IsNullOrWhiteSpace(s.UserName)) continue;
            if (result.Count >= MaxSessions && !result.ContainsKey(s.SessionId)) break;
            result[s.SessionId] = s with
            {
                UserName = Clip(s.UserName, 512),
                State = Enum.IsDefined(s.State) ? s.State : SessionState.Active,
            };
        }
        return result.Values.OrderBy(s => s.SessionId).ToList();
    }

    internal static List<string> NormalizeIps(IReadOnlyList<string>? ips)
    {
        var result = new List<string>();
        foreach (var raw in ips ?? [])
        {
            if (result.Count >= MaxLocalIps) break;
            if (!IPAddress.TryParse(raw?.Trim(), out var ip)) continue;
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            var text = ip.ToString();
            if (text.Length <= 64 && !result.Contains(text, StringComparer.OrdinalIgnoreCase)) result.Add(text);
        }
        return result;
    }

    private static SessionInfo ToInfo(ComputerSession s) =>
        new() { SessionId = s.SessionId, UserName = s.UserName, State = s.State, LogonTimeUtc = s.LogonAtUtc };

    private static DateTime SaneLogonTime(DateTime reported, DateTime now)
    {
        if (reported == default) return now;
        var utc = reported.Kind switch
        {
            DateTimeKind.Utc => reported,
            DateTimeKind.Local => reported.ToUniversalTime(),
            _ => DateTime.SpecifyKind(reported, DateTimeKind.Utc),
        };
        return utc > now || utc < now.AddYears(-10) ? now : utc;
    }

    internal static string Clip(string? value, int max)
    {
        var v = (value ?? "").Trim();
        return v.Length <= max ? v : v[..max];
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
