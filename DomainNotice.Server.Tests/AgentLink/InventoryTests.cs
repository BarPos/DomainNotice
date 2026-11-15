using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Data.Entities;
using DomainNotice.Server.Tests.Infrastructure;
using DomainNotice.Shared;
using DomainNotice.Shared.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DomainNotice.Server.Tests.AgentLink;

public class InventoryTests(PostgresFixture pg)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Hello_updates_inventory_sessions_and_history()
    {
        var db = await pg.CreateDatabaseAsync();
        var observer = new RecordingObserver();
        await using var server = await AgentLinkTestHelpers.StartAsync(db, observer);
        await using var agent = await server.ConnectAsync();
        var id = (await agent.EnrollAsync(computerName: "PC-1")).ComputerId!.Value;
        var logon = DateTime.UtcNow.AddHours(-1);

        var welcome = await agent.HelloAsync(new Hello
        {
            AgentVersion = AppVersion.Current, ComputerName = "PC-1", DnsName = "pc-1.example.local", OsVersion = "Windows 11",
            LocalIps = ["10.0.0.5", "fe80::1", "not-an-ip", "10.0.0.5"],
            Sessions = [TestAgentClient.Session(2, @"CORP\Alice", SessionState.Locked, logon), TestAgentClient.Session(3, "")],
        });

        Assert.True(welcome.Compatible);
        Assert.Equal(AppVersion.Current, welcome.ServerVersion);
        Assert.Null(welcome.KerberosServicePrincipal);
        await TestAgentServer.WaitUntilAsync(() => observer.Online.Contains(id));
        Assert.True(server.Get<IAgentHub>().IsOnline(id));
        var conn = server.Get<IAgentHub>().Get(id)!;
        Assert.Equal(AppVersion.Current, conn.AgentVersion);
        Assert.Equal(ProtocolInfo.Version, conn.ProtocolVersion);
        Assert.Single(conn.Sessions);
        Assert.NotNull(conn.ChannelBinding);

        await using var ctx = db.CreateContext();
        var c = await ctx.Computers.SingleAsync(Ct);
        Assert.True(c.Online);
        Assert.Equal("pc-1.example.local", c.DnsName);
        Assert.Equal("Windows 11", c.OsVersion);
        Assert.Equal("127.0.0.1", c.RemoteIp);
        Assert.Equal(["10.0.0.5", "fe80::1"], c.LocalIps);
        Assert.NotNull(c.LastSeenAtUtc);

        var session = await ctx.ComputerSessions.SingleAsync(Ct);
        Assert.Equal(@"CORP\Alice", session.UserName);
        Assert.Equal(@"corp\alice", session.UserKey);
        Assert.Equal(SessionState.Locked, session.State);
        Assert.Equal(logon, session.LogonAtUtc, TimeSpan.FromMilliseconds(1));

        var history = await ctx.UserLogonHistory.SingleAsync(Ct);
        Assert.Equal(@"corp\alice", history.UserKey);
        Assert.Null(history.LogoffAtUtc);
        Assert.Equal(2, history.SessionId);

        var ips = await ctx.ComputerIpHistory.Where(h => h.IsCurrent).OrderBy(h => h.Address).ToListAsync(Ct);
        Assert.Equal(["10.0.0.5", "127.0.0.1", "fe80::1"], ips.Select(i => i.Address));
        Assert.Equal(IpAddressKind.Remote, ips.Single(i => i.Address == "127.0.0.1").Kind);
        Assert.Empty(observer.SessionChanges); // not for the initial Hello
    }

    [Fact]
    public async Task Rename_and_status_diff_update_history_and_call_observers()
    {
        var db = await pg.CreateDatabaseAsync();
        var observer = new RecordingObserver();
        await using var server = await AgentLinkTestHelpers.StartAsync(db, observer);
        var (agent, id) = await server.ConnectEnrolledAsync("PC-OLD", [TestAgentClient.Session(1, @"CORP\alice")]);
        await using var _ = agent;

        // Same user locks the screen: state change only, no history row.
        await agent.SendStatusAsync("PC-OLD", [TestAgentClient.Session(1, @"CORP\alice", SessionState.Locked)]);
        await TestAgentServer.WaitUntilAsync(() => observer.SessionChanges.Count == 1);
        var (computer, lockChange) = observer.SessionChanges.Single();
        Assert.Equal(id, computer);
        Assert.Empty(lockChange.LoggedOn);
        Assert.Empty(lockChange.LoggedOff);

        // Rename + bob logs on + alice logs off + new IP.
        await agent.SendStatusAsync("PC-NEW", [TestAgentClient.Session(2, @"CORP\Bob")], ["10.0.0.99"]);
        await TestAgentServer.WaitUntilAsync(() => observer.SessionChanges.Count == 2);
        var change = observer.SessionChanges.Last().Change;
        Assert.Equal(@"CORP\Bob", Assert.Single(change.LoggedOn).UserName);
        Assert.Equal(@"CORP\alice", Assert.Single(change.LoggedOff).UserName);
        Assert.Equal(@"CORP\Bob", Assert.Single(change.Current).UserName);

        // Nothing changed: no callback.
        await agent.SendStatusAsync("PC-NEW", [TestAgentClient.Session(2, @"CORP\Bob")], ["10.0.0.99"]);
        await agent.PingAsync();
        await Task.Delay(200, Ct);
        Assert.Equal(2, observer.SessionChanges.Count);

        await using var ctx = db.CreateContext();
        var c = await ctx.Computers.SingleAsync(Ct);
        Assert.Equal("PC-NEW", c.Name);
        Assert.Equal(["10.0.0.99"], c.LocalIps);
        var names = await ctx.ComputerNameHistory.OrderBy(h => h.FirstSeenUtc).ToListAsync(Ct);
        Assert.Equal(["PC-OLD", "PC-NEW"], names.Select(n => n.Name));
        Assert.False(names[0].IsCurrent);
        Assert.True(names[1].IsCurrent);

        var logons = await ctx.UserLogonHistory.OrderBy(h => h.Id).ToListAsync(Ct);
        Assert.Equal(2, logons.Count);
        Assert.Equal(@"corp\alice", logons[0].UserKey);
        Assert.NotNull(logons[0].LogoffAtUtc);
        Assert.Equal(@"corp\bob", logons[1].UserKey);
        Assert.Null(logons[1].LogoffAtUtc);
        Assert.Equal(@"corp\bob", (await ctx.ComputerSessions.SingleAsync(Ct)).UserKey);

        var ip = await ctx.ComputerIpHistory.Where(h => h.Kind == IpAddressKind.Local).OrderBy(h => h.Id).ToListAsync(Ct);
        Assert.False(ip.Single(i => i.Address == "10.0.0.10").IsCurrent);
        Assert.True(ip.Single(i => i.Address == "10.0.0.99").IsCurrent);
    }

    [Fact]
    public async Task Other_user_on_same_session_id_is_logoff_plus_logon()
    {
        var db = await pg.CreateDatabaseAsync();
        var observer = new RecordingObserver();
        await using var server = await AgentLinkTestHelpers.StartAsync(db, observer);
        var (agent, _) = await server.ConnectEnrolledAsync("PC-1", [TestAgentClient.Session(1, @"CORP\alice")]);
        await using var __ = agent;
        await agent.SendStatusAsync("PC-1", [TestAgentClient.Session(1, @"CORP\bob")]);
        await TestAgentServer.WaitUntilAsync(() => observer.SessionChanges.Count == 1);
        await using var ctx = db.CreateContext();
        Assert.Equal(@"corp\bob", (await ctx.ComputerSessions.SingleAsync(Ct)).UserKey);
        Assert.Equal(1, await ctx.UserLogonHistory.CountAsync(h => h.LogoffAtUtc == null, Ct));
        Assert.Equal(2, await ctx.UserLogonHistory.CountAsync(Ct));
    }

    [Fact]
    public async Task Payload_names_cannot_impersonate_another_computer()
    {
        var db = await pg.CreateDatabaseAsync();
        var handler = new RecordingHandler(Shown.Type);
        await using var server = await AgentLinkTestHelpers.StartAsync(db,
            services: (s, _) => s.AddSingleton<IAgentMessageHandler>(handler));
        var (a, idA) = await server.ConnectEnrolledAsync("PC-A", [TestAgentClient.Session(1, @"CORP\alice")]);
        var (b, idB) = await server.ConnectEnrolledAsync("PC-B");
        await using var _a = a;
        await using var _b = b;

        await b.SendStatusAsync("PC-A", [TestAgentClient.Session(1, @"CORP\mallory")]);
        await b.SendAsync(new Shown { DeliveryId = Guid.NewGuid() }, ct: Ct);
        await TestAgentServer.WaitUntilAsync(() => handler.Received.Count == 1);

        Assert.Equal(idB, handler.Received.Single().Computer);
        await using var ctx = db.CreateContext();
        var computerA = await ctx.Computers.SingleAsync(c => c.Id == idA, Ct);
        Assert.Equal("PC-A", computerA.Name);
        Assert.Equal(@"corp\alice", (await ctx.ComputerSessions.SingleAsync(s => s.ComputerId == idA, Ct)).UserKey);
        // B's own record took the claimed name and session, nothing else.
        Assert.Equal("PC-A", (await ctx.Computers.SingleAsync(c => c.Id == idB, Ct)).Name);
        Assert.Equal(@"corp\mallory", (await ctx.ComputerSessions.SingleAsync(s => s.ComputerId == idB, Ct)).UserKey);
        Assert.Equal(1, await ctx.ComputerNameHistory.CountAsync(h => h.ComputerId == idA, Ct));
    }

    [Fact]
    public async Task Messages_are_dispatched_to_handlers_and_unknown_types_ignored()
    {
        var db = await pg.CreateDatabaseAsync();
        var handler = new RecordingHandler(Shown.Type, Confirm.Type);
        await using var server = await AgentLinkTestHelpers.StartAsync(db,
            services: (s, _) => s.AddSingleton<IAgentMessageHandler>(handler));
        var (agent, id) = await server.ConnectEnrolledAsync();
        await using var _ = agent;

        var unknown = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { v = 1, type = "FutureThing", id = Guid.NewGuid(), payload = new { x = 1 } });
        await agent.Ssl.WriteAsync(Framing.BuildJson(unknown), Ct);
        await agent.SendAsync(new Confirm { DeliveryId = Guid.NewGuid() }, ct: Ct);
        await agent.SendAsync(new UpdateStatus { TaskId = Guid.NewGuid() }, ct: Ct); // no handler registered: ignored
        await agent.SendAsync(new Shown { DeliveryId = Guid.NewGuid() }, ct: Ct);
        await agent.PingAsync();

        await TestAgentServer.WaitUntilAsync(() => handler.Received.Count == 2);
        Assert.IsType<Confirm>(handler.Received.First().Message);
        Assert.IsType<Shown>(handler.Received.Last().Message);
        Assert.True(server.Get<IAgentHub>().IsOnline(id));
    }

    [Fact]
    public async Task Newer_agent_is_refused_with_welcome_incompatible()
    {
        var db = await pg.CreateDatabaseAsync();
        await using var server = await AgentLinkTestHelpers.StartAsync(db);
        await using var agent = await server.ConnectAsync();
        var id = (await agent.EnrollAsync()).ComputerId!.Value;

        var welcome = await agent.HelloAsync(agentVersion: "99.0.0");

        Assert.False(welcome.Compatible);
        Assert.Equal(AppVersion.Current, welcome.ServerVersion);
        Assert.True(await agent.WaitForCloseAsync());
        Assert.False(server.Get<IAgentHub>().IsOnline(id));
        Assert.False((await AgentLinkTestHelpers.GetComputerAsync(db, id)).Online);
    }

    [Fact]
    public async Task Second_connection_replaces_the_first_without_going_offline()
    {
        var db = await pg.CreateDatabaseAsync();
        var observer = new RecordingObserver();
        await using var server = await AgentLinkTestHelpers.StartAsync(db, observer);
        var cert = TestAgentClient.CreateCertificate();
        var (first, id) = await server.ConnectEnrolledAsync("PC-1", [TestAgentClient.Session(1, @"CORP\alice")], certificate: cert);
        await using var _ = first;

        await using var second = await server.ConnectAsync(cert);
        Assert.True((await second.HelloAsync("PC-1", [TestAgentClient.Session(1, @"CORP\alice")])).Compatible);

        Assert.True(await first.WaitForCloseAsync());
        await second.PingAsync();
        await Task.Delay(200, Ct);
        Assert.True(server.Get<IAgentHub>().IsOnline(id));
        Assert.Empty(observer.Offline);
        await using var ctx = db.CreateContext();
        Assert.True((await ctx.Computers.SingleAsync(Ct)).Online);
        Assert.Equal(1, await ctx.UserLogonHistory.CountAsync(Ct));
        Assert.Equal(1, await ctx.ComputerSessions.CountAsync(Ct));
    }

    [Fact]
    public async Task Silence_marks_offline_and_closes_history_at_last_seen()
    {
        var db = await pg.CreateDatabaseAsync();
        var observer = new RecordingObserver();
        await using var server = await AgentLinkTestHelpers.StartAsync(db, observer,
            settings: new Dictionary<string, string?> { ["Agents:OfflineAfterSeconds"] = "1" });
        var (agent, id) = await server.ConnectEnrolledAsync("PC-1", [TestAgentClient.Session(1, @"CORP\alice")]);
        await using var _ = agent;
        await agent.PingAsync();
        var lastFrame = DateTime.UtcNow;

        Assert.True(await agent.WaitForCloseAsync(TimeSpan.FromSeconds(5)));
        await TestAgentServer.WaitUntilAsync(() => observer.Offline.Contains(id));

        Assert.False(server.Get<IAgentHub>().IsOnline(id));
        await using var ctx = db.CreateContext();
        var c = await ctx.Computers.SingleAsync(Ct);
        Assert.False(c.Online);
        Assert.Equal(0, await ctx.ComputerSessions.CountAsync(Ct));
        var h = await ctx.UserLogonHistory.SingleAsync(Ct);
        Assert.NotNull(h.LogoffAtUtc);
        Assert.Equal(c.LastSeenAtUtc, h.LogoffAtUtc);
        Assert.InRange(h.LogoffAtUtc!.Value, lastFrame.AddSeconds(-1), lastFrame.AddSeconds(1));
    }

    [Fact]
    public async Task Pings_keep_the_agent_online()
    {
        var db = await pg.CreateDatabaseAsync();
        await using var server = await AgentLinkTestHelpers.StartAsync(db,
            settings: new Dictionary<string, string?> { ["Agents:OfflineAfterSeconds"] = "1" });
        var (agent, id) = await server.ConnectEnrolledAsync();
        await using var _ = agent;
        for (var i = 0; i < 8; i++)
        {
            await Task.Delay(300, Ct);
            await agent.PingAsync();
        }
        Assert.True(server.Get<IAgentHub>().IsOnline(id));
        Assert.False(agent.IsClosed);
    }

    [Fact]
    public async Task Disconnect_marks_offline_and_reconnect_opens_new_rows()
    {
        var db = await pg.CreateDatabaseAsync();
        var observer = new RecordingObserver();
        await using var server = await AgentLinkTestHelpers.StartAsync(db, observer);
        var cert = TestAgentClient.CreateCertificate();
        var (agent, id) = await server.ConnectEnrolledAsync("PC-1", [TestAgentClient.Session(1, @"CORP\alice")], certificate: cert);
        await agent.DisposeAsync();
        await TestAgentServer.WaitUntilAsync(() => observer.Offline.Contains(id));

        await using var again = await server.ConnectAsync(cert);
        await again.HelloAsync("PC-1", [TestAgentClient.Session(1, @"CORP\alice")]);
        await TestAgentServer.WaitUntilAsync(() => server.Get<IAgentHub>().IsOnline(id));
        await using var ctx = db.CreateContext();
        var rows = await ctx.UserLogonHistory.OrderBy(h => h.Id).ToListAsync(Ct);
        Assert.Equal(2, rows.Count);
        Assert.NotNull(rows[0].LogoffAtUtc);
        Assert.Null(rows[1].LogoffAtUtc);
        Assert.True(rows[1].LogonAtUtc >= rows[0].LogoffAtUtc);
    }

    [Fact]
    public async Task Start_recovers_from_a_crash()
    {
        var db = await pg.CreateDatabaseAsync();
        var id = Guid.NewGuid();
        var lastSeen = DateTime.UtcNow.AddHours(-2);
        await using (var ctx = db.CreateContext())
        {
            ctx.Computers.Add(new Computer
            {
                Id = id, Name = "PC-1", AgentKeyFingerprint = new string('a', 64), EnrolledAtUtc = lastSeen.AddDays(-1),
                Online = true, LastSeenAtUtc = lastSeen,
            });
            ctx.ComputerSessions.Add(new ComputerSession { ComputerId = id, SessionId = 1, UserName = "u", UserKey = "u", LogonAtUtc = lastSeen.AddHours(-1) });
            ctx.UserLogonHistory.Add(new UserLogonHistory { ComputerId = id, SessionId = 1, UserName = "u", UserKey = "u", LogonAtUtc = lastSeen.AddHours(-1) });
            await ctx.SaveChangesAsync(Ct);
        }

        await using var server = await AgentLinkTestHelpers.StartAsync(db);

        await using var check = db.CreateContext();
        Assert.False((await check.Computers.SingleAsync(Ct)).Online);
        Assert.Equal(0, await check.ComputerSessions.CountAsync(Ct));
        Assert.Equal(lastSeen, (await check.UserLogonHistory.SingleAsync(Ct)).LogoffAtUtc!.Value, TimeSpan.FromMilliseconds(1));
    }
}
