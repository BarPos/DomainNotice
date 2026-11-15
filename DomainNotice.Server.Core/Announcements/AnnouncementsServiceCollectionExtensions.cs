using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DomainNotice.Server.Announcements;

public static class AnnouncementsServiceCollectionExtensions
{
    /// <summary>Registers the Announcements area. Owned by the Announcements area; called from AddDomainNoticeCore.</summary>
    public static IServiceCollection AddAnnouncements(this IServiceCollection services, IConfiguration config)
    {
        return services;
    }
}
