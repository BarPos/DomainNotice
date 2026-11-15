using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Data;
using DomainNotice.Server.Options;
using DomainNotice.Shared;
using DomainNotice.Shared.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DomainNotice.Server.Updates;

/// <summary>
/// The agent-link side of agent updates (plan 11.3): sends UpdateAgent when an agent comes online, marks a task Done
/// when the agent reconnects at the target version, streams the package on PackageRequest and records UpdateStatus.
/// Every message is checked against <see cref="IAgentConnection.ComputerId"/> (identity rule, plan 3.4): a task
/// id from another computer is ignored.
/// </summary>
internal sealed class AgentUpdateCoordinator(
    IDbContextFactory<AppDbContext> dbFactory,
    IAgentPackageCache packages,
    PackageTransferManager transfers,
    TimeProvider time,
    ILogger<AgentUpdateCoordinator> logger) : IAgentLifecycleObserver, IAgentMessageHandler
{
    public const int MaxErrorLength = 2000;
    public const int MaxLogTailLength = 16 * 1024;

    public IReadOnlyCollection<string> MessageTypes { get; } = [PackageRequest.Type, UpdateStatus.Type];

    public async Task OnAgentOnlineAsync(IAgentConnection connection, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var open = AgentUpdateTaskStates.Open;
        var tasks = await db.AgentUpdateTasks
            .Where(t => t.ComputerId == connection.ComputerId && open.Contains(t.State))
            .OrderBy(t => t.CreatedAtUtc)
            .ToListAsync(ct);
        if (tasks.Count == 0) return;

        var now = time.GetUtcNow().UtcDateTime;
        AgentPackageInfo? package = null;
        var toSend = new List<Data.Entities.AgentUpdateTask>();
        foreach (var task in tasks)
        {
            if (AgentUpdateTaskStates.IsAtOrAbove(connection.AgentVersion, task.ToVersion))
            {
                task.State = AgentUpdateState.Done;
                task.FinishedAtUtc = now;
                task.UpdatedAtUtc = now;
                task.Error = null;
                logger.LogInformation("Agent update task {TaskId}: computer {ComputerId} is now at {Version}", task.Id, task.ComputerId,
                    connection.AgentVersion);
                continue;
            }
            // Verifying/Installing: the agent is mid-install (or msiexec failed); it reports Failed itself or the timeout applies.
            if (task.State is AgentUpdateState.Pending or AgentUpdateState.Sent or AgentUpdateState.Downloading)
            {
                package ??= await packages.GetCurrentAsync(ct);
                if (package is null)
                {
                    logger.LogWarning("Agent update task {TaskId} waits: no cached agent package for {Version}", task.Id, AppVersion.Current);
                    continue;
                }
                toSend.Add(task);
            }
        }
        await db.SaveChangesAsync(ct);

        foreach (var task in toSend)
        {
            try
            {
                await connection.SendAsync(AgentUpdateTaskStates.BuildCommand(task, package!), ct: ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Sending UpdateAgent for task {TaskId} failed", task.Id);
                continue;
            }
            AgentUpdateTaskStates.MarkSent(task, package!, time.GetUtcNow().UtcDateTime);
        }
        if (toSend.Count > 0) await db.SaveChangesAsync(ct);
    }

    public Task OnAgentOfflineAsync(Guid computerId, CancellationToken ct)
    {
        transfers.CancelComputer(computerId);
        return Task.CompletedTask;
    }

    public Task HandleAsync(IAgentConnection connection, Envelope envelope, IAgentMessage message, CancellationToken ct) => message switch
    {
        PackageRequest r => HandlePackageRequestAsync(connection, r, ct),
        UpdateStatus s => HandleStatusAsync(connection, s, ct),
        _ => Task.CompletedTask,
    };

    /// <summary>Starts the background transfer; returns it (tests), or null when the request was ignored.</summary>
    internal async Task<Task?> StartTransferAsync(IAgentConnection connection, PackageRequest request, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var task = await db.AgentUpdateTasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == request.TaskId, ct);
        if (task is null || task.ComputerId != connection.ComputerId)
        {
            logger.LogWarning("PackageRequest for unknown task {TaskId} from computer {ComputerId} ignored", request.TaskId, connection.ComputerId);
            return null;
        }
        if (task.State is not (AgentUpdateState.Pending or AgentUpdateState.Sent or AgentUpdateState.Downloading))
        {
            logger.LogInformation("PackageRequest for task {TaskId} in state {State} ignored", task.Id, task.State);
            return null;
        }
        var package = await packages.GetCurrentAsync(ct);
        if (package is null || package.Version != task.ToVersion)
        {
            logger.LogWarning("PackageRequest for task {TaskId}: no cached package for {Version}", task.Id, task.ToVersion);
            return null;
        }
        if (request.Offset < 0 || request.Offset > package.Size)
        {
            logger.LogWarning("PackageRequest for task {TaskId}: offset {Offset} outside 0..{Size}", task.Id, request.Offset, package.Size);
            return null;
        }
        return transfers.Start(connection, task.Id, package, () => packages.OpenRead(package), request.Offset);
    }

    private async Task HandlePackageRequestAsync(IAgentConnection connection, PackageRequest request, CancellationToken ct) =>
        await StartTransferAsync(connection, request, ct); // the transfer itself runs in the background

    private async Task HandleStatusAsync(IAgentConnection connection, UpdateStatus status, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var task = await db.AgentUpdateTasks.FirstOrDefaultAsync(t => t.Id == status.TaskId, ct);
        if (task is null || task.ComputerId != connection.ComputerId)
        {
            logger.LogWarning("UpdateStatus for unknown task {TaskId} from computer {ComputerId} ignored", status.TaskId, connection.ComputerId);
            return;
        }
        if (!AgentUpdateTaskStates.IsOpen(task.State))
        {
            logger.LogInformation("UpdateStatus {State} for finished task {TaskId} ({TaskState}) ignored", status.State, task.Id, task.State);
            return;
        }
        var now = time.GetUtcNow().UtcDateTime;
        switch (status.State)
        {
            case AgentUpdateState.Downloading or AgentUpdateState.Verifying or AgentUpdateState.Installing:
                task.State = status.State;
                break;
            case AgentUpdateState.Failed:
                task.State = AgentUpdateState.Failed;
                task.Error = Truncate(status.Error, MaxErrorLength) ?? "The agent reported a failure.";
                task.LogTail = Truncate(status.LogTail, MaxLogTailLength);
                task.FinishedAtUtc = now;
                transfers.Cancel(task.Id);
                break;
            default:
                // Done, Pending, Sent and Cancelled are set by the server only.
                logger.LogInformation("UpdateStatus {State} for task {TaskId} ignored", status.State, task.Id);
                return;
        }
        task.UpdatedAtUtc = now;
        await db.SaveChangesAsync(ct);
    }

    private static string? Truncate(string? s, int max) => s is null ? null : s.Length <= max ? s : s[^max..];
}

