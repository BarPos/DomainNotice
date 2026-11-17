using System.Security.Claims;
using DomainNotice.Console.Auth;
using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Accounts;
using DomainNotice.Server.Data;
using DomainNotice.Server.Data.Entities;
using DomainNotice.Server.Directory;
using DomainNotice.Server.Options;
using DomainNotice.Server.Tests.Directory;
using DomainNotice.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace DomainNotice.Server.Tests.Accounts;

public class SessionValidationTests(PostgresFixture pg)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ConsoleSessionValidator Validator(IDbContextFactory<AppDbContext> db, ManualTimeProvider time,
        DirectoryOptions? directory = null, int maxHours = 12) =>
        new(db, MsOptions.Create(new ConsoleOptions { SessionMaxHours = maxHours }),
            MsOptions.Create(directory ?? DirectoryTestContext.ConfiguredOptions()), time, NullLogger<ConsoleSessionValidator>.Instance);

    private sealed record DomainTree(FakeLdapSource Src, LdapDirectoryEntry Boss, LdapDirectoryEntry Reader, LdapDirectoryEntry Admins,
        LdapDirectoryEntry Viewers);

    /// <summary>boss is in "DomainNotice Admins" through the nested "IT Admins"; reader is in "DomainNotice Viewers".</summary>
    private static DomainTree BuildDirectory()
    {
        var src = new FakeLdapSource();
        src.AddDomain();
        var staff = src.AddOu("Staff", FakeLdapSource.BaseDn);
        var boss = src.AddUser("boss", staff.DistinguishedName);
        var reader = src.AddUser("reader", staff.DistinguishedName);
        var it = src.AddGroup("IT Admins", staff.DistinguishedName, boss);
        var admins = src.AddGroup("DomainNotice Admins", staff.DistinguishedName, it);
        var viewers = src.AddGroup("DomainNotice Viewers", staff.DistinguishedName, reader);
        return new DomainTree(src, boss, reader, admins, viewers);
    }

    private static DirectoryOptions WithViewers(DirectoryOptions o)
    {
        o.ViewerGroups = ["DomainNotice Viewers"];
        return o;
    }

    [Fact]
    public async Task Sessions_end_after_the_absolute_lifetime_even_when_active()
    {
        var h = await AccountsHarness.CreateAsync(pg);
        await h.Accounts.CreateFirstAsync("root", "password-1", Ct);
        var outcome = await h.SignIn.SignInLocalAsync("root", "password-1", null, Ct);
        var session = new ConsoleSession("root", SignInSource.Local, ConsoleRoles.Admin, outcome.IssuedAtUtc, null, outcome.PasswordStamp);
        var v = Validator(h.Ctx.Db, h.Ctx.Time);

        Assert.True(await v.IsValidAsync(session, Ct));
        h.Ctx.Time.Advance(TimeSpan.FromHours(12) - TimeSpan.FromMinutes(1));
        Assert.True(await v.IsValidAsync(session, Ct));
        h.Ctx.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.False(await v.IsValidAsync(session, Ct));

        // Configurable; and a session without a sign-in time (older cookie) is never valid.
        Assert.False(await Validator(h.Ctx.Db, h.Ctx.Time, maxHours: 2).IsValidAsync(
            session with { IssuedAtUtc = h.Ctx.Time.GetUtcNow().UtcDateTime.AddHours(-3) }, Ct));
        Assert.False(await v.IsValidAsync(session with { IssuedAtUtc = null }, Ct));
    }

    [Fact]
    public async Task Local_password_reset_and_disabling_end_sessions()
    {
        var h = await AccountsHarness.CreateAsync(pg, directoryConfigured: true);
        await h.Accounts.CreateAsync("ops", "password-1", "a", Ct);
        var outcome = await h.SignIn.SignInLocalAsync("ops", "password-1", null, Ct);
        var session = new ConsoleSession("ops", SignInSource.Local, ConsoleRoles.Admin, outcome.IssuedAtUtc, null, outcome.PasswordStamp);
        var v = Validator(h.Ctx.Db, h.Ctx.Time);
        Assert.True(await v.IsValidAsync(session, Ct));
        Assert.False(await v.IsValidAsync(session with { PasswordStamp = null }, Ct));
        Assert.False(await v.IsValidAsync(session with { PasswordStamp = "0000" }, Ct));

        var id = await h.IdOf("ops");
        Assert.True((await h.Accounts.ResetPasswordAsync(id, "password-2", "admin", Ct)).Ok);
        h.Ctx.Time.Advance(ConsoleSessionValidator.CacheFor);
        Assert.False(await v.IsValidAsync(session, Ct));

        var again = await h.SignIn.SignInLocalAsync("ops", "password-2", null, Ct);
        var fresh = session with { IssuedAtUtc = again.IssuedAtUtc, PasswordStamp = again.PasswordStamp };
        Assert.True(await v.IsValidAsync(fresh, Ct));
        Assert.True((await h.Accounts.SetDisabledAsync(id, true, "admin", Ct)).Ok);
        h.Ctx.Time.Advance(ConsoleSessionValidator.CacheFor);
        Assert.False(await v.IsValidAsync(fresh, Ct));
    }

    [Fact]
    public async Task Domain_sessions_are_rechecked_against_the_synced_directory()
    {
        var ctx = await DirectoryTestContext.CreateAsync(pg);
        var t = BuildDirectory();
        var options = WithViewers(DirectoryTestContext.ConfiguredOptions());
        var v = Validator(ctx.Db, ctx.Time, options);
        var issued = ctx.Time.GetUtcNow().UtcDateTime;
        var boss = new ConsoleSession(@"EXAMPLE\boss", SignInSource.Domain, ConsoleRoles.Admin, issued, t.Boss.ObjectGuid, null);
        var reader = new ConsoleSession(@"EXAMPLE\reader", SignInSource.Domain, ConsoleRoles.Viewer, issued, t.Reader.ObjectGuid, null);

        // Never synced: fail open (lifetime still applies).
        Assert.True(await v.IsValidAsync(boss, Ct));
        Assert.False(await v.IsValidAsync(boss with { DirectoryObjectGuid = null }, Ct)); // older cookie

        ctx.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.True((await ctx.Engine(t.Src).RunAsync(DirectorySyncKind.Full, "t", Ct)).Success);
        ctx.Time.Advance(ConsoleSessionValidator.CacheFor);
        Assert.True(await v.IsValidAsync(boss, Ct));   // nested membership counts
        Assert.True(await v.IsValidAsync(reader, Ct));

        // Role changed (a viewer session for an admin, and the other way round): sign in again.
        Assert.False(await v.IsValidAsync(boss with { Role = ConsoleRoles.Viewer }, Ct));
        Assert.False(await v.IsValidAsync(reader with { Role = ConsoleRoles.Admin }, Ct));
        // A user that is not in the synced directory.
        Assert.False(await v.IsValidAsync(boss with { DirectoryObjectGuid = Guid.NewGuid() }, Ct));

        // Removed from the nested group: no admin or viewer group any more.
        var it = t.Src.Entries.Single(e => e.Cn == "IT Admins");
        t.Src.Update(it.ObjectGuid, g => FakeLdapSource.Copy(g, members: []));
        ctx.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.True((await ctx.Engine(t.Src).RunAsync(DirectorySyncKind.Incremental, "t", Ct)).Success);
        ctx.Time.Advance(ConsoleSessionValidator.CacheFor);
        Assert.False(await v.IsValidAsync(boss, Ct));

        // Disabled.
        t.Src.Update(t.Reader.ObjectGuid, u => FakeLdapSource.Copy(u, uac: 0x202));
        ctx.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.True((await ctx.Engine(t.Src).RunAsync(DirectorySyncKind.Incremental, "t", Ct)).Success);
        ctx.Time.Advance(ConsoleSessionValidator.CacheFor);
        Assert.False(await v.IsValidAsync(reader, Ct));
    }

    [Fact]
    public async Task Synced_data_older_than_the_sign_in_does_not_end_the_session()
    {
        var ctx = await DirectoryTestContext.CreateAsync(pg);
        var t = BuildDirectory();
        Assert.True((await ctx.Engine(t.Src).RunAsync(DirectorySyncKind.Full, "t", Ct)).Success);
        ctx.Time.Advance(TimeSpan.FromMinutes(1));
        // Signed in after the last sync as a user the sync has not seen yet (e.g. just created or just added to the
        // group): the live check at sign-in is newer, so the session stays until a later sync says otherwise.
        var v = Validator(ctx.Db, ctx.Time);
        var newcomer = new ConsoleSession(@"EXAMPLE\new", SignInSource.Domain, ConsoleRoles.Admin,
            ctx.Time.GetUtcNow().UtcDateTime, Guid.NewGuid(), null);
        Assert.True(await v.IsValidAsync(newcomer, Ct));
        ctx.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.True((await ctx.Engine(t.Src).RunAsync(DirectorySyncKind.Incremental, "t", Ct)).Success);
        ctx.Time.Advance(ConsoleSessionValidator.CacheFor);
        Assert.False(await v.IsValidAsync(newcomer, Ct));
    }

    [Fact]
    public async Task Group_dn_outside_the_synced_directory_is_not_held_against_the_user_but_deletion_is()
    {
        var ctx = await DirectoryTestContext.CreateAsync(pg);
        var t = BuildDirectory();
        var options = DirectoryTestContext.ConfiguredOptions();
        options.AdminGroups = ["CN=DomainNotice Admins,CN=Users,DC=elsewhere,DC=local"];
        var v = Validator(ctx.Db, ctx.Time, options);
        var issued = ctx.Time.GetUtcNow().UtcDateTime;
        ctx.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.True((await ctx.Engine(t.Src).RunAsync(DirectorySyncKind.Full, "t", Ct)).Success);
        var boss = new ConsoleSession(@"EXAMPLE\boss", SignInSource.Domain, ConsoleRoles.Admin, issued, t.Boss.ObjectGuid, null);
        Assert.True(await v.IsValidAsync(boss, Ct));

        t.Src.Entries.RemoveAll(e => e.ObjectGuid == t.Boss.ObjectGuid);
        ctx.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.True((await ctx.Engine(t.Src).RunAsync(DirectorySyncKind.Full, "t", Ct)).Success);
        ctx.Time.Advance(ConsoleSessionValidator.CacheFor);
        Assert.False(await v.IsValidAsync(boss, Ct)); // deleted
    }

    [Fact]
    public async Task Look_alike_group_names_grant_nothing_in_the_recheck_either()
    {
        var ctx = await DirectoryTestContext.CreateAsync(pg);
        var t = BuildDirectory();
        // Someone creates a group whose sAMAccountName is the configured name and puts themselves in it.
        var mallory = t.Src.AddUser("mallory", FakeLdapSource.BaseDn);
        var fake = t.Src.AddGroup("Fake", FakeLdapSource.BaseDn, mallory);
        var i = t.Src.Entries.FindIndex(e => e.ObjectGuid == fake.ObjectGuid);
        t.Src.Entries[i] = new LdapDirectoryEntry
        {
            ObjectGuid = fake.ObjectGuid, DistinguishedName = fake.DistinguishedName, ObjectClasses = fake.ObjectClasses,
            Cn = "Fake", Name = "Fake", SamAccountName = "DomainNotice Admins", Members = fake.Members, UsnChanged = fake.UsnChanged,
        };
        var issued = ctx.Time.GetUtcNow().UtcDateTime;
        ctx.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.True((await ctx.Engine(t.Src).RunAsync(DirectorySyncKind.Full, "t", Ct)).Success);
        var v = Validator(ctx.Db, ctx.Time);
        Assert.False(await v.IsValidAsync(new ConsoleSession(@"EXAMPLE\mallory", SignInSource.Domain, ConsoleRoles.Admin, issued,
            mallory.ObjectGuid, null), Ct));
    }

    [Fact]
    public async Task Database_problems_keep_the_session()
    {
        var time = new ManualTimeProvider(DateTime.UtcNow);
        var v = Validator(new ThrowingFactory(), time);
        var now = time.GetUtcNow().UtcDateTime;
        Assert.True(await v.IsValidAsync(new ConsoleSession("root", SignInSource.Local, ConsoleRoles.Admin, now, null, "x"), Ct));
        Assert.True(await v.IsValidAsync(new ConsoleSession(@"EXAMPLE\boss", SignInSource.Domain, ConsoleRoles.Admin, now, Guid.NewGuid(), null), Ct));
        // The lifetime is checked without the database.
        Assert.False(await v.IsValidAsync(new ConsoleSession("root", SignInSource.Local, ConsoleRoles.Admin, now.AddHours(-13), null, "x"), Ct));
    }

    [Fact]
    public async Task Principal_round_trip_and_old_cookies()
    {
        var issued = new DateTime(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc);
        var guid = Guid.NewGuid();
        var principal = ConsoleAuth.CreatePrincipal(new SignInOutcome(true, @"EXAMPLE\boss", "The Boss", ConsoleRoles.Admin,
            SignInSource.Domain, IssuedAtUtc: issued, DirectoryObjectGuid: guid));
        Assert.Equal(new ConsoleSession(@"EXAMPLE\boss", SignInSource.Domain, ConsoleRoles.Admin, issued, guid, null),
            ConsoleAuth.SessionOf(principal));

        var local = ConsoleAuth.CreatePrincipal(new SignInOutcome(true, "root", "root", ConsoleRoles.Admin, SignInSource.Local,
            IssuedAtUtc: issued, PasswordStamp: "abc"));
        Assert.Equal("abc", ConsoleAuth.SessionOf(local)!.PasswordStamp);

        // A 1.0.4 cookie: no sign-in time, no stamp.
        var old = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "root"), new Claim(ClaimTypes.Role, ConsoleRoles.Admin),
            new Claim(ConsoleAuth.SourceClaim, nameof(SignInSource.Local)),
        ], ConsoleAuth.Scheme, ClaimTypes.Name, ClaimTypes.Role));
        var calls = new CountingValidator();
        Assert.True(await ConsoleAuth.IsStillValidAsync(local, calls, Ct));
        Assert.False(await ConsoleAuth.IsStillValidAsync(new ClaimsPrincipal(new ClaimsIdentity()), calls, Ct));
        Assert.Equal(1, calls.Calls);
        // The old cookie reaches the validator (no IssuedAt) and is refused there.
        var time = new ManualTimeProvider(issued);
        var db = await pg.CreateDatabaseAsync();
        Assert.False(await ConsoleAuth.IsStillValidAsync(old, Validator(db, time), Ct));
    }

    private sealed class CountingValidator : IConsoleSessionValidator
    {
        public int Calls { get; private set; }

        public Task<bool> IsValidAsync(ConsoleSession session, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(true);
        }
    }

    private sealed class ThrowingFactory : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => throw new InvalidOperationException("database down");
    }
}
