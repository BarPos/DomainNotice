using DomainNotice.Server.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DomainNotice.Server.Accounts;

public static class AccountsServiceCollectionExtensions
{
    /// <summary>Registers the Accounts area. Owned by the Accounts area; called from AddDomainNoticeCore.</summary>
    /// <remarks>Domain sign-in uses <see cref="Directory.IDomainAuthenticator"/>, registered by AddDirectoryIntegration.</remarks>
    public static IServiceCollection AddAccounts(this IServiceCollection services, IConfiguration config)
    {
        services.TryAddSingleton<FirstRunGate>();
        services.TryAddSingleton<SignInRateLimiter>();
        services.TryAddSingleton<ILocalAccountService, LocalAccountService>();
        services.TryAddSingleton<IAdminSignInService, AdminSignInService>();
        return services;
    }
}
