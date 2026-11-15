using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DomainNotice.Server.Accounts;

public static class AccountsServiceCollectionExtensions
{
    /// <summary>Registers the Accounts area. Owned by the Accounts area; called from AddDomainNoticeCore.</summary>
    public static IServiceCollection AddAccounts(this IServiceCollection services, IConfiguration config)
    {
        return services;
    }
}
