using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Common;
using DomainNotice.Server.Tests.Infrastructure;
using DomainNotice.Shared.Protocol;
using Microsoft.EntityFrameworkCore;

namespace DomainNotice.Server.Tests.AgentLink;

public class EnrolmentTests(PostgresFixture pg)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Dictionary<string, string?> RequirePassword(string password) => new()
    {
        ["Enrolment:RequirePassword"] = "true",
        ["Enrolment:PasswordHash"] = PasswordHasher.Hash(password, 1000),
    };

    [Fact]
    public async Task Right_password_enrols_and_creates_computer_with_name_history()
    {
        var db = await pg.CreateDatabaseAsync();
        await using var server = await AgentLinkTestHelpers.StartAsync(db, settings: RequirePassword("install-secret"));
        await using var agent = await server.ConnectAsync();

        var result = await agent.EnrollAsync("install-secret", "PC-ENROL");

        Assert.True(result.Accepted);
        Assert.NotNull(result.ComputerId);
        await using var ctx = db.CreateContext();
        var computer = await ctx.Computers.SingleAsync(Ct);
        Assert.Equal(result.ComputerId, computer.Id);
        Assert.Equal(agent.Fingerprint, computer.AgentKeyFingerprint);
        Assert.Equal("PC-ENROL", computer.Name);
        var history = await ctx.ComputerNameHistory.SingleAsync(Ct);
        Assert.True(history.IsCurrent);
        Assert.Equal("PC-ENROL", history.Name);

        // The same connection continues with Hello.
        var welcome = await agent.HelloAsync("PC-ENROL");
        Assert.True(welcome.Compatible);
    }

    [Fact]
    public async Task Wrong_password_is_refused_and_closes()
    {
        var db = await pg.CreateDatabaseAsync();
        await using var server = await AgentLinkTestHelpers.StartAsync(db, settings: RequirePassword("install-secret"));
        await using var agent = await server.ConnectAsync();

        var result = await agent.EnrollAsync("wrong");

        Assert.False(result.Accepted);
        Assert.NotNull(result.Error);
        Assert.True(await agent.WaitForCloseAsync());
        await using var ctx = db.CreateContext();
        Assert.Equal(0, await ctx.Computers.CountAsync(Ct));
    }

    [Fact]
    public async Task Failed_attempts_are_rate_limited_per_ip()
    {
        var db = await pg.CreateDatabaseAsync();
        var timings = AgentLinkTestHelpers.FastTimings();
        timings.EnrolMaxFailures = 2;
        await using var server = await AgentLinkTestHelpers.StartAsync(db, settings: RequirePassword("install-secret"), timings: timings);
        for (var i = 0; i < 2; i++)
        {
            await using var bad = await server.ConnectAsync();
            Assert.False((await bad.EnrollAsync("wrong")).Accepted);
        }

        await using var agent = await server.ConnectAsync();
        await agent.SendAsync(new Enroll { InstallPassword = "install-secret", ComputerName = "PC-1", AgentVersion = "1.0.0" }, ct: Ct);
        var refused = await agent.WaitForRefusedAsync();

        Assert.Equal(RefusedReasons.RateLimited, refused?.Reason);
        Assert.Empty(agent.AllReceived.Where(f => f.Message is EnrollResult));
    }

    [Fact]
    public async Task Requirement_off_accepts_without_password()
    {
        var db = await pg.CreateDatabaseAsync();
        await using var server = await AgentLinkTestHelpers.StartAsync(db);
        await using var agent = await server.ConnectAsync();
        Assert.True((await agent.EnrollAsync(null)).Accepted);
    }

    [Fact]
    public async Task Required_without_any_hash_refuses()
    {
        var db = await pg.CreateDatabaseAsync();
        await using var server = await AgentLinkTestHelpers.StartAsync(db, settings: new Dictionary<string, string?> { ["Enrolment:RequirePassword"] = "true" });
        await using var agent = await server.ConnectAsync();
        var result = await agent.EnrollAsync("anything");
        Assert.False(result.Accepted);
    }

    [Fact]
    public async Task Console_settings_override_config_and_are_audited()
    {
        var db = await pg.CreateDatabaseAsync();
        await using var server = await AgentLinkTestHelpers.StartAsync(db); // config: not required
        var admin = server.Get<IEnrolmentAdmin>();

        Assert.False((await admin.SetPasswordAsync("short", "admin", Ct)).Ok);
        Assert.True((await admin.SetPasswordAsync("db-password-1", "admin", Ct)).Ok);
        Assert.True((await admin.SetRequirePasswordAsync(true, "admin", Ct)).Ok);
        var state = await admin.GetStateAsync(Ct);
        Assert.Equal(new EnrolmentState(true, true, true, true), state);

        await using (var bad = await server.ConnectAsync())
            Assert.False((await bad.EnrollAsync(null)).Accepted);
        await using (var good = await server.ConnectAsync())
            Assert.True((await good.EnrollAsync("db-password-1")).Accepted);

        // Rotation does not affect already enrolled computers (the key is the identity).
        Assert.True((await admin.SetPasswordAsync("db-password-2", "admin", Ct)).Ok);

        await admin.SetAwaitingApprovalPolicyAsync(AwaitingApprovalPolicy.Nothing, "admin", Ct);
        Assert.Equal(AwaitingApprovalPolicy.Nothing, await admin.GetAwaitingApprovalPolicyAsync(Ct));

        await using var ctx = db.CreateContext();
        var actions = await ctx.AuditLog.Select(a => a.Action).ToListAsync(Ct);
        Assert.Equal(2, actions.Count(a => a == AuditActions.InstallPasswordChanged));
        Assert.Contains(AuditActions.InstallPasswordRequirementChanged, actions);
        Assert.Contains(AuditActions.SettingChanged, actions);
        // Only the hash is stored.
        var stored = await ctx.Settings.SingleAsync(s => s.Key == SettingKeys.EnrolmentPasswordHash, Ct);
        Assert.True(PasswordHasher.LooksLikeHash(stored.Value));
        Assert.DoesNotContain("db-password", stored.Value);
    }

    [Fact]
    public async Task Unknown_key_may_only_send_enroll()
    {
        var db = await pg.CreateDatabaseAsync();
        await using var server = await AgentLinkTestHelpers.StartAsync(db);

        await using (var agent = await server.ConnectAsync())
        {
            await agent.SendAsync(TestAgentClient.CreateHello(), ct: Ct);
            Assert.Equal(RefusedReasons.UnknownKey, (await agent.WaitForRefusedAsync())?.Reason);
            Assert.DoesNotContain(agent.AllReceived, f => f.Message is Welcome);
        }
        await using (var agent = await server.ConnectAsync())
        {
            await agent.SendAsync(new Ping(), ct: Ct);
            Assert.Equal(RefusedReasons.UnknownKey, (await agent.WaitForRefusedAsync())?.Reason);
        }
        await using (var agent = await server.ConnectAsync())
        {
            await agent.SendAsync(new Confirm { DeliveryId = Guid.NewGuid() }, ct: Ct);
            Assert.Equal(RefusedReasons.UnknownKey, (await agent.WaitForRefusedAsync())?.Reason);
        }
        await using var check = db.CreateContext();
        Assert.Equal(0, await check.Computers.CountAsync(Ct));
    }

    [Fact]
    public async Task Enrolled_key_sending_enroll_again_gets_its_id_and_can_reconnect()
    {
        var db = await pg.CreateDatabaseAsync();
        await using var server = await AgentLinkTestHelpers.StartAsync(db);
        var cert = TestAgentClient.CreateCertificate();
        Guid id;
        await using (var agent = await server.ConnectAsync(cert))
            id = (await agent.EnrollAsync()).ComputerId!.Value;

        await using (var again = await server.ConnectAsync(cert))
        {
            var result = await again.EnrollAsync();
            Assert.True(result.Accepted);
            Assert.Equal(id, result.ComputerId);
            Assert.True((await again.HelloAsync()).Compatible);
        }
        await using var ctx = db.CreateContext();
        Assert.Equal(1, await ctx.Computers.CountAsync(Ct));
    }

    [Fact]
    public async Task Silent_connection_before_hello_is_closed()
    {
        var db = await pg.CreateDatabaseAsync();
        var timings = AgentLinkTestHelpers.FastTimings();
        timings.PreHelloTimeout = TimeSpan.FromMilliseconds(500);
        await using var server = await AgentLinkTestHelpers.StartAsync(db, timings: timings);
        await using var agent = await server.ConnectAsync();
        Assert.True(await agent.WaitForCloseAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Oversized_frame_closes_the_connection()
    {
        var db = await pg.CreateDatabaseAsync();
        await using var server = await AgentLinkTestHelpers.StartAsync(db,
            settings: new Dictionary<string, string?> { ["Agents:MaxMessageBytes"] = "4096" });
        await using var agent = await server.ConnectAsync();
        await agent.SendAsync(new Enroll { ComputerName = new string('x', 10_000) }, ct: Ct);
        Assert.True(await agent.WaitForCloseAsync());
        await using var ctx = db.CreateContext();
        Assert.Equal(0, await ctx.Computers.CountAsync(Ct));
    }
}
