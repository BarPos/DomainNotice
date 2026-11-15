using DomainNotice.Server.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DomainNotice.Server.Directory;

public static class DirectoryServiceCollectionExtensions
{
    /// <summary>Registers the Directory area. Owned by the Directory area; called from AddDomainNoticeCore.</summary>
    public static IServiceCollection AddDirectoryIntegration(this IServiceCollection services, IConfiguration config)
    {
        // Nothing here reads or validates Directory settings: a bad directory configuration must not stop the host
        // (local accounts are the backup). Problems surface in the sync status and in TestConnectionAsync.
        services.TryAddSingleton<BindPasswordProvider>();
        services.TryAddSingleton<LdapConnector>();
        services.TryAddSingleton<ILdapSource, LdapDirectorySource>();
        services.TryAddSingleton<DirectorySyncCoordinator>();
        services.TryAddSingleton<DirectorySyncEngine>();
        services.TryAddSingleton<IDirectoryAdmin, DirectoryAdmin>();
        services.TryAddSingleton<IDomainAuthenticator, LdapDomainAuthenticator>();
        services.AddHostedService<DirectorySyncService>();
        return services;
    }
}
