using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DomainNotice.Server.Announcements;

/// <summary>
/// Ticks every <see cref="Interval"/> (and right after an announcement is saved) so start times, end times and repeat
/// intervals take effect for online computers.
/// </summary>
internal sealed class DeliveryScheduler(DeliveryCoordinator coordinator, ILogger<DeliveryScheduler> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await coordinator.WaitForWakeAsync(Interval, stoppingToken);
            if (stoppingToken.IsCancellationRequested) break;
            try
            {
                await coordinator.EvaluateAllOnlineAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Announcement scheduler pass failed");
            }
        }
    }
}
