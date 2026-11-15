using DomainNotice.Server.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DomainNotice.Server.Updates;

public static class UpdatesServiceCollectionExtensions
{
    /// <summary>Registers the Updates area. Owned by the Updates area; called from AddDomainNoticeCore.</summary>
    public static IServiceCollection AddUpdates(this IServiceCollection services, IConfiguration config)
    {
        // Named client for GitHub. Nothing creates it while Updates:GitHubRepository is blank (plan 11.4).
        services.AddHttpClient(GitHubReleaseSource.HttpClientName, c => c.Timeout = TimeSpan.FromMinutes(10));

        services.TryAddSingleton<IReleaseSource, GitHubReleaseSource>();
        services.TryAddSingleton<IServerInstallation, ServerInstallation>();

        services.AddSingleton<AgentPackageCache>();
        services.AddSingleton<IAgentPackageCache>(sp => sp.GetRequiredService<AgentPackageCache>());

        services.AddSingleton<ServerUpdateService>();
        services.AddSingleton<IServerUpdateService>(sp => sp.GetRequiredService<ServerUpdateService>());

        services.AddSingleton<PackageTransferManager>();
        services.AddSingleton<IAgentUpdateService, AgentUpdateService>();

        services.AddSingleton<AgentUpdateCoordinator>();
        services.AddSingleton<IAgentLifecycleObserver>(sp => sp.GetRequiredService<AgentUpdateCoordinator>());
        services.AddSingleton<IAgentMessageHandler>(sp => sp.GetRequiredService<AgentUpdateCoordinator>());

        services.AddSingleton<AgentUpdateTimeoutService>();
        services.AddHostedService(sp => sp.GetRequiredService<AgentUpdateTimeoutService>());
        services.AddSingleton<UpdatesBackgroundService>();
        services.AddHostedService(sp => sp.GetRequiredService<UpdatesBackgroundService>());
        return services;
    }
}
