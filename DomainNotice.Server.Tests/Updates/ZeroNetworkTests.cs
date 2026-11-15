using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Common;
using DomainNotice.Server.Data;
using DomainNotice.Server.Options;
using DomainNotice.Server.Tests.Infrastructure;
using DomainNotice.Server.Updates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DomainNotice.Server.Tests.Updates;

/// <summary>Plan 11.4: a blank Updates:GitHubRepository means no network calls at all, through the real registrations.</summary>
public class ZeroNetworkTests(PostgresFixture pg)
{
    [Fact]
    public async Task Blank_repository_makes_zero_network_calls_anywhere()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = await pg.CreateDatabaseAsync();
        var dataDir = Path.Combine(Path.GetTempPath(), "dn-zero-" + Guid.NewGuid().ToString("N"));
        var handler = new RecordingHandler { FailOnRequest = true };
        var factory = new RecordingHttpClientFactory(handler);
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Updates:GitHubRepository"] = "",
                ["Agents:AutoUpdate"] = "true",
                ["Paths:Data"] = dataDir,
            }).Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(TimeProvider.System);
            services.Configure<UpdatesOptions>(config.GetSection(UpdatesOptions.Section));
            services.Configure<AgentsOptions>(config.GetSection(AgentsOptions.Section));
            services.Configure<PathsOptions>(config.GetSection(PathsOptions.Section));
            services.AddSingleton<IDbContextFactory<AppDbContext>>(db);
            services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            services.AddSingleton<ISettingsStore, SettingsStore>();
            services.AddSingleton<IAuditLog, AuditLog>();
            services.AddSingleton<IAgentHub, FakeHub>();
            services.AddSingleton<IServerInstallation>(new FakeInstallation { SelfUpdateSupported = true });
            services.AddUpdates(config);
            services.AddSingleton<IHttpClientFactory>(factory);
            await using var sp = services.BuildServiceProvider();

            var server = sp.GetRequiredService<IServerUpdateService>();
            var status = await server.CheckNowAsync("alice", ct);
            Assert.False(status.Configured);
            Assert.False(status.UpdateAvailable);
            var update = await server.UpdateNowAsync("alice", ct);
            Assert.False(update.Ok);

            var cache = sp.GetRequiredService<AgentPackageCache>();
            Assert.False((await cache.EnsureCurrentAsync(ct)).Ok);

            var background = sp.GetRequiredService<UpdatesBackgroundService>();
            await background.RunStartupAsync(ct);
            await background.TickAsync(ct);
            await background.AutoUpdateAgentsAsync(ct);

            // The hosted services are registered through AddUpdates.
            Assert.Contains(sp.GetServices<IHostedService>(), h => h is UpdatesBackgroundService);
            Assert.Contains(sp.GetServices<IHostedService>(), h => h is AgentUpdateTimeoutService);
            Assert.Contains(sp.GetServices<IAgentMessageHandler>(), h => h is AgentUpdateCoordinator);
            Assert.Contains(sp.GetServices<IAgentLifecycleObserver>(), h => h is AgentUpdateCoordinator);

            Assert.Empty(handler.Requests);
            Assert.Equal(0, factory.Created);
        }
        finally
        {
            try { System.IO.Directory.Delete(dataDir, true); } catch (IOException) { }
        }
    }
}
