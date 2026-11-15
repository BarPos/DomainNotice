using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Accounts;
using DomainNotice.Server.AgentLink;
using DomainNotice.Server.Announcements;
using DomainNotice.Server.Common;
using DomainNotice.Server.Data;
using DomainNotice.Server.Directory;
using DomainNotice.Server.Options;
using DomainNotice.Server.Updates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace DomainNotice.Server;

public static class ServiceCollectionExtensions
{
    public const string MigrationsAssembly = "DomainNotice.Server";

    /// <summary>
    /// Registers all non-UI server services. Each area registers itself in its own Add* method
    /// (AgentLink, Announcements, Directory, Accounts, Updates); do not register area services here.
    /// </summary>
    public static IServiceCollection AddDomainNoticeCore(this IServiceCollection services, IConfiguration config, IHostEnvironment env)
    {
        services.Configure<ConsoleOptions>(config.GetSection(ConsoleOptions.Section));
        services.Configure<PathsOptions>(config.GetSection(PathsOptions.Section));
        services.Configure<AgentsOptions>(config.GetSection(AgentsOptions.Section));
        services.Configure<AnonymousAnswersOptions>(config.GetSection(AnonymousAnswersOptions.Section));
        services.Configure<LocalAccountsOptions>(config.GetSection(LocalAccountsOptions.Section));
        services.Configure<UpdatesOptions>(config.GetSection(UpdatesOptions.Section));
        services.Configure<EnrolmentOptions>(config.GetSection(EnrolmentOptions.Section));
        services.Configure<HistoryOptions>(config.GetSection(HistoryOptions.Section));
        services.Configure<DirectoryOptions>(config.GetSection(DirectoryOptions.Section));

        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();

        var connectionString = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default is not set (PostgreSQL is required).");
        services.AddDbContextFactory<AppDbContext>(o => ConfigureDb(o, connectionString));
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

        var paths = config.GetSection(PathsOptions.Section).Get<PathsOptions>() ?? new PathsOptions();
        var dp = services.AddDataProtection().SetApplicationName("DomainNotice");
        if (!env.IsEnvironment("Testing"))
        {
            System.IO.Directory.CreateDirectory(paths.DataProtectionDirectory);
            dp.PersistKeysToFileSystem(new DirectoryInfo(paths.DataProtectionDirectory));
        }

        services.AddSingleton<ISettingsStore, SettingsStore>();
        services.AddSingleton<IAuditLog, AuditLog>();

        services.AddAgentLink(config);
        services.AddAnnouncements(config);
        services.AddDirectoryIntegration(config);
        services.AddAccounts(config);
        services.AddUpdates(config);
        return services;
    }

    public static void ConfigureDb(DbContextOptionsBuilder o, string connectionString) =>
        o.UseNpgsql(connectionString, n => n.MigrationsAssembly(MigrationsAssembly));
}
