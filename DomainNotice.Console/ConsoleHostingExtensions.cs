using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DomainNotice.Console;

/// <summary>Entry points used by DomainNotice.Server/Program.cs. Owned by the Console area.</summary>
public static class ConsoleHostingExtensions
{
    /// <summary>Registers Razor components, MudBlazor, cookie authentication and authorization.</summary>
    public static IServiceCollection AddDomainNoticeConsole(this IServiceCollection services, IConfiguration config)
    {
        return services;
    }

    /// <summary>Adds the middleware (static files, auth, antiforgery) and maps the Console's endpoints and components.</summary>
    public static WebApplication UseDomainNoticeConsole(this WebApplication app)
    {
        return app;
    }
}
