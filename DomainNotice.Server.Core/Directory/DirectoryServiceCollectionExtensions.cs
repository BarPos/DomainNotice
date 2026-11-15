using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DomainNotice.Server.Directory;

public static class DirectoryServiceCollectionExtensions
{
    /// <summary>Registers the Directory area. Owned by the Directory area; called from AddDomainNoticeCore.</summary>
    public static IServiceCollection AddDirectoryIntegration(this IServiceCollection services, IConfiguration config)
    {
        return services;
    }
}
