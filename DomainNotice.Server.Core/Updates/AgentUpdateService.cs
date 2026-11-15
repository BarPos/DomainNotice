using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Data;
using DomainNotice.Server.Data.Entities;
using DomainNotice.Shared;
using DomainNotice.Shared.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DomainNotice.Server.Updates;

internal static class AgentUpdateTaskStates
{
    /// <summary>States of a task that is still running (one per computer).</summary>
    public static readonly AgentUpdateState[] Open =
        [AgentUpdateState.Pending, AgentUpdateState.Sent, AgentUpdateState.Downloading, AgentUpdateState.Verifying, AgentUpdateState.Installing];

    /// <summary>States after UpdateAgent went out; the timeout applies to these.</summary>
    public static readonly AgentUpdateState[] InFlight =
        [AgentUpdateState.Sent, AgentUpdateState.Downloading, AgentUpdateState.Verifying, AgentUpdateState.Installing];

    public static bool IsOpen(AgentUpdateState s) => Array.IndexOf(Open, s) >= 0;

    /// <summary>True when the agent runs <paramref name="target"/> or newer.</summary>
    public static bool IsAtOrAbove(string? agentVersion, string target) =>
        AppVersion.TryParse(agentVersion, out var a) && AppVersion.TryParse(target, out var t) && a.CompareTo(t) >= 0;

    public static UpdateAgent BuildCommand(AgentUpdateTask task, AgentPackageInfo package) =>
        new() { TaskId = task.Id, Version = package.Version, Size = package.Size, Sha256 = package.Sha256 };

    /// <summary>
    /// After UpdateAgent went out: Pending → Sent, retarget to the package version. The timeout runs from the first
    /// send, so an agent that reconnects in a loop still times out.
    /// </summary>
    public static void MarkSent(AgentUpdateTask task, AgentPackageInfo package, DateTime now)
    {
        if (task.State == AgentUpdateState.Pending) task.State = AgentUpdateState.Sent;
        task.ToVersion = package.Version;
        task.SentAtUtc ??= now;
        task.UpdatedAtUtc = now;
    }
}

