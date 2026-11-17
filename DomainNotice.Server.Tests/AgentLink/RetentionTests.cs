using DomainNotice.Server.AgentLink;
using DomainNotice.Server.Data.Entities;
using DomainNotice.Server.Options;
using DomainNotice.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DomainNotice.Server.Tests.AgentLink;

public class RetentionTests(PostgresFixture pg)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(30)]
    [InlineData(7)]
    public async Task Deletes_only_closed_history_older_than_retention(int days)
    {
        var db = await pg.CreateDatabaseAsync();
        var now = DateTime.UtcNow;
        var old = now.AddDays(-days - 1);
        var recent = now.AddDays(-days + 1);
        var id = Guid.NewGuid();
        await using (var ctx = db.CreateContext())
        {
            ctx.Computers.Add(new Computer { Id = id, Name = "PC", AgentKeyFingerprint = new string('b', 64), EnrolledAtUtc = old.AddDays(-5), LastSeenAtUtc = old });
            ctx.ComputerSessions.Add(new ComputerSession { ComputerId = id, SessionId = 1, UserName = "u", UserKey = "u", LogonAtUtc = old, LastSeenUtc = old });
            ctx.ComputerNameHistory.AddRange(
                new ComputerNameHistory { ComputerId = id, Name = "old-closed", FirstSeenUtc = old, LastSeenUtc = old, IsCurrent = false },
                new ComputerNameHistory { ComputerId = id, Name = "recent-closed", FirstSeenUtc = old, LastSeenUtc = recent, IsCurrent = false },
                new ComputerNameHistory { ComputerId = id, Name = "old-open", FirstSeenUtc = old, LastSeenUtc = old, IsCurrent = true });
            ctx.UserLogonHistory.AddRange(
                new UserLogonHistory { ComputerId = id, UserName = "old-closed", UserKey = "a", LogonAtUtc = old.AddHours(-1), LogoffAtUtc = old },
                new UserLogonHistory { ComputerId = id, UserName = "recent-closed", UserKey = "b", LogonAtUtc = old, LogoffAtUtc = recent },
                new UserLogonHistory { ComputerId = id, UserName = "old-open", UserKey = "c", LogonAtUtc = old });
            ctx.ComputerIpHistory.AddRange(
                new ComputerIpHistory { ComputerId = id, Kind = IpAddressKind.Local, Address = "10.0.0.1", FirstSeenUtc = old, LastSeenUtc = old, IsCurrent = false },
                new ComputerIpHistory { ComputerId = id, Kind = IpAddressKind.Local, Address = "10.0.0.2", FirstSeenUtc = old, LastSeenUtc = recent, IsCurrent = false },
                new ComputerIpHistory { ComputerId = id, Kind = IpAddressKind.Remote, Address = "10.0.0.3", FirstSeenUtc = old, LastSeenUtc = old, IsCurrent = true });
            await ctx.SaveChangesAsync(Ct);
        }

        var job = new HistoryRetention(db, Microsoft.Extensions.Options.Options.Create(new HistoryOptions { RetentionDays = days }),
            new AgentLinkTimings(), TimeProvider.System, NullLogger<HistoryRetention>.Instance);
        var (names, logons, ips) = await job.PruneAsync(Ct);

        Assert.Equal((1, 1, 1), (names, logons, ips));
        await using var check = db.CreateContext();
        Assert.Equal(["old-open", "recent-closed"], (await check.ComputerNameHistory.Select(h => h.Name).ToListAsync(Ct)).Order());
        Assert.Equal(["old-open", "recent-closed"], (await check.UserLogonHistory.Select(h => h.UserName).ToListAsync(Ct)).Order());
        Assert.Equal(["10.0.0.2", "10.0.0.3"], (await check.ComputerIpHistory.Select(h => h.Address).ToListAsync(Ct)).Order());
        Assert.Equal(1, await check.Computers.CountAsync(Ct));
        Assert.Equal(1, await check.ComputerSessions.CountAsync(Ct));
    }

    [Fact]
    public async Task Zero_retention_keeps_everything()
    {
        var db = await pg.CreateDatabaseAsync();
        var job = new HistoryRetention(db, Microsoft.Extensions.Options.Options.Create(new HistoryOptions { RetentionDays = 0 }),
            new AgentLinkTimings(), TimeProvider.System, NullLogger<HistoryRetention>.Instance);
        Assert.Equal((0, 0, 0), await job.PruneAsync(Ct));
    }

    [Fact]
    public async Task Old_failed_sign_ins_are_pruned_and_other_audit_entries_kept()
    {
        var db = await pg.CreateDatabaseAsync();
        var now = DateTime.UtcNow;
        await using (var ctx = db.CreateContext())
        {
            ctx.AuditLog.AddRange(
                new AuditLogEntry { AtUtc = now.AddDays(-91), Actor = "x", Action = Abstractions.AuditActions.SignInFailed },
                new AuditLogEntry { AtUtc = now.AddDays(-89), Actor = "y", Action = Abstractions.AuditActions.SignInFailed },
                new AuditLogEntry { AtUtc = now.AddDays(-400), Actor = "z", Action = Abstractions.AuditActions.SignIn });
            await ctx.SaveChangesAsync(Ct);
        }
        var job = new HistoryRetention(db, Microsoft.Extensions.Options.Options.Create(new HistoryOptions()),
            new AgentLinkTimings(), TimeProvider.System, NullLogger<HistoryRetention>.Instance);
        Assert.Equal(1, await job.PruneFailedSignInsAsync(Ct));
        await using var check = db.CreateContext();
        Assert.Equal(["y", "z"], (await check.AuditLog.Select(a => a.Actor).ToListAsync(Ct)).Order());

        var keepAll = new HistoryRetention(db, Microsoft.Extensions.Options.Options.Create(new HistoryOptions { FailedSignInRetentionDays = 0 }),
            new AgentLinkTimings(), TimeProvider.System, NullLogger<HistoryRetention>.Instance);
        Assert.Equal(0, await keepAll.PruneFailedSignInsAsync(Ct));
    }
}
