using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Options;
using DomainNotice.Shared;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DomainNotice.Server.Updates;

/// <summary>
/// Startup and periodic work of the Updates area. Runs only in the normal server process (Program.cs removes hosted
/// services for CLI runs such as <c>--migrate-only</c>):
/// <list type="bullet">
/// <item>finish the ServerUpdateRun from the result file update.sh left;</item>
/// <item>fetch the agent MSI for the server's own version if it is missing (only when updates are configured);</item>
/// <item>check for releases every <c>Updates:CheckIntervalHours</c> (only when configured);</item>
/// <item>P11: with automatic updates on (Settings, or <c>Agents:AutoUpdate</c>), request updates for outdated agents
/// once per server version. Agents that come online later are handled when they connect.</item>
/// </list>
/// </summary>
internal sealed class UpdatesBackgroundService(
    ServerUpdateService server,
    AgentPackageCache packages,
    IAgentUpdateService agentUpdates,
    IReleaseSource releases,
    ISettingsStore settings,
    IOptions<UpdatesOptions> updatesOptions,
    TimeProvider time,
    ILogger<UpdatesBackgroundService> logger) : BackgroundService
{
    internal static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(5);
    public const string SystemActor = "system";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Guard(() => RunStartupAsync(stoppingToken), "startup");
        using var timer = new PeriodicTimer(TickInterval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await Guard(() => TickAsync(stoppingToken), "periodic check");
    }

    internal async Task RunStartupAsync(CancellationToken ct)
    {
        await server.ProcessResultFileAsync(ct);
        if (releases.IsConfigured)
        {
            var ensure = await packages.EnsureCurrentAsync(ct);
            if (!ensure.Ok) logger.LogInformation("Agent package for {Version} not cached: {Error}", AppVersion.Current, ensure.Error);
        }
        await AutoUpdateAgentsAsync(ct);
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        if (releases.IsConfigured && await IsCheckDueAsync(ct))
            await server.CheckNowAsync(SystemActor, ct);
        await AutoUpdateAgentsAsync(ct);
    }

    internal async Task<bool> IsCheckDueAsync(CancellationToken ct)
    {
        var hours = updatesOptions.Value.CheckIntervalHours;
        if (hours <= 0) hours = 24;
        var last = await server.ReadLastCheckAsync(ct);
        return last is null || time.GetUtcNow().UtcDateTime - last.CheckedAtUtc >= TimeSpan.FromHours(hours);
    }

    /// <summary>P11: once per server version, after the package for that version is cached.</summary>
    internal async Task AutoUpdateAgentsAsync(CancellationToken ct)
    {
        if (!await agentUpdates.GetAutoUpdateAsync(ct)) return;
        var done = await settings.GetAsync(UpdateSettingKeys.AutoUpdateDoneForVersion, ct);
        if (done == AppVersion.Current) return;
        if (await packages.GetCurrentAsync(ct) is null) return; // retried on the next tick (e.g. after an import)
        var (result, created) = await agentUpdates.RequestUpdateAllOutdatedAsync(SystemActor, ct);
        if (!result.Ok)
        {
            logger.LogWarning("Automatic agent updates failed: {Error}", result.Error);
            return;
        }
        await settings.SetAsync(UpdateSettingKeys.AutoUpdateDoneForVersion, AppVersion.Current, SystemActor, ct);
        logger.LogInformation("Automatic agent updates to {Version}: {Created} task(s) created", AppVersion.Current, created);
    }

    private async Task Guard(Func<Task> action, string what)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Updates {What} failed", what);
        }
    }
}
