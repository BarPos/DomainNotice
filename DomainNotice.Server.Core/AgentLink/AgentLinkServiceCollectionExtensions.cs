using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DomainNotice.Server.AgentLink;

public static class AgentLinkServiceCollectionExtensions
{
    /// <summary>Registers the AgentLink area. Owned by the AgentLink area; called from AddDomainNoticeCore.</summary>
    public static IServiceCollection AddAgentLink(this IServiceCollection services, IConfiguration config)
    {
        return services;
    }
}
