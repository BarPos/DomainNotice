using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Tests.Infrastructure;
using DomainNotice.Server.Updates;
using DomainNotice.Shared;
using DomainNotice.Shared.Protocol;
using Microsoft.EntityFrameworkCore;

namespace DomainNotice.Server.Tests.Updates;

public class AgentUpdateTests(PostgresFixture pg)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Envelope Env = new() { Type = "x", Id = Guid.NewGuid() };

    [Fact]
    public async Task Request_needs_cached_package()
    {
        await using var kit = await UpdatesKit.CreateAsync(pg);
        var pc = await kit.AddComputerAsync("0.9.0");

        var (result, created) = await kit.AgentUpdates.RequestUpdateAsync([pc], "alice", Ct);

        Assert.False(result.Ok);
        Assert.Equal(0, created);
    }

    [Fact]
    public async Task Automatic_updates_update_outdated_agents_when_they_come_online()
    {
        await using var kit = await UpdatesKit.CreateAsync(pg);
        await kit.SeedPackageAsync();
        var pc = await kit.AddComputerAsync("0.9.0");
        var connection = new FakeConnection(pc, "0.9.0");
        kit.Hub.Connections[pc] = connection;

        // Off (default): coming online does nothing.
        Assert.False(await kit.AgentUpdates.GetAutoUpdateAsync(Ct));
        await kit.Coordinator.OnAgentOnlineAsync(connection, Ct);
        await using (var db = kit.Db.CreateContext()) Assert.Equal(0, await db.AgentUpdateTasks.CountAsync(Ct));

        // On: the Console setting wins over Agents:AutoUpdate, and the agent gets its task + UpdateAgent at once.
        Assert.True((await kit.AgentUpdates.SetAutoUpdateAsync(true, "alice", Ct)).Ok);
        Assert.True(await kit.AgentUpdates.GetAutoUpdateAsync(Ct));
        await using (var db = kit.Db.CreateContext())
        {
            var task = await db.AgentUpdateTasks.SingleAsync(Ct); // turning it on covered the online agent
            Assert.Equal(AgentUpdateService.AutoActor, task.RequestedBy);
        }
        Assert.Contains(connection.Sent, m => m is UpdateAgent);

        // A failed automatic update is not retried on the next connect (no reinstall loop).
        await using (var db = kit.Db.CreateContext())
        {
            var task = await db.AgentUpdateTasks.SingleAsync(Ct);
            task.State = AgentUpdateState.Failed;
            task.UpdatedAtUtc = kit.Time.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(Ct);
        }
        await kit.Coordinator.OnAgentOnlineAsync(connection, Ct);
        await using (var db = kit.Db.CreateContext()) Assert.Equal(1, await db.AgentUpdateTasks.CountAsync(Ct));

        // A current agent is never touched.
        var current = await kit.AddComputerAsync(AppVersion.Current);
        await kit.Coordinator.OnAgentOnlineAsync(new FakeConnection(current, AppVersion.Current), Ct);
        await using (var db = kit.Db.CreateContext()) Assert.Equal(0, await db.AgentUpdateTasks.CountAsync(t => t.ComputerId == current, Ct));
    }

    [Fact]
    public async Task Request_creates_one_open_task_per_outdated_computer_and_sends_when_online()
    {
        await using var kit = await UpdatesKit.CreateAsync(pg);
        var (package, _) = await kit.SeedPackageAsync();
        var outdatedOnline = await kit.AddComputerAsync("0.9.0");
        var outdatedOffline = await kit.AddComputerAsync("0.8.0");
        var current = await kit.AddComputerAsync(AppVersion.Current);
        var revoked = await kit.AddComputerAsync("0.9.0", revoked: true);
        var connection = new FakeConnection(outdatedOnline, "0.9.0");
        kit.Hub.Connections[outdatedOnline] = connection;

        var (result, created) = await kit.AgentUpdates.RequestUpdateAsync([outdatedOnline, outdatedOffline, current, revoked], "alice", Ct);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(2, created);
        await using (var db = kit.Db.CreateContext())
        {
            var tasks = await db.AgentUpdateTasks.ToListAsync(Ct);
            Assert.Equal(2, tasks.Count);
            Assert.All(tasks, t => Assert.Equal(AppVersion.Current, t.ToVersion));
            var online = tasks.Single(t => t.ComputerId == outdatedOnline);
            Assert.Equal(AgentUpdateState.Sent, online.State);
            Assert.NotNull(online.SentAtUtc);
            Assert.Equal("0.9.0", online.FromVersion);
            Assert.Equal(AgentUpdateState.Pending, tasks.Single(t => t.ComputerId == outdatedOffline).State);
            Assert.Contains(await db.AuditLog.ToListAsync(Ct), a => a.Action == AuditActions.AgentUpdateRequested);

            var command = Assert.IsType<UpdateAgent>(Assert.Single(connection.Sent));
            Assert.Equal(online.Id, command.TaskId);
            Assert.Equal(AppVersion.Current, command.Version);
            Assert.Equal(package.Size, command.Size);
            Assert.Equal(package.Sha256, command.Sha256);
        }

        // One open task per computer.
        var (again, createdAgain) = await kit.AgentUpdates.RequestUpdateAllOutdatedAsync("alice", Ct);
        Assert.True(again.Ok);
        Assert.Equal(0, createdAgain);
    }

    [Fact]
    public async Task All_outdated_and_cancel()
    {
        await using var kit = await UpdatesKit.CreateAsync(pg);
        await kit.SeedPackageAsync();
        await kit.AddComputerAsync("0.9.0");
        await kit.AddComputerAsync("0.9.9");
        await kit.AddComputerAsync(AppVersion.Current);
        await kit.AddComputerAsync("");

        var (result, created) = await kit.AgentUpdates.RequestUpdateAllOutdatedAsync("alice", Ct);
        Assert.True(result.Ok);
        Assert.Equal(2, created);

        Guid taskId;
        await using (var db = kit.Db.CreateContext()) taskId = (await db.AgentUpdateTasks.FirstAsync(Ct)).Id;
        Assert.True((await kit.AgentUpdates.CancelAsync(taskId, "bob", Ct)).Ok);
        Assert.Equal(AgentUpdateState.Cancelled, (await kit.GetTaskAsync(taskId)).State);
        Assert.False((await kit.AgentUpdates.CancelAsync(taskId, "bob", Ct)).Ok);
        await using (var db = kit.Db.CreateContext())
            Assert.Contains(await db.AuditLog.ToListAsync(Ct), a => a.Action == AuditActions.AgentUpdateCancelled && a.Actor == "bob");
    }

    [Fact]
    public async Task Package_streams_in_chunks_from_requested_offset()
    {
        await using var kit = await UpdatesKit.CreateAsync(pg);
        var (package, bytes) = await kit.SeedPackageAsync(600_000);
        var pc = await kit.AddComputerAsync("0.9.0");
        var task = await kit.AddTaskAsync(pc, AgentUpdateState.Sent, kit.Time.GetUtcNow().UtcDateTime);
        var connection = new FakeConnection(pc, "0.9.0");

        // Full transfer.
        var transfer = await kit.Coordinator.StartTransferAsync(connection, new PackageRequest { TaskId = task.Id, Offset = 0 }, Ct);
        Assert.NotNull(transfer);
        await transfer;
        Assert.Equal([0L, ProtocolInfo.ChunkSize, 2L * ProtocolInfo.ChunkSize], connection.Chunks.Select(c => c.Offset));
        Assert.All(connection.Chunks, c => Assert.True(c.Data.Length <= ProtocolInfo.ChunkSize));
        Assert.Equal(bytes, connection.Received(task.Id));
        var row = await kit.GetTaskAsync(task.Id);
        Assert.Equal(AgentUpdateState.Downloading, row.State);
        Assert.Equal(package.Size, row.BytesSent);

        // Resume after a dropped connection: only the rest is sent, starting at the offset.
        var resumed = new FakeConnection(pc, "0.9.0");
        await kit.Coordinator.HandleAsync(resumed, Env, new PackageRequest { TaskId = task.Id, Offset = 300_000 }, Ct);
        await kit.Transfers.WhenIdleAsync();
        Assert.Equal(300_000, resumed.Chunks.First().Offset);
        Assert.Equal(bytes[300_000..], resumed.Received(task.Id));
        Assert.All(resumed.Chunks, c => Assert.Equal(task.Id, c.TransferId));

        // Offsets outside the package are ignored.
        Assert.Null(await kit.Coordinator.StartTransferAsync(resumed, new PackageRequest { TaskId = task.Id, Offset = package.Size + 1 }, Ct));
    }

    [Fact]
    public async Task At_most_max_parallel_transfers_send_at_once()
    {
        await using var kit = await UpdatesKit.CreateAsync(pg);
        kit.AgentsOptions.MaxParallelUpdates = 2;
        var (_, bytes) = await kit.SeedPackageAsync(300_000);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connections = new List<(FakeConnection Conn, Guid TaskId)>();
        for (var i = 0; i < 3; i++)
        {
            var pc = await kit.AddComputerAsync("0.9.0");
            var task = await kit.AddTaskAsync(pc, AgentUpdateState.Sent, kit.Time.GetUtcNow().UtcDateTime);
            var conn = new FakeConnection(pc, "0.9.0") { BeforeChunk = () => gate.Task };
            connections.Add((conn, task.Id));
        }

        var transfers = new List<Task>();
        foreach (var (conn, taskId) in connections)
            transfers.Add((await kit.Coordinator.StartTransferAsync(conn, new PackageRequest { TaskId = taskId, Offset = 0 }, Ct))!);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (kit.Transfers.Sending < 2 && DateTime.UtcNow < deadline) await Task.Delay(10, Ct);
        await Task.Delay(100, Ct);
        Assert.Equal(2, kit.Transfers.Sending);
        Assert.All(connections, c => Assert.Empty(c.Conn.Chunks));

        gate.SetResult();
        await Task.WhenAll(transfers);
        Assert.Equal(2, kit.Transfers.MaxObservedParallel);
        Assert.All(connections, c => Assert.Equal(bytes, c.Conn.Received(c.TaskId)));
    }

    [Fact]
    public async Task Messages_for_another_computers_task_are_ignored()
    {
        await using var kit = await UpdatesKit.CreateAsync(pg);
        await kit.SeedPackageAsync();
        var owner = await kit.AddComputerAsync("0.9.0");
        var intruder = await kit.AddComputerAsync("0.9.0");
        var task = await kit.AddTaskAsync(owner, AgentUpdateState.Sent, kit.Time.GetUtcNow().UtcDateTime);
        var connection = new FakeConnection(intruder, "0.9.0");

        Assert.Null(await kit.Coordinator.StartTransferAsync(connection, new PackageRequest { TaskId = task.Id, Offset = 0 }, Ct));
        await kit.Coordinator.HandleAsync(connection, Env, new PackageRequest { TaskId = task.Id, Offset = 0 }, Ct);
        await kit.Coordinator.HandleAsync(connection, Env,
            new UpdateStatus { TaskId = task.Id, State = AgentUpdateState.Failed, Error = "forged" }, Ct);
        await kit.Transfers.WhenIdleAsync();

        Assert.Empty(connection.Chunks);
        var row = await kit.GetTaskAsync(task.Id);
        Assert.Equal(AgentUpdateState.Sent, row.State);
        Assert.Null(row.Error);
    }

    [Fact]
    public async Task Update_status_moves_state_and_failure_keeps_log_tail()
    {
        await using var kit = await UpdatesKit.CreateAsync(pg);
        var pc = await kit.AddComputerAsync("0.9.0");
        var task = await kit.AddTaskAsync(pc, AgentUpdateState.Downloading, kit.Time.GetUtcNow().UtcDateTime);
        var connection = new FakeConnection(pc, "0.9.0");

        await kit.Coordinator.HandleAsync(connection, Env, new UpdateStatus { TaskId = task.Id, State = AgentUpdateState.Verifying }, Ct);
        Assert.Equal(AgentUpdateState.Verifying, (await kit.GetTaskAsync(task.Id)).State);
        await kit.Coordinator.HandleAsync(connection, Env, new UpdateStatus { TaskId = task.Id, State = AgentUpdateState.Installing }, Ct);
        Assert.Equal(AgentUpdateState.Installing, (await kit.GetTaskAsync(task.Id)).State);

        // An agent cannot declare itself Done.
        await kit.Coordinator.HandleAsync(connection, Env, new UpdateStatus { TaskId = task.Id, State = AgentUpdateState.Done }, Ct);
        Assert.Equal(AgentUpdateState.Installing, (await kit.GetTaskAsync(task.Id)).State);

        var log = new string('x', 40_000) + "END";
        await kit.Coordinator.HandleAsync(connection, Env,
            new UpdateStatus { TaskId = task.Id, State = AgentUpdateState.Failed, Error = "msiexec exit 1603", LogTail = log }, Ct);
        var row = await kit.GetTaskAsync(task.Id);
        Assert.Equal(AgentUpdateState.Failed, row.State);
        Assert.Equal("msiexec exit 1603", row.Error);
        Assert.Equal(AgentUpdateCoordinator.MaxLogTailLength, row.LogTail!.Length);
        Assert.EndsWith("END", row.LogTail);
        Assert.NotNull(row.FinishedAtUtc);

        // Terminal tasks do not change any more.
        await kit.Coordinator.HandleAsync(connection, Env, new UpdateStatus { TaskId = task.Id, State = AgentUpdateState.Installing }, Ct);
        Assert.Equal(AgentUpdateState.Failed, (await kit.GetTaskAsync(task.Id)).State);
    }

    [Fact]
    public async Task Timeout_marks_in_flight_tasks_failed()
    {
        await using var kit = await UpdatesKit.CreateAsync(pg);
        kit.AgentsOptions.UpdateTimeoutMinutes = 30;
        var now = kit.Time.GetUtcNow().UtcDateTime;
        var old = await kit.AddTaskAsync(await kit.AddComputerAsync("0.9.0"), AgentUpdateState.Installing, now.AddMinutes(-31));
        var recent = await kit.AddTaskAsync(await kit.AddComputerAsync("0.9.0"), AgentUpdateState.Sent, now.AddMinutes(-10));
        var pending = await kit.AddTaskAsync(await kit.AddComputerAsync("0.9.0"), AgentUpdateState.Pending);

        Assert.Equal(1, await kit.TimeoutJob().SweepAsync(Ct));

        var failed = await kit.GetTaskAsync(old.Id);
        Assert.Equal(AgentUpdateState.Failed, failed.State);
        Assert.Contains("30 minutes", failed.Error);
        Assert.Equal(AgentUpdateState.Sent, (await kit.GetTaskAsync(recent.Id)).State);
        Assert.Equal(AgentUpdateState.Pending, (await kit.GetTaskAsync(pending.Id)).State);

        kit.Time.Advance(TimeSpan.FromMinutes(25));
        Assert.Equal(1, await kit.TimeoutJob().SweepAsync(Ct));
        Assert.Equal(AgentUpdateState.Failed, (await kit.GetTaskAsync(recent.Id)).State);
    }

    [Fact]
    public async Task Online_at_target_version_marks_done_and_pending_tasks_are_sent_on_connect()
    {
        await using var kit = await UpdatesKit.CreateAsync(pg);
        var (package, _) = await kit.SeedPackageAsync();
        var updated = await kit.AddComputerAsync("0.9.0");
        var installing = await kit.AddTaskAsync(updated, AgentUpdateState.Installing, kit.Time.GetUtcNow().UtcDateTime);
        var waiting = await kit.AddComputerAsync("0.9.0");
        var pending = await kit.AddTaskAsync(waiting, AgentUpdateState.Pending);

        // The MSI restarted the agent at the new version.
        var reconnected = new FakeConnection(updated, AppVersion.Current);
        await ((IAgentLifecycleObserver)kit.Coordinator).OnAgentOnlineAsync(reconnected, Ct);
        var done = await kit.GetTaskAsync(installing.Id);
        Assert.Equal(AgentUpdateState.Done, done.State);
        Assert.NotNull(done.FinishedAtUtc);
        Assert.Empty(reconnected.Sent);

        // An offline computer with a pending task gets UpdateAgent when it connects.
        var connection = new FakeConnection(waiting, "0.9.0");
        await ((IAgentLifecycleObserver)kit.Coordinator).OnAgentOnlineAsync(connection, Ct);
        var command = Assert.IsType<UpdateAgent>(Assert.Single(connection.Sent));
        Assert.Equal(pending.Id, command.TaskId);
        Assert.Equal(package.Sha256, command.Sha256);
        Assert.Equal(package.Size, command.Size);
        var sent = await kit.GetTaskAsync(pending.Id);
        Assert.Equal(AgentUpdateState.Sent, sent.State);
        Assert.NotNull(sent.SentAtUtc);
    }

    [Fact]
    public async Task Auto_update_creates_tasks_once_per_server_version()
    {
        await using var kit = await UpdatesKit.CreateAsync(pg);
        kit.AgentsOptions.AutoUpdate = true;
        kit.Releases.IsConfigured = false;
        var pc = await kit.AddComputerAsync("0.9.0");

        // No package yet: nothing happens, and it is retried later.
        await kit.Background().AutoUpdateAgentsAsync(Ct);
        await using (var db = kit.Db.CreateContext()) Assert.Empty(await db.AgentUpdateTasks.ToListAsync(Ct));

        await kit.SeedPackageAsync();
        await kit.Background().AutoUpdateAgentsAsync(Ct);
        await using (var db = kit.Db.CreateContext())
        {
            var task = await db.AgentUpdateTasks.SingleAsync(Ct);
            Assert.Equal(pc, task.ComputerId);
            Assert.Equal("system", task.RequestedBy);
        }
        Assert.Equal(AppVersion.Current, await kit.Settings.GetAsync(UpdateSettingKeys.AutoUpdateDoneForVersion, Ct));

        // Cancelled tasks are not recreated for the same server version.
        await using (var db = kit.Db.CreateContext())
            await kit.AgentUpdates.CancelAsync((await db.AgentUpdateTasks.SingleAsync(Ct)).Id, "alice", Ct);
        await kit.Background().AutoUpdateAgentsAsync(Ct);
        await using (var db = kit.Db.CreateContext()) Assert.Single(await db.AgentUpdateTasks.ToListAsync(Ct));
    }

    [Fact]
    public void Bandwidth_limiter_paces_to_the_configured_rate()
    {
        var time = new ManualTime(DateTimeOffset.UnixEpoch);
        var limiter = new BandwidthLimiter(1_000_000, time); // 1 MB/s, 1 s burst

        Assert.Equal(TimeSpan.Zero, limiter.Reserve(600_000));
        Assert.Equal(TimeSpan.Zero, limiter.Reserve(400_000));
        Assert.Equal(TimeSpan.FromSeconds(0.5), limiter.Reserve(500_000));
        time.Advance(TimeSpan.FromSeconds(0.5));
        Assert.Equal(TimeSpan.FromSeconds(0.25), limiter.Reserve(250_000));
        time.Advance(TimeSpan.FromSeconds(10)); // refills, but only up to the burst size
        Assert.Equal(TimeSpan.Zero, limiter.Reserve(1_000_000 - 250_000));
        Assert.True(limiter.Reserve(500_000) > TimeSpan.Zero);
    }
}
