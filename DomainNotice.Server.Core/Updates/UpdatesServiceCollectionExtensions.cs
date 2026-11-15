using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DomainNotice.Server.Updates;

public static class UpdatesServiceCollectionExtensions
{
    /// <summary>Registers the Updates area. Owned by the Updates area; called from AddDomainNoticeCore.</summary>
    public static IServiceCollection AddUpdates(this IServiceCollection services, IConfiguration config)
    {
        return services;
    }
}