/// <summary>Console actions for agent updates (plan 11.3). The target is always the server's own version.</summary>
internal sealed class AgentUpdateService(
    IDbContextFactory<AppDbContext> dbFactory,
    IAgentPackageCache packages,
    PackageTransferManager transfers,
    IAgentHub hub,
    IAuditLog audit,
    TimeProvider time,
    ILogger<AgentUpdateService> logger,
    ISettingsStore settings,
    Microsoft.Extensions.Options.IOptions<Options.AgentsOptions> agentsOptions) : IAgentUpdateService
{
    private readonly SemaphoreSlim _createLock = new(1, 1);
    public const string AutoActor = "system (automatic update)";
    /// <summary>An automatic update that failed is not retried automatically for this long (no reinstall loop).</summary>
    public static readonly TimeSpan AutoRetryAfterFailure = TimeSpan.FromHours(24);

    public async Task<bool> GetAutoUpdateAsync(CancellationToken ct = default)
    {
        var value = await settings.GetAsync(SettingKeys.AgentsAutoUpdate, ct);
        return bool.TryParse(value, out var enabled) ? enabled : agentsOptions.Value.AutoUpdate;
    }

    public async Task<OperationResult> SetAutoUpdateAsync(bool enabled, string actor, CancellationToken ct = default)
    {
        await settings.SetAsync(SettingKeys.AgentsAutoUpdate, enabled ? "true" : "false", actor, ct);
        await audit.WriteAsync(actor, AuditActions.SettingChanged, "Setting", SettingKeys.AgentsAutoUpdate, enabled ? "on" : "off", ct);
        if (!enabled) return OperationResult.Success;
        var online = hub.OnlineComputerIds.ToList();
        if (online.Count == 0 || await packages.GetCurrentAsync(ct) is null) return OperationResult.Success;
        var (result, created) = await RequestUpdateAsync(online, AutoActor, ct);
        if (result.Ok && created > 0) logger.LogInformation("Automatic updates turned on: {Created} online agent(s) are being updated", created);
        return OperationResult.Success;
    }

    public async Task AutoUpdateOnlineAgentAsync(Guid computerId, string? agentVersion, CancellationToken ct = default)
    {
        if (!AppVersion.IsAgentOutdated(agentVersion) || !await GetAutoUpdateAsync(ct)) return;
        if (await packages.GetCurrentAsync(ct) is null) return;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var since = time.GetUtcNow().UtcDateTime - AutoRetryAfterFailure;
            var failedRecently = await db.AgentUpdateTasks.AsNoTracking().AnyAsync(t =>
                t.ComputerId == computerId && t.ToVersion == AppVersion.Current && t.State == AgentUpdateState.Failed &&
                t.UpdatedAtUtc >= since, ct);
            if (failedRecently) return;
        }
        var (result, created) = await RequestUpdateAsync([computerId], AutoActor, ct);
        if (!result.Ok) logger.LogWarning("Automatic update of computer {ComputerId} not started: {Error}", computerId, result.Error);
        else if (created > 0) logger.LogInformation("Automatic update of computer {ComputerId} to {Version} started", computerId, AppVersion.Current);
    }

    public async Task<(OperationResult Result, int Created)> RequestUpdateAsync(IReadOnlyCollection<Guid> computerIds, string actor,
        CancellationToken ct = default)
    {
        if (computerIds.Count == 0) return (OperationResult.Success, 0);
        var package = await packages.GetCurrentAsync(ct);
        if (package is null)
            return (OperationResult.Fail($"The agent package for version {AppVersion.Current} is not in the cache. " +
                                         "Check for updates, or import a locally built MSI in Settings."), 0);

        var ids = computerIds.Distinct().ToList();
        var created = new List<AgentUpdateTask>();
        await _createLock.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var computers = await db.Computers.AsNoTracking()
                .Where(c => ids.Contains(c.Id) && c.RevokedAtUtc == null && c.ReplacedByComputerId == null)
                .Select(c => new { c.Id, c.AgentVersion })
                .ToListAsync(ct);
            var open = AgentUpdateTaskStates.Open;
            var busy = (await db.AgentUpdateTasks.AsNoTracking()
                .Where(t => ids.Contains(t.ComputerId) && open.Contains(t.State))
                .Select(t => t.ComputerId).ToListAsync(ct)).ToHashSet();

            var now = time.GetUtcNow().UtcDateTime;
            foreach (var c in computers)
            {
                if (busy.Contains(c.Id)) continue;
                if (!AppVersion.IsAgentOutdated(c.AgentVersion)) continue;
                var task = new AgentUpdateTask
                {
                    Id = Guid.NewGuid(),
                    ComputerId = c.Id,
                    FromVersion = c.AgentVersion,
                    ToVersion = AppVersion.Current,
                    RequestedBy = actor,
                    State = AgentUpdateState.Pending,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                };
                db.AgentUpdateTasks.Add(task);
                created.Add(task);
            }
            if (created.Count > 0) await db.SaveChangesAsync(ct);
        }
        finally
        {
            _createLock.Release();
        }

        if (created.Count > 0)
        {
            await audit.WriteAsync(actor, AuditActions.AgentUpdateRequested,
                created.Count == 1 ? "Computer" : "Computers",
                created.Count == 1 ? created[0].ComputerId.ToString() : null,
                $"{created.Count} task(s) to version {AppVersion.Current}" +
                (created.Count == 1 ? $"; task {created[0].Id}" : ""), ct);
        }

        foreach (var task in created) await TrySendAsync(task, package, ct);
        return (OperationResult.Success, created.Count);
    }

    public async Task<(OperationResult Result, int Created)> RequestUpdateAllOutdatedAsync(string actor, CancellationToken ct = default)
    {
        List<(Guid Id, string Version)> all;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            all = (await db.Computers.AsNoTracking()
                    .Where(c => c.RevokedAtUtc == null && c.ReplacedByComputerId == null)
                    .Select(c => new { c.Id, c.AgentVersion }).ToListAsync(ct))
                .Select(c => (c.Id, c.AgentVersion)).ToList();
        }
        var outdated = all.Where(c => AppVersion.IsAgentOutdated(c.Version)).Select(c => c.Id).ToList();
        if (outdated.Count == 0) return (OperationResult.Success, 0);
        return await RequestUpdateAsync(outdated, actor, ct);
    }

    public async Task<OperationResult> CancelAsync(Guid taskId, string actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var task = await db.AgentUpdateTasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null) return OperationResult.Fail("Update task not found.");
        if (!AgentUpdateTaskStates.IsOpen(task.State)) return OperationResult.Fail($"The task is already {task.State}.");
        var now = time.GetUtcNow().UtcDateTime;
        task.State = AgentUpdateState.Cancelled;
        task.FinishedAtUtc = now;
        task.UpdatedAtUtc = now;
        await db.SaveChangesAsync(ct);
        transfers.Cancel(taskId);
        await audit.WriteAsync(actor, AuditActions.AgentUpdateCancelled, "AgentUpdateTask", taskId.ToString(),
            $"computer {task.ComputerId}", ct);
        return OperationResult.Success;
    }

    private async Task TrySendAsync(AgentUpdateTask task, AgentPackageInfo package, CancellationToken ct)
    {
        if (!hub.IsOnline(task.ComputerId)) return;
        bool sent;
        try
        {
            sent = await hub.TrySendAsync(task.ComputerId, AgentUpdateTaskStates.BuildCommand(task, package), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Sending UpdateAgent for task {TaskId} failed", task.Id);
            sent = false;
        }
        if (!sent) return; // stays Pending; sent when the agent next connects

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.AgentUpdateTasks.FirstOrDefaultAsync(t => t.Id == task.Id, ct);
        if (row is null || !AgentUpdateTaskStates.IsOpen(row.State)) return;
        AgentUpdateTaskStates.MarkSent(row, package, time.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
    }
}
