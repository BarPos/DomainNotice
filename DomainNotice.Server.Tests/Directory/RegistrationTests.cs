using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Directory;
using DomainNotice.Server.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DomainNotice.Server.Tests.Directory;

public class RegistrationTests(PostgresFixture pg)
{
    [Fact]
    public async Task Directory_and_accounts_services_resolve_from_core_registration()
    {
        var db = await pg.CreateDatabaseAsync();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = db.ConnectionString,
            // A directory setting that is refused outside Development must not break registration or resolution.
            ["Directory:Hosts:0"] = "dc1.example.local",
            ["Directory:BaseDn"] = "DC=example,DC=local",
            ["Directory:Security"] = "None",
        }).Build();
        var env = new TestHostEnvironment("Testing");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(env);
        services.AddDomainNoticeCore(config, env);
        await using var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var signIn = sp.GetRequiredService<IAdminSignInService>();
        Assert.False(signIn.DomainSignInAvailable); // None outside Development
        Assert.True(signIn.LocalSignInEnabled);
        Assert.False(await signIn.IsFirstRunSetupAllowedAsync(TestContext.Current.CancellationToken)); // directory configured
        Assert.NotNull(sp.GetRequiredService<ILocalAccountService>());
        var admin = sp.GetRequiredService<IDirectoryAdmin>();
        Assert.True(admin.IsConfigured);
        Assert.Contains("Development", (await admin.TestConnectionAsync(TestContext.Current.CancellationToken)).Error);
        Assert.Contains(sp.GetServices<IHostedService>(), s => s is DirectorySyncService);
        Assert.IsType<LdapDomainAuthenticator>(sp.GetRequiredService<IDomainAuthenticator>());
    }
}
