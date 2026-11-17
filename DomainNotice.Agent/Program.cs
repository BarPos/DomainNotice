// DomainNotice.Agent — Windows service (LocalSystem): server link, keys, user sessions, Session UI launcher.
using DomainNotice.Agent.Link;
using DomainNotice.Agent.Logging;
using DomainNotice.Agent.Logic;
using DomainNotice.Agent.Platform;
using DomainNotice.Agent.Sessions;
using DomainNotice.Agent.Updates;
using DomainNotice.Shared;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging.EventLog;

// Administrator tool: "DomainNotice.Agent.exe --reenrol" makes the service create a new key and enrol again
// (e.g. after the server revoked this computer; a revoked key stays refused forever). Needs admin rights.
if (args.Any(a => a.Equals("--reenrol", StringComparison.OrdinalIgnoreCase)))
    return ReenrolCommand.Run();

var paths = AgentPaths.Default;
// Secures %ProgramData%\DomainNotice before anything uses it. On failure, updates and file logging stay off
// (paths.InsecureReason) and the Event Log gets the reason.
var folders = paths.EnsureCreatedAndSecured();

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    // Services start in System32; keep the content root next to the executable.
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Services.AddWindowsService(options => options.ServiceName = AgentConstants.ServiceName);
builder.Services.AddSingleton<SessionChangeEvents>();
string? recoveryError = null;
if (WindowsServiceHelpers.IsWindowsService())
{
    builder.Services.AddSingleton<IHostLifetime, AgentServiceLifetime>();
    recoveryError = ServiceRecovery.Ensure(AgentConstants.ServiceName);
}

builder.Services.Configure<HostOptions>(options =>
{
    options.ShutdownTimeout = TimeSpan.FromSeconds(15);
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
});

// Logs: rolling files in %ProgramData%\DomainNotice\logs (only when the folder is secured) plus the Windows Event Log
// (warnings and errors, and the start/stop/connection events of the ServiceEvents category).
if (folders.Error is null) builder.Logging.AddProvider(new RollingFileLoggerProvider(paths));
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);
// The default host limits the Event Log provider to Warning; these category rules are more specific and win.
builder.Logging.AddFilter<EventLogLoggerProvider>(AgentConstants.ServiceEventsCategory, LogLevel.Information);
builder.Logging.AddFilter<EventLogLoggerProvider>("Microsoft.Hosting.Lifetime", LogLevel.Information);
builder.Services.Configure<EventLogSettings>(settings =>
{
    settings.SourceName = AgentConstants.EventSource;
    settings.LogName = "Application";
    settings.Filter = (category, level) =>
        level >= LogLevel.Warning ||
        (level >= LogLevel.Information && (category == AgentConstants.ServiceEventsCategory || category == "Microsoft.Hosting.Lifetime"));
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(paths);
builder.Services.AddSingleton<AgentSettingsStore>();
builder.Services.AddSingleton<StatusTrigger>();
builder.Services.AddSingleton(sp =>
{
    var router = new AnnouncementRouter(sp.GetRequiredService<TimeProvider>());
    router.InitializeLanguage(sp.GetRequiredService<AgentSettingsStore>().LoadLanguage());
    return router;
});
builder.Services.AddSingleton<AgentUpdater>();
builder.Services.AddSingleton<SessionManager>();
builder.Services.AddSingleton<ISessionsSource>(sp => sp.GetRequiredService<SessionManager>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<SessionManager>());
builder.Services.AddSingleton<ServerLinkService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ServerLinkService>());

var host = builder.Build();

var events = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(AgentConstants.ServiceEventsCategory);
var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStarted.Register(() => events.LogInformation("DomainNotice Agent {Version} started.", AppVersion.Current));
lifetime.ApplicationStopping.Register(() => events.LogInformation("DomainNotice Agent {Version} stopping.", AppVersion.Current));
foreach (var repair in folders.Repairs) events.LogWarning("Data folder: {Repair}", repair);
if (folders.Error is not null)
    events.LogError("{Error} Agent updates and file logging are off until the folder can be secured (fix it, then restart the service).", folders.Error);
if (recoveryError is not null) events.LogWarning("Could not set the service recovery actions: {Error}", recoveryError);

try
{
    host.Run();
    return Environment.ExitCode;
}
catch (Exception ex)
{
    events.LogCritical(ex, "DomainNotice Agent stopped after an unexpected error.");
    // Non-zero exit so the service recovery actions (restart after 1 minute) apply.
    return 1;
}
