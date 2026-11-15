using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Accounts;
using DomainNotice.Server.Common;
using DomainNotice.Server.Directory;
using DomainNotice.Server.Options;
using DomainNotice.Server.Tests.Directory;
using DomainNotice.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace DomainNotice.Server.Tests.Accounts;

internal sealed class FakeDomainAuthenticator : IDomainAuthenticator
{
    public bool IsAvailable { get; set; } = true;
    public Func<string, string, DomainAuthResult> Handler { get; set; } =
        (_, _) => new DomainAuthResult(DomainAuthStatus.InvalidCredentials);
    public int Calls { get; private set; }

    public Task<DomainAuthResult> AuthenticateAsync(string userName, string password, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(Handler(userName, password));
    }
}

internal sealed class AccountsHarness
{
    public required DirectoryTestContext Ctx { get; init; }
    public required LocalAccountService Accounts { get; init; }
    public required AdminSignInService SignIn { get; init; }
    public required FakeDomainAuthenticator Domain { get; init; }

    public static async Task<AccountsHarness> CreateAsync(PostgresFixture pg, bool directoryConfigured = false, bool localEnabled = true)
    {
        var ctx = await DirectoryTestContext.CreateAsync(pg);
        var dir = directoryConfigured ? DirectoryTestContext.ConfiguredOptions() : new DirectoryOptions();
        var gate = new FirstRunGate(MsOptions.Create(dir), ctx.Db);
        var domain = new FakeDomainAuthenticator { IsAvailable = directoryConfigured };
        return new AccountsHarness
        {
            Ctx = ctx,
            Domain = domain,
            Accounts = new LocalAccountService(ctx.Db, gate, ctx.Audit, ctx.Time),
            SignIn = new AdminSignInService(ctx.Db, domain, gate, new SignInRateLimiter(ctx.Time),
                MsOptions.Create(new LocalAccountsOptions { Enabled = localEnabled }), ctx.Audit, ctx.Time,
                NullLogger<AdminSignInService>.Instance),
        };
    }

    public async Task<Guid> IdOf(string name)
    {
        await using var db = Ctx.Db.CreateContext();
        return (await db.LocalAccounts.SingleAsync(a => a.NormalizedUserName == name.ToLowerInvariant())).Id;
    }
}

