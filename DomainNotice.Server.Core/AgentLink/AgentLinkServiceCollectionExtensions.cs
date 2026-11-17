using DomainNotice.Server.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace DomainNotice.Server.AgentLink;

public static class AgentLinkServiceCollectionExtensions
{
    /// <summary>Registers the AgentLink area. Owned by the AgentLink area; called from AddDomainNoticeCore.</summary>
    public static IServiceCollection AddAgentLink(this IServiceCollection services, IConfiguration config)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<AgentLinkTimings>();
        services.TryAddSingleton<AgentListenerEndpoint>();

        services.TryAddSingleton<ServerIdentity>();
        services.TryAddSingleton<IServerIdentity>(sp => sp.GetRequiredService<ServerIdentity>());

        services.TryAddSingleton<AgentHub>();
        services.TryAddSingleton<IAgentHub>(sp => sp.GetRequiredService<AgentHub>());
        services.TryAddSingleton<AgentObservers>();
        services.TryAddSingleton<IAgentEvents>(sp => sp.GetRequiredService<AgentObservers>());

        services.TryAddSingleton<InventoryLimiter>();
        services.TryAddSingleton<ComputerInventory>();
        services.TryAddSingleton<Enrolment>();
        services.TryAddSingleton<IEnrolmentAdmin>(sp => sp.GetRequiredService<Enrolment>());
        services.TryAddSingleton<AdLinkService>();
        services.TryAddSingleton<KerberosProofValidator>();
        services.TryAddSingleton<ComputerAdminService>();
        services.TryAddSingleton<IComputerAdminService>(sp => sp.GetRequiredService<ComputerAdminService>());
        services.TryAddSingleton<AgentProtocol>();
        services.TryAddSingleton<ConnectionLimiter>();

        services.TryAddSingleton<AgentListener>();
        services.TryAddSingleton<OfflineSweeper>();
        services.TryAddSingleton<AdLinkJob>();
        services.TryAddSingleton<HistoryRetention>();
        services.AddHostedService(sp => sp.GetRequiredService<AgentListener>());
        services.AddHostedService(sp => sp.GetRequiredService<OfflineSweeper>());
        services.AddHostedService(sp => sp.GetRequiredService<AdLinkJob>());
        services.AddHostedService(sp => sp.GetRequiredService<HistoryRetention>());
        services.AddSingleton<AgentConfigPusher>();
        services.AddSingleton<IAgentLifecycleObserver>(sp => sp.GetRequiredService<AgentConfigPusher>());
        return services;
    }
}
