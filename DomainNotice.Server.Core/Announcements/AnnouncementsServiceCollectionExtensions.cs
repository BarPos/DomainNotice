using DomainNotice.Server.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DomainNotice.Server.Announcements;

public static class AnnouncementsServiceCollectionExtensions
{
    /// <summary>Registers the Announcements area. Owned by the Announcements area; called from AddDomainNoticeCore.</summary>
    public static IServiceCollection AddAnnouncements(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<TargetingDataLoader>();
        services.AddSingleton<AnonymousAnswerBuffer>();

        // IAgentHub is resolved lazily inside the coordinator (the hub itself depends on observers/handlers).
        services.AddSingleton<DeliveryCoordinator>();
        services.AddSingleton<IAnnouncementWindowSettings, AnnouncementWindowSettings>();
        services.AddSingleton<IAgentLifecycleObserver>(sp => sp.GetRequiredService<DeliveryCoordinator>());

        services.AddSingleton<ConfirmationHandler>();
        services.AddSingleton<IAgentMessageHandler>(sp => sp.GetRequiredService<ConfirmationHandler>());

        services.AddSingleton<AnnouncementService>();
        services.AddSingleton<IAnnouncementService>(sp => sp.GetRequiredService<AnnouncementService>());

        services.AddSingleton<ResultsExport>();
        services.AddSingleton<IResultsExport>(sp => sp.GetRequiredService<ResultsExport>());
        services.AddSingleton<IAnnouncementResults>(sp => sp.GetRequiredService<ResultsExport>());

        services.AddHostedService<DeliveryScheduler>();
        services.AddHostedService<AnonymousAnswerFlusher>();
        return services;
    }
}
