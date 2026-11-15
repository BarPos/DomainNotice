using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Data.Entities;
using DomainNotice.Server.Directory;
using DomainNotice.Server.Options;
using DomainNotice.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace DomainNotice.Server.Tests.Directory;

public class DirectoryAdminTests(PostgresFixture pg)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (DirectoryAdmin Admin, DirectorySyncCoordinator Coordinator) Create(DirectoryTestContext ctx, DirectoryOptions o, ILdapSource src)
    {
        var opts = MsOptions.Create(o);
        var passwords = new BindPasswordProvider(opts, ctx.Settings);
        var connector = new LdapConnector(opts, new TestHostEnvironment(), passwords, ctx.Time, NullLogger<LdapConnector>.Instance);
        var coordinator = new DirectorySyncCoordinator();
        return (new DirectoryAdmin(opts, coordinator, ctx.Db, src, connector, passwords, ctx.Settings, ctx.Audit), coordinator);
    }

    [Fact]
    public async Task Bind_password_is_stored_encrypted_and_audited()
    {
        var ctx = await DirectoryTestContext.CreateAsync(pg);
        var (admin, _) = Create(ctx, DirectoryTestContext.ConfiguredOptions(), new FakeLdapSource());

        Assert.False((await admin.SetBindPasswordAsync("", "admin", Ct)).Ok);
        Assert.True((await admin.SetBindPasswordAsync("S3cret-bind!", "admin", Ct)).Ok);
        Assert.Equal("S3cret-bind!", await ctx.Settings.GetSecretAsync(SettingKeys.DirectoryBindPassword, Ct));

        await using var db = ctx.Db.CreateContext();
        var row = await db.Settings.SingleAsync(s => s.Key == SettingKeys.DirectoryBindPassword, Ct);
        Assert.True(row.IsSecret);
        Assert.DoesNotContain("S3cret", row.Value);
        Assert.True(await db.AuditLog.AnyAsync(a => a.Action == AuditActions.DirectoryBindPasswordChanged && a.Actor == "admin", Ct));
    }

    [Fact]
    public async Task Bind_password_is_refused_when_source_is_environment()
    {
        var ctx = await DirectoryTestContext.CreateAsync(pg);
        var o = DirectoryTestContext.ConfiguredOptions();
        o.BindPasswordSource = "Environment:DOMAINNOTICE_BIND_PASSWORD";
        var (admin, _) = Create(ctx, o, new FakeLdapSource());
        var r = await admin.SetBindPasswordAsync("x-password", "admin", Ct);
        Assert.False(r.Ok);
        Assert.Contains("Database", r.Error);
        Assert.Null(await ctx.Settings.GetSecretAsync(SettingKeys.DirectoryBindPassword, Ct));
    }

    [Fact]
    public async Task Bind_password_provider_reads_environment_or_database()
    {
        var ctx = await DirectoryTestContext.CreateAsync(pg);
        var o = DirectoryTestContext.ConfiguredOptions();
        var varName = "DN_TEST_BIND_" + Guid.NewGuid().ToString("N");
        o.BindPasswordSource = "Environment:" + varName;
        var p = new BindPasswordProvider(MsOptions.Create(o), ctx.Settings);
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => p.GetAsync(Ct));
        Assert.Contains(varName, e.Message);
        Environment.SetEnvironmentVariable(varName, "from-env");
        Assert.Equal("from-env", await p.GetAsync(Ct));
        Environment.SetEnvironmentVariable(varName, null);

        o.BindPasswordSource = "Database";
        await Assert.ThrowsAsync<InvalidOperationException>(() => p.GetAsync(Ct));
        await ctx.Settings.SetSecretAsync(SettingKeys.DirectoryBindPassword, "from-db", "t", Ct);
        Assert.Equal("from-db", await p.GetAsync(Ct));
    }

    [Fact]
    public async Task Test_connection_and_status()
    {
        var ctx = await DirectoryTestContext.CreateAsync(pg);
        var src = new FakeLdapSource();
        src.AddDomain();
        var (admin, coordinator) = Create(ctx, DirectoryTestContext.ConfiguredOptions(), src);
        Assert.True(admin.IsConfigured);
        Assert.True((await admin.TestConnectionAsync(Ct)).Ok);

        var empty = await admin.GetStatusAsync(Ct);
        Assert.True(empty.Configured);
        Assert.Null(empty.LastSuccessUtc);
        Assert.Equal(0, empty.ObjectCount);

        var engine = ctx.Engine(src);
        await engine.RunAsync(DirectorySyncKind.Full, null, Ct);
        var ok = await admin.GetStatusAsync(Ct);
        Assert.NotNull(ok.LastSuccessUtc);
        Assert.Null(ok.LastError);
        Assert.Equal(1, ok.ObjectCount);

        src.OpenError = new DirectoryConnectException("DC down");
        Assert.Equal("DC down", (await admin.TestConnectionAsync(Ct)).Error);
        await engine.RunAsync(DirectorySyncKind.Incremental, null, Ct);
        var failed = await admin.GetStatusAsync(Ct);
        Assert.Equal("DC down", failed.LastError);
        Assert.Equal(ok.LastSuccessUtc, failed.LastSuccessUtc);

        await admin.RequestSyncAsync(true, "admin", Ct);
        Assert.True(coordinator.Requests.TryRead(out var req));
        Assert.True(req.Full);
        Assert.Equal("admin", req.RequestedBy);
    }

    [Fact]
    public async Task Not_configured_is_inert()
    {
        var ctx = await DirectoryTestContext.CreateAsync(pg);
        var (admin, coordinator) = Create(ctx, new DirectoryOptions(), new FakeLdapSource());
        Assert.False(admin.IsConfigured);
        Assert.False((await admin.TestConnectionAsync(Ct)).Ok);
        await admin.RequestSyncAsync(true, "admin", Ct);
        Assert.False(coordinator.Requests.TryRead(out _));
        Assert.False((await admin.GetStatusAsync(Ct)).Configured);
    }

    [Fact]
    public async Task Hosted_service_syncs_at_start_and_on_request()
    {
        var ctx = await DirectoryTestContext.CreateAsync(pg);
        var src = new FakeLdapSource();
        src.AddDomain();
        src.AddUser("u1", FakeLdapSource.BaseDn);
        var o = DirectoryTestContext.ConfiguredOptions();
        var coordinator = new DirectorySyncCoordinator();
        var service = new DirectorySyncService(ctx.Engine(src), coordinator, ctx.Db, MsOptions.Create(o), new TestHostEnvironment(),
            ctx.Time, NullLogger<DirectorySyncService>.Instance);
        await service.StartAsync(Ct);
        try
        {
            var runs = await WaitForRunsAsync(ctx, 1);
            Assert.Equal(DirectorySyncKind.Full, runs[0].Kind); // never synced → full at start

            coordinator.Request(false, "admin");
            runs = await WaitForRunsAsync(ctx, 2);
            Assert.Equal(DirectorySyncKind.Incremental, runs[1].Kind);
            Assert.Equal("admin", runs[1].RequestedBy);
            Assert.All(runs, r => Assert.True(r.Success, r.Error));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Hosted_service_does_nothing_when_not_configured_or_refused()
    {
        var ctx = await DirectoryTestContext.CreateAsync(pg);
        var src = new FakeLdapSource();
        src.AddDomain();

        var notConfigured = new DirectorySyncService(ctx.Engine(src), new DirectorySyncCoordinator(), ctx.Db,
            MsOptions.Create(new DirectoryOptions()), new TestHostEnvironment(), ctx.Time, NullLogger<DirectorySyncService>.Instance);
        await notConfigured.StartAsync(Ct);
        await (notConfigured.ExecuteTask ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // Plain LDAP outside Development: refused, recorded as the status error, no scheduled runs, host not stopped.
        var o = DirectoryTestContext.ConfiguredOptions();
        o.Security = DirectorySecurity.None;
        var coordinator = new DirectorySyncCoordinator();
        var refused = new DirectorySyncService(ctx.Engine(src), coordinator, ctx.Db, MsOptions.Create(o), new TestHostEnvironment(),
            ctx.Time, NullLogger<DirectorySyncService>.Instance);
        await refused.StartAsync(Ct);
        for (var i = 0; i < 100 && coordinator.ConfigError is null; i++) await Task.Delay(20, Ct);
        Assert.Contains("Development", coordinator.ConfigError);
        await refused.StopAsync(CancellationToken.None);

        await using var db = ctx.Db.CreateContext();
        Assert.Equal(0, await db.DirectorySyncRuns.CountAsync(Ct));
    }

    private static async Task<List<DirectorySyncRun>> WaitForRunsAsync(DirectoryTestContext ctx, int count)
    {
        for (var i = 0; i < 250; i++)
        {
            await using var db = ctx.Db.CreateContext();
            var runs = await db.DirectorySyncRuns.Where(r => r.FinishedAtUtc != null).OrderBy(r => r.Id).ToListAsync(Ct);
            if (runs.Count >= count) return runs;
            await Task.Delay(40, Ct);
        }
        throw new TimeoutException($"Expected {count} finished sync runs.");
    }
}