public class LocalAccountTests(PostgresFixture pg)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_hashes_password_and_audits()
    {
        var h = await AccountsHarness.CreateAsync(pg, directoryConfigured: true);
        Assert.False(await h.Accounts.AnyAsync(Ct));
        var r = await h.Accounts.CreateAsync("  Admin2 ", "correct horse", "alice", Ct);
        Assert.True(r.Ok, r.Error);
        Assert.True(await h.Accounts.AnyAsync(Ct));

        await using var db = h.Ctx.Db.CreateContext();
        var a = await db.LocalAccounts.SingleAsync(Ct);
        Assert.Equal("Admin2", a.UserName);
        Assert.Equal("admin2", a.NormalizedUserName);
        Assert.Equal("alice", a.CreatedBy);
        Assert.StartsWith("pbkdf2-sha256$", a.PasswordHash);
        Assert.True(PasswordHasher.Verify("correct horse", a.PasswordHash));
        Assert.True(await db.AuditLog.AnyAsync(x => x.Action == AuditActions.LocalAccountCreated && x.Actor == "alice", Ct));
    }

    [Fact]
    public async Task Duplicate_names_are_refused_case_insensitively()
    {
        var h = await AccountsHarness.CreateAsync(pg, directoryConfigured: true);
        Assert.True((await h.Accounts.CreateAsync("Backup", "password-1", "a", Ct)).Ok);
        var dup = await h.Accounts.CreateAsync(" BACKUP ", "password-2", "a", Ct);
        Assert.False(dup.Ok);
        Assert.Contains("already exists", dup.Error);
    }

    [Theory]
    [InlineData("u", "short7!", "at least 8")]
    [InlineData("", "longenough", "user name")]
    [InlineData("   ", "longenough", "user name")]
    [InlineData("a\nb", "longenough", "invalid")]
    public async Task Validation(string user, string password, string expected)
    {
        var h = await AccountsHarness.CreateAsync(pg, directoryConfigured: true);
        var r = await h.Accounts.CreateAsync(user, password, "a", Ct);
        Assert.False(r.Ok);
        Assert.Contains(expected, r.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True((await h.Accounts.CreateAsync("eight", "12345678", "a", Ct)).Ok); // exactly 8 is fine
    }

    [Fact]
    public async Task Reset_password()
    {
        var h = await AccountsHarness.CreateAsync(pg, directoryConfigured: true);
        await h.Accounts.CreateAsync("ops", "old-password", "a", Ct);
        var id = await h.IdOf("ops");
        Assert.False((await h.Accounts.ResetPasswordAsync(id, "short", "a", Ct)).Ok);
        Assert.False((await h.Accounts.ResetPasswordAsync(Guid.NewGuid(), "new-password", "a", Ct)).Ok);
        Assert.True((await h.Accounts.ResetPasswordAsync(id, "new-password", "admin", Ct)).Ok);
        Assert.False((await h.SignIn.SignInLocalAsync("ops", "old-password", null, Ct)).Succeeded);
        Assert.True((await h.SignIn.SignInLocalAsync("ops", "new-password", null, Ct)).Succeeded);
        await using var db = h.Ctx.Db.CreateContext();
        Assert.True(await db.AuditLog.AnyAsync(x => x.Action == AuditActions.LocalAccountPasswordReset && x.Actor == "admin", Ct));
    }

    [Fact]
    public async Task Disable_and_enable()
    {
        var h = await AccountsHarness.CreateAsync(pg, directoryConfigured: true);
        await h.Accounts.CreateAsync("ops", "password-1", "a", Ct);
        var id = await h.IdOf("ops");
        Assert.True((await h.Accounts.SetDisabledAsync(id, true, "admin", Ct)).Ok);
        var refused = await h.SignIn.SignInLocalAsync("ops", "password-1", null, Ct);
        Assert.False(refused.Succeeded);
        Assert.Equal(AdminSignInService.GenericFailure, refused.Error);
        Assert.True((await h.Accounts.SetDisabledAsync(id, false, "admin", Ct)).Ok);
        Assert.True((await h.SignIn.SignInLocalAsync("ops", "password-1", null, Ct)).Succeeded);
        await using var db = h.Ctx.Db.CreateContext();
        Assert.True(await db.AuditLog.AnyAsync(x => x.Action == AuditActions.LocalAccountDisabled, Ct));
        Assert.True(await db.AuditLog.AnyAsync(x => x.Action == AuditActions.LocalAccountEnabled, Ct));
    }

    [Fact]
    public async Task Last_enabled_account_cannot_be_disabled_without_a_directory()
    {
        var h = await AccountsHarness.CreateAsync(pg, directoryConfigured: false);
        Assert.True((await h.Accounts.CreateFirstAsync("first", "password-1", Ct)).Ok);
        var id = await h.IdOf("first");
        var r = await h.Accounts.SetDisabledAsync(id, true, "first", Ct);
        Assert.False(r.Ok);
        Assert.Contains("lock", r.Error);
        Assert.True((await h.Accounts.CreateAsync("second", "password-2", "first", Ct)).Ok);
        Assert.True((await h.Accounts.SetDisabledAsync(id, true, "first", Ct)).Ok);
    }

    [Fact]
    public async Task First_run_setup_only_once_and_only_without_directory()
    {
        var h = await AccountsHarness.CreateAsync(pg, directoryConfigured: false);
        Assert.True(await h.SignIn.IsFirstRunSetupAllowedAsync(Ct));
        Assert.False((await h.Accounts.CreateFirstAsync("root", "short", Ct)).Ok);
        Assert.True(await h.SignIn.IsFirstRunSetupAllowedAsync(Ct));
        Assert.True((await h.Accounts.CreateFirstAsync("root", "password-1", Ct)).Ok);
        Assert.False(await h.SignIn.IsFirstRunSetupAllowedAsync(Ct));
        var again = await h.Accounts.CreateFirstAsync("other", "password-2", Ct);
        Assert.False(again.Ok);

        var withDir = await AccountsHarness.CreateAsync(pg, directoryConfigured: true);
        Assert.False(await withDir.SignIn.IsFirstRunSetupAllowedAsync(Ct));
        Assert.False((await withDir.Accounts.CreateFirstAsync("root", "password-1", Ct)).Ok);
        Assert.False(await withDir.Accounts.AnyAsync(Ct));
    }

    [Fact]
    public async Task First_run_setup_is_atomic_under_concurrency()
    {
        var h = await AccountsHarness.CreateAsync(pg, directoryConfigured: false);
        var results = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(i => Task.Run(() => h.Accounts.CreateFirstAsync($"admin{i}", "password-123", Ct), Ct)));
        Assert.Equal(1, results.Count(r => r.Ok));
        await using var db = h.Ctx.Db.CreateContext();
        Assert.Equal(1, await db.LocalAccounts.CountAsync(Ct));
    }
}

