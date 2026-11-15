using System.Threading.Channels;
using DomainNotice.Server.Data;
using DomainNotice.Server.Data.Entities;
using DomainNotice.Server.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DomainNotice.Server.Directory;

internal readonly record struct SyncRequest(bool Full, string? RequestedBy);

/// <summary>Shared state between the sync service and the Console ("Sync now", status).</summary>
internal sealed class DirectorySyncCoordinator
{
    private readonly Channel<SyncRequest> _requests = Channel.CreateUnbounded<SyncRequest>(new UnboundedChannelOptions { SingleReader = true });
    private int _running;

    public ChannelReader<SyncRequest> Requests => _requests.Reader;
    public bool Running => Volatile.Read(ref _running) == 1;
    /// <summary>A configuration problem found at start (shown as the last error until a run records one).</summary>
    public string? ConfigError { get; set; }

    public void Request(bool full, string? requestedBy) => _requests.Writer.TryWrite(new SyncRequest(full, requestedBy));

    internal void SetRunning(bool running) => Volatile.Write(ref _running, running ? 1 : 0);
}

/// <summary>
/// Background directory sync (plan 7 item 5): full sync on Directory:FullSyncCron (server local time) and at start when
/// no full sync has succeeded yet; incremental every Directory:IncrementalSyncMinutes; "Sync now" through the
/// coordinator. Does nothing when the directory is not configured. Never throws out of ExecuteAsync, so a directory
/// problem cannot stop the host (local accounts must keep working).
/// </summary>
internal sealed class DirectorySyncService(
    DirectorySyncEngine engine,
    DirectorySyncCoordinator coordinator,
    IDbContextFactory<AppDbContext> dbFactory,
    IOptions<DirectoryOptions> options,
    IHostEnvironment env,
    TimeProvider time,
    ILogger<DirectorySyncService> logger) : BackgroundService
{
    public const string DefaultCron = "0 2 * * *";
    private static readonly TimeSpan MaxWait = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var o = options.Value;
        if (!o.IsConfigured)
        {
            logger.LogInformation("Directory not configured (Directory:Hosts/BaseDn); directory sync is off.");
            return;
        }

        var scheduled = true;
        var configError = LdapSecurityPolicy.ValidateServiceAccount(o, env.IsDevelopment());
        if (configError is not null)
        {
            coordinator.ConfigError = configError;
            scheduled = false;
            logger.LogError("Directory sync disabled: {Error}", configError);
        }

        if (!CronSchedule.TryParse(o.FullSyncCron, out var cron, out var cronError))
        {
            logger.LogError("Directory:FullSyncCron is invalid ({Error}); using \"{Default}\".", cronError, DefaultCron);
            cron = CronSchedule.Parse(DefaultCron);
        }
        var incremental = o.IncrementalSyncMinutes > 0 ? TimeSpan.FromMinutes(o.IncrementalSyncMinutes) : (TimeSpan?)null;

        SyncRequest? pending = null;
        if (scheduled)
        {
            var hasFull = await SafeAsync(async () =>
            {
                await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
                return await db.DirectorySyncRuns.AnyAsync(r => r.Success && r.Kind == DirectorySyncKind.Full, stoppingToken);
            }, false);
            pending = new SyncRequest(!hasFull, null);
        }

        var nextFull = cron!.GetNextOccurrenceUtc(time.GetUtcNow().UtcDateTime, time.LocalTimeZone);
        DateTime? nextIncremental = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (pending is null)
                {
                    var now = time.GetUtcNow().UtcDateTime;
                    DateTime? due = null;
                    if (scheduled)
                    {
                        due = nextFull;
                        if (nextIncremental is { } ni && (due is null || ni < due)) due = ni;
                    }
                    if (due is { } d && d <= now)
                    {
                        var full = nextFull is { } nf && nf <= now;
                        pending = new SyncRequest(full, null);
                    }
                    else
                    {
                        pending = await WaitAsync(due is { } dd ? dd - now : null, stoppingToken);
                        continue; // re-evaluate (a timer firing returns null)
                    }
                }

                // Coalesce queued requests; a full request wins.
                var request = pending.Value;
                while (coordinator.Requests.TryRead(out var more))
                    request = new SyncRequest(request.Full || more.Full, request.RequestedBy ?? more.RequestedBy);
                pending = null;

                if (request.Full && nextFull is { } f && f <= time.GetUtcNow().UtcDateTime)
                    nextFull = cron.GetNextOccurrenceUtc(time.GetUtcNow().UtcDateTime, time.LocalTimeZone);

                coordinator.SetRunning(true);
                try
                {
                    await engine.RunAsync(request.Full ? DirectorySyncKind.Full : DirectorySyncKind.Incremental, request.RequestedBy, stoppingToken);
                }
                finally
                {
                    coordinator.SetRunning(false);
                }
                if (incremental is { } inc) nextIncremental = time.GetUtcNow().UtcDateTime + inc;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Directory sync loop error; retrying in a minute.");
                pending = null;
                await SafeAsync(async () => { await Task.Delay(TimeSpan.FromMinutes(1), time, stoppingToken); return true; }, false);
            }
        }
    }

    /// <summary>Waits for a "Sync now" request or the timeout. Returns the request, or null on timeout.</summary>
    private async Task<SyncRequest?> WaitAsync(TimeSpan? timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var read = coordinator.Requests.WaitToReadAsync(cts.Token).AsTask();
        var tasks = new List<Task> { read };
        // Capped: Task.Delay rejects very long delays (~49 days), and the loop re-evaluates anyway.
        if (timeout is { } t) tasks.Add(Task.Delay(t < TimeSpan.Zero ? TimeSpan.Zero : t > MaxWait ? MaxWait : t, time, cts.Token));
        var done = await Task.WhenAny(tasks);
        await cts.CancelAsync();
        ct.ThrowIfCancellationRequested();
        if (done == read && read.IsCompletedSuccessfully && read.Result && coordinator.Requests.TryRead(out var r)) return r;
        return null;
    }

    private async Task<T> SafeAsync<T>(Func<Task<T>> f, T fallback)
    {
        try
        {
            return await f();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Directory sync: database not reachable.");
            return fallback;
        }
        catch (OperationCanceledException)
        {
            return fallback;
        }
    }
}
