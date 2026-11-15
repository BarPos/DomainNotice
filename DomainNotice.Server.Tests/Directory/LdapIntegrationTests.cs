using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Directory;
using DomainNotice.Server.Options;
using DomainNotice.Server.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace DomainNotice.Server.Tests.Directory;

/// <summary>
/// Optional tests against a real LDAP server. Skipped unless DOMAINNOTICE_TEST_LDAP_HOST is set. Works with OpenLDAP
/// for the connection layer (TLS trust, failover, bind, paged search); the sync and sign-in checks need real AD
/// (objectGUID, uSNChanged, the chain rule) and run only with DOMAINNOTICE_TEST_LDAP_AD=1.
/// <code>
/// DOMAINNOTICE_TEST_LDAP_HOST=dc1.example.local[:port]   DOMAINNOTICE_TEST_LDAP_SECURITY=Ldaps|StartTls|None
/// DOMAINNOTICE_TEST_LDAP_BASEDN=dc=example,dc=local      DOMAINNOTICE_TEST_LDAP_CACERT=/path/ca.pem (optional)
/// DOMAINNOTICE_TEST_LDAP_BINDUSER=...  DOMAINNOTICE_TEST_LDAP_BINDPASSWORD=...
/// AD only: DOMAINNOTICE_TEST_LDAP_AD=1  DOMAINNOTICE_TEST_LDAP_ADMINGROUP=...  DOMAINNOTICE_TEST_LDAP_SIGNIN_USER/_PASSWORD
/// </code>
/// libldap reads its TLS settings once per process, so only these tests may open real LDAP connections in a run.
/// </summary>
public class LdapIntegrationTests(PostgresFixture pg)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string? Env(string name) => Environment.GetEnvironmentVariable("DOMAINNOTICE_TEST_LDAP_" + name);
    private const string PasswordVar = "DOMAINNOTICE_TEST_LDAP_BINDPASSWORD";

    private static DirectoryOptions OptionsOrSkip(bool prependDeadHost = false)
    {
        var host = Env("HOST");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(host), "DOMAINNOTICE_TEST_LDAP_HOST not set; no LDAP server to test against.");
        var hosts = new List<string>();
        if (prependDeadHost) hosts.Add("127.0.0.1:1"); // refused at once → must fail over
        hosts.Add(host!);
        return new DirectoryOptions
        {
            Hosts = hosts,
            Security = Enum.Parse<DirectorySecurity>(Env("SECURITY") ?? "Ldaps", ignoreCase: true),
            Port = 636,
            BaseDn = Env("BASEDN") ?? "",
            BindUser = Env("BINDUSER") ?? "",
            BindPasswordSource = "Environment:" + PasswordVar,
            TrustedCertificate = Env("CACERT") ?? "",
            AdminGroups = string.IsNullOrWhiteSpace(Env("ADMINGROUP")) ? [] : [Env("ADMINGROUP")!],
        };
    }

    private static LdapConnector Connector(DirectoryOptions o, ISettingsStore settings) =>
        new(MsOptions.Create(o), new TestHostEnvironment(o.Security == DirectorySecurity.None ? "Development" : "Production"),
            new BindPasswordProvider(MsOptions.Create(o), settings), TimeProvider.System, NullLogger<LdapConnector>.Instance);

    [Fact]
    public async Task Service_bind_fails_over_and_searches()
    {
        var o = OptionsOrSkip(prependDeadHost: true);
        var ctx = await DirectoryTestContext.CreateAsync(pg);
        var connector = Connector(o, ctx.Settings);
        var source = new LdapDirectorySource(connector, MsOptions.Create(o));
        await using (var session = await source.OpenAsync(Ct))
        {
            Assert.Equal(LdapSecurityPolicy.ParseHost(o.Hosts[1], 0).Host, session.Host);
            await session.ReadRootDseAsync(Ct);
            var count = 0;
            await foreach (var _ in session.SearchObjectsAsync(null, Ct)) count++;
            // OpenLDAP has no objectGUID, so nothing is decoded there; AD returns objects.
            if (Env("AD") == "1") Assert.True(count > 0);
        }
        // Pooled: a second lease reuses the connection.
        await using (var lease = await connector.LeaseServiceAsync(Ct)) Assert.NotNull(lease.Connection);
        connector.ClearPool();
    }

    [Fact]
    public async Task Wrong_password_is_invalid_credentials_without_failover()
    {
        var o = OptionsOrSkip();
        var ctx = await DirectoryTestContext.CreateAsync(pg);
        var connector = Connector(o, ctx.Settings);
        var e = await Assert.ThrowsAsync<DirectoryConnectException>(() => connector.BindAsUserAsync(o.BindUser, "definitely-wrong", Ct));
        Assert.True(e.InvalidCredentials, e.Message);
        var empty = await Assert.ThrowsAsync<DirectoryConnectException>(() => connector.BindAsUserAsync(o.BindUser, "", Ct));
        Assert.True(empty.InvalidCredentials); // never an anonymous bind
    }

    [Fact]
    public async Task Untrusted_certificate_is_refused()
    {
        var o = OptionsOrSkip();
        Assert.SkipWhen(Env("UNTRUSTED_CHECK") != "1" || o.Security == DirectorySecurity.None,
            "Set DOMAINNOTICE_TEST_LDAP_UNTRUSTED_CHECK=1 with CACERT pointing at a CA that did NOT sign the server certificate (separate run).");
        var ctx = await DirectoryTestContext.CreateAsync(pg);
        var e = await Assert.ThrowsAsync<DirectoryConnectException>(() => Connector(o, ctx.Settings).LeaseServiceAsync(Ct));
        Assert.False(e.InvalidCredentials);
    }

    [Fact]
    public async Task Ad_full_sync_and_sign_in()
    {
        var o = OptionsOrSkip();
        Assert.SkipWhen(Env("AD") != "1", "DOMAINNOTICE_TEST_LDAP_AD=1 not set (needs Active Directory).");
        var ctx = await DirectoryTestContext.CreateAsync(pg);
        var connector = Connector(o, ctx.Settings);
        var run = await ctx.Engine(new LdapDirectorySource(connector, MsOptions.Create(o))).RunAsync(Data.Entities.DirectorySyncKind.Full, "it", Ct);
        Assert.True(run.Success, run.Error);
        Assert.True(run.ObjectsChanged > 0);

        if (Env("SIGNIN_USER") is { } user && Env("SIGNIN_PASSWORD") is { } password)
        {
            var auth = new LdapDomainAuthenticator(connector, MsOptions.Create(o), new TestHostEnvironment(), NullLogger<LdapDomainAuthenticator>.Instance);
            var result = await auth.AuthenticateAsync(user, password, Ct);
            Assert.Equal(DomainAuthStatus.Success, result.Status);
            Assert.Equal(ConsoleRoles.Admin, result.Role);
        }
    }
}
