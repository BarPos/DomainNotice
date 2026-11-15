using DomainNotice.Server.Data;
using DomainNotice.Server.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DomainNotice.Server.AgentLink;

/// <summary>
/// Marks computers offline after Agents:OfflineAfterSeconds of silence by closing their connection (the
/// connection teardown does the inventory work), and flushes LastSeenAtUtc of online computers now and then.
/// </summary>
internal sealed class OfflineSweeper(
    AgentHub hub,
    ComputerInventory inventory,
    IOptions<AgentsOptions> options,
    AgentLinkTimings timings,
    TimeProvider time,
    ILogger<OfflineSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(timings.SweepInterval, time);
        while (await WaitAsync(timer, stoppingToken))
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Offline sweep failed");
            }
        }
    }

    internal async Task SweepAsync(CancellationToken ct)
    {
        var limit = TimeSpan.FromSeconds(Math.Max(1, options.Value.OfflineAfterSeconds));
        var now = time.GetUtcNow().UtcDateTime;
        var connections = hub.Connections;
        foreach (var conn in connections)
        {
            if (now - conn.LastActivityUtc <= limit) continue;
            logger.LogInformation("Computer {Computer} silent for {Seconds:F0} s; closing its connection", conn.ComputerId,
                (now - conn.LastActivityUtc).TotalSeconds);
            conn.Abort();
        }
        await inventory.FlushLastSeenAsync(connections.Where(c => !c.IsClosing), timings.LastSeenFlushInterval, ct);
    }

    internal static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

/// <summary>Re-evaluates the AD name fallback for unlinked computers every few minutes.</summary>
internal sealed class AdLinkJob(AdLinkService adLink, AgentLinkTimings timings, TimeProvider time, ILogger<AdLinkJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(timings.AdLinkInterval, time);
        while (await OfflineSweeper.WaitAsync(timer, stoppingToken))
        {
            try
            {
                var changed = await adLink.EvaluatePendingAsync(stoppingToken);
                if (changed > 0) logger.LogInformation("AD name fallback linked or changed {Count} computers", changed);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "AD link re-evaluation failed");
            }
        }
    }
}

/// <summary>
/// History retention (plan 6.3, P6): deletes CLOSED name, logon and IP history rows older than
/// History:RetentionDays (default 30). Open rows and everything else are never pruned. RetentionDays &lt;= 0 keeps all.
/// </summary>
internal sealed class HistoryRetention(
    IDbContextFactory<AppDbContext> dbFactory,
    IOptions<HistoryOptions> options,
    AgentLinkTimings timings,
    TimeProvider time,
    ILogger<HistoryRetention> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(timings.RetentionFirstDelay, time, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        using var timer = new PeriodicTimer(timings.RetentionInterval, time);
        do
        {
            try
            {
                await PruneAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "History retention failed");
            }
        } while (await OfflineSweeper.WaitAsync(timer, stoppingToken));
    }

    public async Task<(int Names, int Logons, int Ips)> PruneAsync(CancellationToken ct)
    {
        var days = options.Value.RetentionDays;
        if (days <= 0) return (0, 0, 0);
        var cutoff = time.GetUtcNow().UtcDateTime.AddDays(-days);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var names = await db.ComputerNameHistory.Where(h => !h.IsCurrent && h.LastSeenUtc < cutoff).ExecuteDeleteAsync(ct);
        var logons = await db.UserLogonHistory.Where(h => h.LogoffAtUtc != null && h.LogoffAtUtc < cutoff).ExecuteDeleteAsync(ct);
        var ips = await db.ComputerIpHistory.Where(h => !h.IsCurrent && h.LastSeenUtc < cutoff).ExecuteDeleteAsync(ct);
        if (names + logons + ips > 0)
            logger.LogInformation("History retention ({Days} days): deleted {Names} name, {Logons} logon and {Ips} IP rows", days, names, logons, ips);
        return (names, logons, ips);
    }
}
