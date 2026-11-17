using System.Buffers.Binary;
using System.Net;
using System.Text;
using DomainNotice.Server.Abstractions;
using DomainNotice.Server.AgentLink;
using DomainNotice.Server.Common;
using DomainNotice.Server.Options;
using DomainNotice.Server.Tests.Infrastructure;
using DomainNotice.Shared;
using DomainNotice.Shared.Protocol;
using Microsoft.EntityFrameworkCore;

namespace DomainNotice.Server.Tests.AgentLink;

/// <summary>
/// Resource limits of the agent link: enrolment rate limit and hashing queue, connection caps, read limits,
/// the inbound queue (Status coalescing), per-computer inventory limits, and log clipping.
/// </summary>
public class LinkLimitsTests(PostgresFixture pg)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Dictionary<string, string?> RequirePassword(string password) => new()
    {
        ["Enrolment:RequirePassword"] = "true",
        ["Enrolment:PasswordHash"] = PasswordHasher.Hash(password, 1000),
    };

    // ---------------------------------------------------------------- enrolment

    [Fact]
    public async Task Parallel_wrong_passwords_from_one_ip_get_only_the_allowed_guesses()
    {
        var db = await pg.CreateDatabaseAsync();
        await using var server = await AgentLinkTestHelpers.StartAsync(db, settings: RequirePassword("install-secret"));
        var enrolment = server.Get<Enrolment>();
        var ip = IPAddress.Parse("192.0.2.10");

        var decisions = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Task.Run(() => enrolment.CheckAsync(ip, "wrong", Ct))));

        Assert.True(enrolment.VerificationsStarted <= 5, $"{enrolment.VerificationsStarted} hashes for one IP");
        Assert.Equal(5, decisions.Count(d => d.Outcome == EnrolOutcome.Rejected));
        Assert.Equal(35, decisions.Count(d => d.Outcome == EnrolOutcome.RateLimited));
        // The IP stays limited, even for the right password, and nothing more is hashed.
        Assert.Equal(EnrolOutcome.RateLimited, (await enrolment.CheckAsync(ip, "install-secret", Ct)).Outcome);
        Assert.True(enrolment.VerificationsStarted <= 5);
        // Another IP is not affected.
        Assert.Equal(EnrolOutcome.Accepted, (await enrolment.CheckAsync(IPAddress.Parse("192.0.2.11"), "install-secret", Ct)).Outcome);
    }

    [Fact]
    public async Task Remembered_password_gives_no_extra_guesses_either()
    {
        var db = await pg.CreateDatabaseAsync();
        await using var server = await AgentLinkTestHelpers.StartAsync(db, settings: RequirePassword("install-secret"));
        var enrolment = server.Get<Enrolment>();
        // Warm: one PC enrolled, so the fast (HMAC) comparison is in use.
        Assert.Equal(EnrolOutcome.Accepted, (await enrolment.CheckAsync(IPAddress.Parse("192.0.2.40"), "install-secret", Ct)).Outcome);
        var compared = enrolment.ComparisonsMade;
        var hashed = enrolment.VerificationsStarted;

        var ip = IPAddress.Parse("192.0.2.41");
        var decisions = await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(() => enrolment.CheckAsync(ip, "guess" + i, Ct))));

        Assert.Equal(5, enrolment.ComparisonsMade - compared);
        Assert.Equal(hashed, enrolment.VerificationsStarted); // wrong guesses against a remembered password need no PBKDF2
        Assert.Equal(5, decisions.Count(d => d.Outcome == EnrolOutcome.Rejected));
        Assert.Equal(35, decisions.Count(d => d.Outcome == EnrolOutcome.RateLimited));
    }

    [Fact]
    public async Task Many_correct_enrolments_from_one_ip_are_all_accepted_and_hashed_once()
    {
        var db = await pg.CreateDatabaseAsync();
        var timings = AgentLinkTestHelpers.FastTimings();
        timings.EnrolMaxFailures = 2;
        await using var server = await AgentLinkTestHelpers.StartAsync(db, settings: RequirePassword("install-secret"), timings: timings);
        var enrolment = server.Get<Enrolment>();
        var ip = IPAddress.Parse("192.0.2.20");

        var decisions = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => enrolment.CheckAsync(ip, "install-secret", Ct))));

        Assert.All(decisions, d => Assert.Equal(EnrolOutcome.Accepted, d.Outcome));
        // At most the per-IP slots were hashed before the first success was remembered.
        Assert.True(enrolment.VerificationsStarted <= 2, $"{enrolment.VerificationsStarted} hashes");

        // One wrong attempt counts; correct ones from the same IP keep working.
        Assert.Equal(EnrolOutcome.Rejected, (await enrolment.CheckAsync(ip, "wrong", Ct)).Outcome);
        Assert.Equal(EnrolOutcome.Accepted, (await enrolment.CheckAsync(ip, "install-secret", Ct)).Outcome);

        // A new password is checked with PBKDF2 again (the remembered one belongs to the old hash).
        var before = enrolment.VerificationsStarted;
        await server.Get<IEnrolmentAdmin>().SetPasswordAsync("install-secret-2", "admin", Ct);
        Assert.Equal(EnrolOutcome.Rejected, (await enrolment.CheckAsync(IPAddress.Parse("192.0.2.21"), "install-secret", Ct)).Outcome);
        Assert.Equal(EnrolOutcome.Accepted, (await enrolment.CheckAsync(IPAddress.Parse("192.0.2.21"), "install-secret-2", Ct)).Outcome);
        Assert.Equal(before + 2, enrolment.VerificationsStarted);
    }

    [Fact]
    public async Task Parallel_correct_enrolments_over_tls_from_one_address_all_succeed()
    {
        var db = await pg.CreateDatabaseAsync();
        var timings = AgentLinkTestHelpers.FastTimings();
        timings.EnrolMaxFailures = 2;
        await using var server = await AgentLinkTestHelpers.StartAsync(db, settings: RequirePassword("install-secret"), timings: timings);

        var agents = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => server.ConnectAsync()));
        try
        {
            var results = await Task.WhenAll(agents.Select((a, i) => a.EnrollAsync("install-secret", $"PC-{i}")));
            Assert.All(results, r => Assert.True(r.Accepted, r.Error));
        }
        finally
        {
            foreach (var a in agents) await a.DisposeAsync();
        }
        await using var ctx = db.CreateContext();
        Assert.Equal(20, await ctx.Computers.CountAsync(Ct));
    }

    [Fact]
    public async Task Enrolment_waiting_too_long_for_a_check_is_closed_without_refusal()
    {
        var db = await pg.CreateDatabaseAsync();
        var timings = AgentLinkTestHelpers.FastTimings();
        timings.EnrolMaxParallelHashes = 1;
        timings.EnrolQueueTimeout = TimeSpan.FromMilliseconds(200);
        // A slow hash keeps the only server-wide hashing slot busy.
        await using var server = await AgentLinkTestHelpers.StartAsync(db, timings: timings, settings: new Dictionary<string, string?>
        {
            ["Enrolment:RequirePassword"] = "true",
            ["Enrolment:PasswordHash"] = PasswordHasher.Hash("install-secret", 5_000_000),
        });
        var enrolment = server.Get<Enrolment>();

        var first = Task.Run(() => enrolment.CheckAsync(IPAddress.Parse("192.0.2.30"), "wrong", Ct));
        await TestAgentServer.WaitUntilAsync(() => enrolment.VerificationsStarted == 1);
        var second = await enrolment.CheckAsync(IPAddress.Parse("192.0.2.31"), "wrong-too", Ct);

        Assert.Equal(EnrolOutcome.Busy, second.Outcome);
        Assert.Equal(EnrolOutcome.Rejected, (await first).Outcome);
        Assert.Equal(1, enrolment.VerificationsStarted);
        // Busy is not a failure: that address can still try (and is hashed now that the slot is free).
        Assert.Equal(EnrolOutcome.Rejected, (await enrolment.CheckAsync(IPAddress.Parse("192.0.2.31"), "wrong-too", Ct)).Outcome);
    }

    // ---------------------------------------------------------------- connection caps

    [Fact]
    public async Task Pending_connections_per_ip_are_capped_until_hello()
    {
        var db = await pg.CreateDatabaseAsync();
        await using var server = await AgentLinkTestHelpers.StartAsync(db,
            settings: new Dictionary<string, string?> { ["Agents:MaxPendingConnectionsPerIp"] = "2" });
        var limiter = server.Get<ConnectionLimiter>();

        await using var a = await server.ConnectAsync();
        await using var b = await server.ConnectAsync();
        await TestAgentServer.WaitUntilAsync(() => limiter.PendingFrom(IPAddress.Loopback) == 2);
        await Assert.ThrowsAnyAsync<Exception>(async () => await (await server.ConnectAsync()).PingAsync(TimeSpan.FromSeconds(2)));

        // Once one of them is online it no longer counts against the address.
        Assert.True((await a.EnrollAsync()).Accepted);
        Assert.True((await a.HelloAsync()).Compatible);
        await TestAgentServer.WaitUntilAsync(() => limiter.PendingFrom(IPAddress.Loopback) == 1);
        await using var c = await server.ConnectAsync();
        Assert.True((await c.EnrollAsync(computerName: "PC-2")).Accepted);
    }

    [Fact]
    public async Task Total_connections_are_capped()
    {
        var db = await pg.CreateDatabaseAsync();
        await using var server = await AgentLinkTestHelpers.StartAsync(db,
            settings: new Dictionary<string, string?> { ["Agents:MaxConnections"] = "2" });
        var limiter = server.Get<ConnectionLimiter>();

        var (a, _) = await server.ConnectEnrolledAsync("PC-1");
        await using var _a = a;
        await using (var b = await server.ConnectAsync())
        {
            await TestAgentServer.WaitUntilAsync(() => limiter.Total == 2);
            await Assert.ThrowsAnyAsync<Exception>(async () => await (await server.ConnectAsync()).PingAsync(TimeSpan.FromSeconds(2)));
        }
        // A closed connection frees its place.
        await TestAgentServer.WaitUntilAsync(() => limiter.Total == 1);
        await using var c = await server.ConnectAsync();
        Assert.True((await c.EnrollAsync(computerName: "PC-3")).Accepted);
    }

    // ---------------------------------------------------------------- read limits

    /// <summary>
    /// The largest Hello a legitimate 1.0.0–1.0.4 agent can send that the server still uses in full: the golden
    /// 1.0.0 Hello with 256 sessions (the server keeps no more), non-ASCII user names of maximal length (the encoder
    /// writes them as \uXXXX, 6 bytes per character), 64 long IPv6 addresses and maximal names.
    /// </summary>
    internal static Hello WorstCaseHello()
    {
        var json = FrozenProtocolTests.Golden_1_0_0.Select(r => r.Data).First(d => d.Item1 == "Hello").Item2;
        var golden = (Hello)ProtocolCodec.DecodePayload(ProtocolCodec.DecodeEnvelope(Encoding.UTF8.GetBytes(json)))!;
        var domain = new string('Ż', 15);   // NetBIOS domain: 15 characters
        return golden with
        {
            ComputerName = new string('Ł', 15),
            DnsName = new string('ł', 255),
            OsVersion = new string('Ó', 255),
            LocalIps = Enumerable.Range(0, 64).Select(i => $"fe80::{i:x4}:ffff:ffff:ffff%{i + 10}").ToList(),
            Sessions = Enumerable.Range(0, 256).Select(i => golden.Sessions[0] with
            {
                SessionId = 65536 + i,
                UserName = domain + "\\" + new string('Ś', 20) + i.ToString("D4"), // sAMAccountName: 20 characters (+ margin)
                State = SessionState.Disconnected,
                LogonTimeUtc = new DateTime(2026, 11, 15, 8, 0, 0, DateTimeKind.Utc).AddTicks(1234567),
            }).ToList(),
        };
    }

    [Fact]
    public void Worst_case_legitimate_messages_fit_the_read_limits_with_margin()
    {
        var o = new AgentsOptions();
        var hello = ProtocolCodec.Encode(WorstCaseHello());
        var enroll = ProtocolCodec.Encode(new Enroll
        {
            InstallPassword = new string('ą', 256), ComputerName = new string('Ł', 255), DnsName = new string('ł', 255),
            OsVersion = new string('Ó', 255), AgentVersion = "1.0.4",
        });
        // About 87 KB and 6 KB today.
        Assert.True(hello.Length * 3 < o.MaxInboundMessageBytes, $"worst-case Hello is {hello.Length} bytes");
        Assert.True(enroll.Length * 4 < o.MaxEnrolMessageBytes, $"worst-case Enroll is {enroll.Length} bytes");
        Assert.True(o.MaxEnrolMessageBytes < o.MaxInboundMessageBytes && o.MaxInboundMessageBytes < o.MaxMessageBytes);
    }

    [Fact]
    public async Task Worst_case_hello_is_accepted_over_tls()
    {
        var db = await pg.CreateDatabaseAsync();
        // 256 users on one computer at once is more than the default new-user limit: switch it off here.
        await using var server = await AgentLinkTestHelpers.StartAsync(db,
            settings: new Dictionary<string, string?> { ["Agents:MaxNewSessionUsersPerHour"] = "0" });
        await using var agent = await server.ConnectAsync();
        Assert.True((await agent.EnrollAsync()).Accepted);

        var welcome = await agent.HelloAsync(WorstCaseHello());

        Assert.True(welcome.Compatible);
        await using var ctx = db.CreateContext();
        Assert.Equal(256, await ctx.ComputerSessions.CountAsync(Ct));
    }

    [Fact]
    public async Task Unknown_key_gets_the_small_enrolment_limit()
    {
        var db = await pg.CreateDatabaseAsync();
        await using var server = await AgentLinkTestHelpers.StartAsync(db);
        await using var agent = await server.ConnectAsync();

        // 100 KB is fine for an enrolled agent, not for a key that may only send Enroll.
        await agent.SendAsync(new Enroll { ComputerName = new string('x', 100_000) }, ct: Ct);

        Assert.Equal(RefusedReasons.ProtocolError, (await agent.WaitForRefusedAsync())?.Reason);
        await using var ctx = db.CreateContext();
        Assert.Equal(0, await ctx.Computers.CountAsync(Ct));
    }

    [Fact]
    public async Task Header_announcing_a_large_body_that_never_comes_times_out()
    {
        var db = await pg.CreateDatabaseAsync();
        var timings = AgentLinkTestHelpers.FastTimings();
        timings.PreHelloTimeout = TimeSpan.FromMilliseconds(500);
        await using var server = await AgentLinkTestHelpers.StartAsync(db, timings: timings);
        await using var agent = await server.ConnectAsync();

        var header = new byte[Framing.HeaderSize];
        BinaryPrimitives.WriteUInt32BigEndian(header, 60_000);
        await agent.Ssl.WriteAsync(header, Ct);
        await agent.Ssl.WriteAsync(new byte[100], Ct);
        await agent.Ssl.FlushAsync(Ct);

        Assert.True(await agent.WaitForCloseAsync(TimeSpan.FromSeconds(5)));
    }

    // ---------------------------------------------------------------- inbound queue

    [Fact]
    public async Task Status_burst_is_coalesced_and_the_final_state_is_kept()
    {
        var db = await pg.CreateDatabaseAsync();
        var observer = new RecordingObserver();
        var timings = AgentLinkTestHelpers.FastTimings();
        timings.StatusMinInterval = TimeSpan.FromMilliseconds(500);
        await using var server = await AgentLinkTestHelpers.StartAsync(db, observer, timings: timings);
        var (agent, id) = await server.ConnectEnrolledAsync("PC-1");
        await using var _ = agent;
        await TestAgentServer.WaitUntilAsync(() => observer.Online.Contains(id));

        for (var i = 1; i <= 20; i++)
            await agent.SendStatusAsync("PC-1", [TestAgentClient.Session(i, $@"CORP\user{i % 3}")]);

        await TestAgentServer.WaitUntilAsync(async () =>
        {
            await using var ctx = db.CreateContext();
            return await ctx.ComputerSessions.AnyAsync(s => s.SessionId == 20, Ct);
        });
        await Task.Delay(800, Ct); // nothing else is still pending
        Assert.True(observer.SessionChanges.Count <= 3, $"{observer.SessionChanges.Count} Status messages applied");
        Assert.Equal(20, observer.SessionChanges.Last().Change.Current.Single().SessionId);
        await using var check = db.CreateContext();
        Assert.Equal(20, (await check.ComputerSessions.SingleAsync(Ct)).SessionId);
    }

    [Fact]
    public async Task Inbound_queue_bounds_bytes_and_keeps_only_the_latest_status()
    {
        var queue = new InboundQueue(TimeProvider.System, TimeSpan.Zero);
        var big = Frame(new Confirm { DeliveryId = Guid.NewGuid() }, (int)InboundQueue.MaxBodyBytes / 2 + 1);

        await queue.EnqueueAsync(big, Ct);                   // an empty queue takes any size
        var blocked = queue.EnqueueAsync(big, Ct).AsTask();  // over the byte budget: waits
        await Task.Delay(100, Ct);
        Assert.False(blocked.IsCompleted);
        for (var i = 0; i < 100; i++)
            await queue.EnqueueAsync(Frame(new Status { ComputerName = "S" + i }, 100), Ct); // never blocks, coalesced

        await using var items = queue.ReadAllAsync(Ct).GetAsyncEnumerator(Ct);
        Assert.True(await items.MoveNextAsync());
        Assert.IsType<Confirm>(items.Current.Frame.Message);
        queue.Done(items.Current);
        await blocked.WaitAsync(TimeSpan.FromSeconds(5), Ct);  // budget freed
        Assert.True(await items.MoveNextAsync());
        Assert.Equal("S99", Assert.IsType<Status>(items.Current.Frame.Message).ComputerName);
        queue.Done(items.Current);
        Assert.True(await items.MoveNextAsync());
        Assert.IsType<Confirm>(items.Current.Frame.Message);
        queue.Done(items.Current);
        queue.Complete();
        Assert.False(await items.MoveNextAsync());
        Assert.Equal(0, queue.QueuedBytes);
    }

    [Fact]
    public async Task Status_waiting_for_its_interval_does_not_hold_up_other_messages()
    {
        var queue = new InboundQueue(TimeProvider.System, TimeSpan.FromSeconds(1));
        await using var items = queue.ReadAllAsync(Ct).GetAsyncEnumerator(Ct);
        await queue.EnqueueAsync(Frame(new Status { ComputerName = "S1" }, 100), Ct);
        Assert.True(await items.MoveNextAsync());
        Assert.Equal("S1", Assert.IsType<Status>(items.Current.Frame.Message).ComputerName);
        queue.Done(items.Current);

        var started = DateTime.UtcNow;
        await queue.EnqueueAsync(Frame(new Status { ComputerName = "S2" }, 100), Ct);
        await queue.EnqueueAsync(Frame(new Confirm { DeliveryId = Guid.NewGuid() }, 100), Ct);
        Assert.True(await items.MoveNextAsync());
        Assert.IsType<Confirm>(items.Current.Frame.Message);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromMilliseconds(800), "Confirm waited for the Status interval");
        queue.Done(items.Current);

        await queue.EnqueueAsync(Frame(new Status { ComputerName = "S3" }, 100), Ct); // replaces S2 while it waits
        Assert.True(await items.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.Equal("S3", Assert.IsType<Status>(items.Current.Frame.Message).ComputerName);
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(900));
        queue.Done(items.Current);
    }

    private static IncomingFrame Frame<T>(T message, int bodyLength) where T : IAgentMessage
    {
        var frame = IncomingFrame.FromEnvelope(ProtocolCodec.Wrap(message));
        frame.BodyLength = bodyLength;
        return frame;
    }

    // ---------------------------------------------------------------- inventory limits

    [Fact]
    public async Task New_session_users_per_computer_are_limited_per_hour()
    {
        var db = await pg.CreateDatabaseAsync();
        var observer = new RecordingObserver();
        await using var server = await AgentLinkTestHelpers.StartAsync(db, observer,
            settings: new Dictionary<string, string?> { ["Agents:MaxNewSessionUsersPerHour"] = "3" });
        var (agent, id) = await server.ConnectEnrolledAsync("PC-1", [TestAgentClient.Session(1, @"CORP\u1"), TestAgentClient.Session(2, @"CORP\u2")]);

        await agent.SendStatusAsync("PC-1", Enumerable.Range(1, 6).Select(i => TestAgentClient.Session(i, $@"CORP\u{i}")));
        await TestAgentServer.WaitUntilAsync(() => observer.SessionChanges.Any(c => c.Change.Current.Count == 3));

        await using var ctx = db.CreateContext();
        var users = await ctx.ComputerSessions.Where(s => s.ComputerId == id).OrderBy(s => s.SessionId).Select(s => s.UserName).ToListAsync(Ct);
        Assert.Equal([@"CORP\u1", @"CORP\u2", @"CORP\u3"], users);
        Assert.Equal(3, server.Get<IAgentHub>().Get(id)!.Sessions.Count); // ignored users get no announcements either
        Assert.Equal(3, await ctx.UserLogonHistory.CountAsync(h => h.ComputerId == id, Ct));

        // Users seen before stay welcome after a reconnect (logon history), new ones are still limited.
        await agent.DisposeAsync();
        await TestAgentServer.WaitUntilAsync(() => !server.Get<IAgentHub>().IsOnline(id));
        await using var again = await server.ConnectAsync(agent.Certificate);
        await again.HelloAsync("PC-1", [TestAgentClient.Session(3, @"CORP\u3"), TestAgentClient.Session(7, @"CORP\u7")]);
        await TestAgentServer.WaitUntilAsync(() => server.Get<IAgentHub>().Get(id) is { } c && c.Sessions.Count == 1);
        Assert.Equal(@"CORP\u3", server.Get<IAgentHub>().Get(id)!.Sessions.Single().UserName);
    }

    [Fact]
    public async Task Name_changes_per_computer_are_limited_per_hour()
    {
        var db = await pg.CreateDatabaseAsync();
        var observer = new RecordingObserver();
        await using var server = await AgentLinkTestHelpers.StartAsync(db, observer,
            settings: new Dictionary<string, string?> { ["Agents:MaxNameChangesPerHour"] = "2" });
        var (agent, id) = await server.ConnectEnrolledAsync("PC-1");
        await using var _ = agent;

        async Task<string> NameAsync()
        {
            await using var c = db.CreateContext();
            return (await c.Computers.SingleAsync(x => x.Id == id, Ct)).Name;
        }
        foreach (var name in new[] { "PC-A", "PC-B" })
        {
            await agent.SendStatusAsync(name);
            await TestAgentServer.WaitUntilAsync(async () => await NameAsync() == name);
        }
        await agent.SendStatusAsync("PC-C");
        await Task.Delay(300, Ct);
        await agent.SendStatusAsync("PC-D", [TestAgentClient.Session(1, @"CORP\alice")]);
        await TestAgentServer.WaitUntilAsync(() => observer.SessionChanges.Count >= 1); // the last Status was applied

        await using var ctx = db.CreateContext();
        Assert.Equal("PC-B", (await ctx.Computers.SingleAsync(c => c.Id == id, Ct)).Name);
        Assert.Equal(3, await ctx.ComputerNameHistory.CountAsync(h => h.ComputerId == id, Ct)); // PC-1, PC-A, PC-B
    }

    [Fact]
    public void User_names_with_control_characters_are_dropped()
    {
        var sessions = ComputerInventory.SanitizeSessions([TestAgentClient.Session(1, "CORP\\a\nb"), TestAgentClient.Session(2, @"CORP\ok")]);
        Assert.Equal(@"CORP\ok", Assert.Single(sessions).UserName);
    }

    // ---------------------------------------------------------------- logs

    [Fact]
    public void Agent_text_in_logs_is_clipped_and_has_no_control_characters()
    {
        Assert.Equal("", LogText.Clip(null));
        Assert.Equal("Hello", LogText.Clip("Hello"));
        var clipped = LogText.Clip(new string('x', 5000) + "\n");
        Assert.Equal(LogText.DefaultMax + 1, clipped.Length);
        Assert.EndsWith("…", clipped);
        Assert.Equal("a?b?c", LogText.Clip("a\nb\u001bc"));
        Assert.Equal("ab", LogText.Clip("ab😀", 3)[..2]);
        Assert.DoesNotContain('\uD83D', LogText.Clip("ab😀", 3));
    }
}