/// <summary>Marks tasks Failed that are not Done within <c>Agents:UpdateTimeoutMinutes</c> after UpdateAgent was sent.</summary>
internal sealed class AgentUpdateTimeoutService(
    IDbContextFactory<AppDbContext> dbFactory,
    PackageTransferManager transfers,
    IOptions<AgentsOptions> options,
    TimeProvider time,
    ILogger<AgentUpdateTimeoutService> logger) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Agent update timeout sweep failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task<int> SweepAsync(CancellationToken ct)
    {
        var minutes = Math.Max(1, options.Value.UpdateTimeoutMinutes);
        var now = time.GetUtcNow().UtcDateTime;
        var cutoff = now - TimeSpan.FromMinutes(minutes);
        var inFlight = AgentUpdateTaskStates.InFlight;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var expired = await db.AgentUpdateTasks
            .Where(t => inFlight.Contains(t.State) && t.SentAtUtc != null && t.SentAtUtc < cutoff)
            .ToListAsync(ct);
        foreach (var task in expired)
        {
            task.State = AgentUpdateState.Failed;
            task.Error = $"The agent did not come back at version {task.ToVersion} within {minutes} minutes." +
                         (string.IsNullOrEmpty(task.Error) ? "" : " " + task.Error);
            task.FinishedAtUtc = now;
            task.UpdatedAtUtc = now;
            transfers.Cancel(task.Id);
            logger.LogWarning("Agent update task {TaskId} for computer {ComputerId} timed out", task.Id, task.ComputerId);
        }
        if (expired.Count > 0) await db.SaveChangesAsync(ct);
        return expired.Count;
    }
}