public class SignInTests(PostgresFixture pg)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Local_sign_in_success_updates_last_sign_in_and_audits()
    {
        var h = await AccountsHarness.CreateAsync(pg);
        await h.Accounts.CreateFirstAsync("Root", "password-1", Ct);
        var outcome = await h.SignIn.SignInLocalAsync(" root ", "password-1", "10.0.0.5", Ct);
        Assert.True(outcome.Succeeded);
        Assert.Equal("Root", outcome.UserName);
        Assert.Equal(ConsoleRoles.Admin, outcome.Role);
        Assert.Equal(SignInSource.Local, outcome.Source);

        await using var db = h.Ctx.Db.CreateContext();
        Assert.Equal(h.Ctx.Time.GetUtcNow().UtcDateTime, (await db.LocalAccounts.SingleAsync(Ct)).LastSignInAtUtc);
        Assert.True(await db.AuditLog.AnyAsync(a => a.Action == AuditActions.SignIn && a.Actor == "Root", Ct));
    }

    [Fact]
    public async Task Failures_are_audited_with_generic_message()
    {
        var h = await AccountsHarness.CreateAsync(pg);
        await h.Accounts.CreateFirstAsync("root", "password-1", Ct);
        var wrong = await h.SignIn.SignInLocalAsync("root", "nope-nope", "10.0.0.5", Ct);
        var unknown = await h.SignIn.SignInLocalAsync("ghost", "nope-nope", "10.0.0.5", Ct);
        Assert.Equal(wrong.Error, unknown.Error); // does not reveal which names exist
        await using var db = h.Ctx.Db.CreateContext();
        var failures = await db.AuditLog.Where(a => a.Action == AuditActions.SignInFailed).ToListAsync(Ct);
        Assert.Equal(2, failures.Count);
        Assert.Contains(failures, f => f.Actor == "ghost" && f.Details!.Contains("unknown user") && f.Details.Contains("10.0.0.5"));
    }

    [Fact]
    public async Task Rate_limited_per_account_then_released_after_window()
    {
        var h = await AccountsHarness.CreateAsync(pg);
        await h.Accounts.CreateFirstAsync("root", "password-1", Ct);
        for (var i = 0; i < SignInRateLimiter.MaxFailuresPerAccount; i++)
            Assert.False((await h.SignIn.SignInLocalAsync("root", "wrong-" + i, $"10.0.0.{i}", Ct)).Succeeded);

        // Correct password from a fresh IP is still refused: the account is over its limit.
        var blocked = await h.SignIn.SignInLocalAsync("ROOT", "password-1", "10.9.9.9", Ct);
        Assert.False(blocked.Succeeded);
        Assert.Equal(AdminSignInService.TooManyAttempts, blocked.Error);

        h.Ctx.Time.Advance(SignInRateLimiter.Window);
        Assert.True((await h.SignIn.SignInLocalAsync("root", "password-1", "10.9.9.9", Ct)).Succeeded);
    }

    [Fact]
    public async Task Success_clears_account_failures()
    {
        var h = await AccountsHarness.CreateAsync(pg);
        await h.Accounts.CreateFirstAsync("root", "password-1", Ct);
        for (var i = 0; i < SignInRateLimiter.MaxFailuresPerAccount - 1; i++)
            await h.SignIn.SignInLocalAsync("root", "wrong", null, Ct);
        Assert.True((await h.SignIn.SignInLocalAsync("root", "password-1", null, Ct)).Succeeded);
        for (var i = 0; i < SignInRateLimiter.MaxFailuresPerAccount - 1; i++)
            await h.SignIn.SignInLocalAsync("root", "wrong", null, Ct);
        Assert.True((await h.SignIn.SignInLocalAsync("root", "password-1", null, Ct)).Succeeded);
    }

    [Fact]
    public async Task Rate_limited_per_ip_across_accounts()
    {
        var h = await AccountsHarness.CreateAsync(pg);
        await h.Accounts.CreateFirstAsync("root", "password-1", Ct);
        for (var i = 0; i < SignInRateLimiter.MaxFailuresPerIp; i++)
            await h.SignIn.SignInLocalAsync($"guess{i}", "wrong", "203.0.113.7", Ct);

        var fromBadIp = await h.SignIn.SignInLocalAsync("root", "password-1", "203.0.113.7", Ct);
        Assert.False(fromBadIp.Succeeded);
        Assert.Equal(AdminSignInService.TooManyAttempts, fromBadIp.Error);
        Assert.True((await h.SignIn.SignInLocalAsync("root", "password-1", "198.51.100.1", Ct)).Succeeded);

        // Domain sign-ins from that IP are limited too, without asking the DC.
        h.Domain.IsAvailable = true;
        var domain = await h.SignIn.SignInDomainAsync("jdoe", "pw", "203.0.113.7", Ct);
        Assert.Equal(AdminSignInService.TooManyAttempts, domain.Error);
        Assert.Equal(0, h.Domain.Calls);
    }

    [Fact]
    public async Task Domain_roles_pass_through_and_failures_are_limited_per_account()
    {
        var h = await AccountsHarness.CreateAsync(pg, directoryConfigured: true);
        h.Domain.Handler = (user, pw) => (BindNames.SamAccountName(user).ToLowerInvariant(), pw) switch
        {
            ("boss", "pw") => new DomainAuthResult(DomainAuthStatus.Success, @"EXAMPLE\boss", "The Boss", ConsoleRoles.Admin),
            ("reader", "pw") => new DomainAuthResult(DomainAuthStatus.Success, @"EXAMPLE\reader", null, ConsoleRoles.Viewer),
            ("outsider", "pw") => new DomainAuthResult(DomainAuthStatus.NotAuthorized, @"EXAMPLE\outsider"),
            _ => new DomainAuthResult(DomainAuthStatus.InvalidCredentials),
        };
        Assert.True(h.SignIn.DomainSignInAvailable);

        var boss = await h.SignIn.SignInDomainAsync("boss", "pw", "10.0.0.1", Ct);
        Assert.True(boss.Succeeded);
        Assert.Equal(ConsoleRoles.Admin, boss.Role);
        Assert.Equal(@"EXAMPLE\boss", boss.UserName);
        Assert.Equal("The Boss", boss.DisplayName);
        Assert.Equal(SignInSource.Domain, boss.Source);

        var reader = await h.SignIn.SignInDomainAsync("reader@example.local", "pw", "10.0.0.1", Ct);
        Assert.Equal(ConsoleRoles.Viewer, reader.Role);
        Assert.Equal(@"EXAMPLE\reader", reader.DisplayName);

        var outsider = await h.SignIn.SignInDomainAsync("outsider", "pw", "10.0.0.1", Ct);
        Assert.False(outsider.Succeeded);
        Assert.Null(outsider.Role);

        // Different spellings of one account share the limit.
        string[] spellings = ["jdoe", @"EXAMPLE\jdoe", "JDoe@example.local", "jdoe", "JDOE"];
        foreach (var s in spellings) Assert.False((await h.SignIn.SignInDomainAsync(s, "bad", $"10.1.0.{s.Length}", Ct)).Succeeded);
        var calls = h.Domain.Calls;
        Assert.Equal(AdminSignInService.TooManyAttempts, (await h.SignIn.SignInDomainAsync("jdoe", "bad", "10.2.0.1", Ct)).Error);
        Assert.Equal(calls, h.Domain.Calls);

        await using var db = h.Ctx.Db.CreateContext();
        Assert.True(await db.AuditLog.AnyAsync(a => a.Action == AuditActions.SignIn && a.Actor == @"EXAMPLE\boss", Ct));
        Assert.True(await db.AuditLog.AnyAsync(a => a.Action == AuditActions.SignInFailed && a.Details!.Contains("not in an admin"), Ct));
    }

    [Fact]
    public async Task Directory_outage_is_not_counted_against_the_user()
    {
        var h = await AccountsHarness.CreateAsync(pg, directoryConfigured: true);
        h.Domain.Handler = (_, _) => new DomainAuthResult(DomainAuthStatus.Unavailable, Error: "no DC");
        for (var i = 0; i < SignInRateLimiter.MaxFailuresPerAccount + 2; i++)
        {
            var r = await h.SignIn.SignInDomainAsync("jdoe", "pw", "10.0.0.1", Ct);
            Assert.False(r.Succeeded);
            Assert.Contains("could not be reached", r.Error);
        }
        h.Domain.Handler = (_, _) => new DomainAuthResult(DomainAuthStatus.Success, "jdoe", null, ConsoleRoles.Admin);
        Assert.True((await h.SignIn.SignInDomainAsync("jdoe", "pw", "10.0.0.1", Ct)).Succeeded);
    }

    [Fact]
    public async Task Sign_in_availability_flags()
    {
        var noDir = await AccountsHarness.CreateAsync(pg, directoryConfigured: false, localEnabled: false);
        Assert.False(noDir.SignIn.DomainSignInAvailable);
        Assert.True(noDir.SignIn.LocalSignInEnabled); // the only way in without a directory
        Assert.False((await noDir.SignIn.SignInDomainAsync("x", "y", null, Ct)).Succeeded);

        var localOff = await AccountsHarness.CreateAsync(pg, directoryConfigured: true, localEnabled: false);
        Assert.False(localOff.SignIn.LocalSignInEnabled);
        await localOff.Accounts.CreateAsync("backup", "password-1", "a", Ct);
        var r = await localOff.SignIn.SignInLocalAsync("backup", "password-1", null, Ct);
        Assert.False(r.Succeeded);
        Assert.Contains("turned off", r.Error);
    }

    [Fact]
    public void Rate_limiter_window_slides()
    {
        var time = new ManualTimeProvider(DateTime.UtcNow);
        var limiter = new SignInRateLimiter(time);
        var key = SignInRateLimiter.AccountKey("local", "x");
        for (var i = 0; i < SignInRateLimiter.MaxFailuresPerAccount; i++)
        {
            limiter.RecordFailure(key, null);
            time.Advance(TimeSpan.FromMinutes(1));
        }
        Assert.True(limiter.IsBlocked(key, null));
        // Failures at t0..t4, now t5. The oldest leaves the 15-minute window at t15.
        time.Advance(TimeSpan.FromMinutes(9));
        Assert.True(limiter.IsBlocked(key, null));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.False(limiter.IsBlocked(key, null));
        Assert.False(limiter.IsBlocked(SignInRateLimiter.AccountKey("local", "other"), "1.2.3.4"));
    }
}
