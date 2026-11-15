using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DomainNotice.Agent.Logic;
using DomainNotice.Agent.Platform;
using DomainNotice.Shared;
using DomainNotice.Shared.Protocol;

namespace DomainNotice.Agent.Updates;

/// <summary>
/// Agent update (plan 11.3): UpdateAgent → PackageRequest → binary chunks into updates\&lt;taskId&gt;.part (resumed
/// after a reconnect) → size + SHA-256 check → msiexec started detached, so it survives the MSI stopping this
/// service. The outcome is reported with UpdateStatus; a failed or rolled-back install is detected at the next
/// start through the install marker and reported with the end of the MSI log.
/// </summary>
public sealed class AgentUpdater : IDisposable
{
    private static readonly TimeSpan KeepFilesFor = TimeSpan.FromDays(7);

    private readonly ILogger<AgentUpdater> _logger;
    private readonly AgentPaths _paths;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DateTime _processStartedUtc;

    private IServerSender? _link;
    private PackageDownload? _download;
    private bool _requestedOnThisLink;
    private bool _resyncRequested;
    private bool _installing;

    public AgentUpdater(ILogger<AgentUpdater> logger, AgentPaths paths, TimeProvider time)
    {
        _logger = logger;
        _paths = paths;
        _time = time;
        _processStartedUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();
    }

    public async Task OnLinkUpAsync(IServerSender link, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _link = link;
            _requestedOnThisLink = false;
            _resyncRequested = false;
            await ReportPreviousInstallAsync(ct).ConfigureAwait(false);

            if (_download is { IsComplete: false } download && !_installing)
                await RequestAsync(download, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void OnLinkDown()
    {
        _link = null;
        _requestedOnThisLink = false;
    }

    public async Task OnUpdateAgentAsync(UpdateAgent command, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            switch (UpdatePolicy.Decide(command.Version, AppVersion.Current))
            {
                case UpdateDecision.AlreadyCurrent:
                    _logger.LogInformation("Update task {TaskId}: already at version {Version}; nothing to do.", command.TaskId, command.Version);
                    return;
                case UpdateDecision.Older:
                    await FailAsync(command.TaskId, $"Version {command.Version} is older than this agent ({AppVersion.Current}); downgrades are not done.", null, ct).ConfigureAwait(false);
                    return;
                case UpdateDecision.Invalid:
                    await FailAsync(command.TaskId, $"Version '{command.Version}' is not a valid version.", null, ct).ConfigureAwait(false);
                    return;
            }

            if (UpdatePolicy.Validate(command) is { } invalid)
            {
                await FailAsync(command.TaskId, invalid, null, ct).ConfigureAwait(false);
                return;
            }

            if (_installing)
            {
                _logger.LogInformation("Update task {TaskId}: an install is already running; ignored.", command.TaskId);
                return;
            }

            if (_download is { } current && current.Matches(command))
            {
                // Re-sent after a reconnect: resume, unless we already asked on this link.
                if (current.IsComplete) await VerifyAndInstallAsync(current, ct).ConfigureAwait(false);
                else if (!_requestedOnThisLink) await RequestAsync(current, ct).ConfigureAwait(false);
                return;
            }

            _download?.Dispose();
            PackageDownload.CleanFolder(_paths.Updates, command.TaskId, KeepFilesFor, _time.GetUtcNow().UtcDateTime);
            _download = PackageDownload.OpenOrResume(_paths.Updates, command);
            _logger.LogInformation("Update task {TaskId}: downloading version {Version} ({Size} bytes, resuming at {Offset}).",
                command.TaskId, command.Version, command.Size, _download.Offset);
            await SendStatusAsync(command.TaskId, AgentUpdateState.Downloading, null, null, ct).ConfigureAwait(false);

            if (_download.IsComplete) await VerifyAndInstallAsync(_download, ct).ConfigureAwait(false);
            else await RequestAsync(_download, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Update task {TaskId}: cannot write the package.", command.TaskId);
            await FailAsync(command.TaskId, "Cannot write the package: " + ex.Message, null, ct).ConfigureAwait(false);
            DiscardDownload();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task OnChunkAsync(PackageChunk chunk, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var download = _download;
            if (download is null || _installing)
            {
                _logger.LogDebug("Ignored a package chunk for transfer {TransferId}: no download in progress.", chunk.TransferId);
                return;
            }

            ChunkResult result;
            try
            {
                result = download.Append(chunk);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await FailAsync(download.TaskId, "Cannot write the package: " + ex.Message, null, ct).ConfigureAwait(false);
                DiscardDownload();
                return;
            }

            switch (result)
            {
                case ChunkResult.Accepted:
                    _resyncRequested = false;
                    break;
                case ChunkResult.Complete:
                    _resyncRequested = false;
                    await VerifyAndInstallAsync(download, ct).ConfigureAwait(false);
                    break;
                case ChunkResult.WrongTransfer:
                    _logger.LogDebug("Ignored a chunk of transfer {TransferId} (active: {TaskId}).", chunk.TransferId, download.TaskId);
                    break;
                case ChunkResult.OutOfOrder:
                    if (!_resyncRequested)
                    {
                        _logger.LogWarning("Update task {TaskId}: chunk at offset {ChunkOffset}, expected {Offset}; asking again.",
                            download.TaskId, chunk.Offset, download.Offset);
                        _resyncRequested = true;
                        await RequestAsync(download, ct).ConfigureAwait(false);
                    }
                    break;
                case ChunkResult.TooLarge:
                    await FailAsync(download.TaskId, $"The server sent more than the announced {download.Size} bytes.", null, ct).ConfigureAwait(false);
                    DiscardDownload();
                    break;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RequestAsync(PackageDownload download, CancellationToken ct)
    {
        var link = _link;
        if (link is null) return;
        var sent = await link.TrySendAsync(new PackageRequest { TaskId = download.TaskId, Offset = download.Offset }, ct).ConfigureAwait(false);
        _requestedOnThisLink = sent is not null;
    }

    /// <summary>Called with the gate held. Hashing a few hundred MB blocks the read loop for a moment; acceptable.</summary>
    private async Task VerifyAndInstallAsync(PackageDownload download, CancellationToken ct)
    {
        await SendStatusAsync(download.TaskId, AgentUpdateState.Verifying, null, null, ct).ConfigureAwait(false);
        var verify = await Task.Run(download.Verify, ct).ConfigureAwait(false);
        if (!verify.Ok)
        {
            _logger.LogError("Update task {TaskId}: {Error}", download.TaskId, verify.Error);
            await FailAsync(download.TaskId, verify.Error ?? "Verification failed.", null, ct).ConfigureAwait(false);
            DiscardDownload();
            return;
        }

        var logPath = Path.Combine(_paths.Updates, download.TaskId.ToString("D") + ".log");
        var marker = new InstallMarker(download.TaskId, download.Version, logPath, _time.GetUtcNow().UtcDateTime);
        try
        {
            marker.Write(_paths.InstallMarker);
        }
        catch (IOException ex)
        {
            _logger.LogWarning("Could not write the install marker: {Error}", ex.Message);
        }

        await SendStatusAsync(download.TaskId, AgentUpdateState.Installing, null, null, ct).ConfigureAwait(false);
        _logger.LogInformation("Update task {TaskId}: starting msiexec for version {Version}.", download.TaskId, download.Version);

        Process msiexec;
        try
        {
            msiexec = StartDetachedMsiexec(download.PackagePath, logPath);
        }
        catch (Exception ex)
        {
            TryDelete(_paths.InstallMarker);
            await FailAsync(download.TaskId, "Could not start msiexec: " + ex.Message, null, ct).ConfigureAwait(false);
            DiscardDownload();
            return;
        }

        _installing = true;
        var taskId = download.TaskId;
        _ = Task.Run(() => WatchInstallAsync(msiexec, taskId, logPath));
    }

    /// <summary>
    /// If msiexec ends while this service still runs, the install did not take place (on success the MSI stops
    /// the service first). Report the failure with the log tail.
    /// </summary>
    private async Task WatchInstallAsync(Process process, Guid taskId, string logPath)
    {
        int? exitCode = null;
        using (process)
        {
            try
            {
                await process.WaitForExitAsync().ConfigureAwait(false);
                exitCode = process.ExitCode;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Exit code not available; reported as unknown.
            }
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _installing = false;
            TryDelete(_paths.InstallMarker);
            if (exitCode is 0 or 3010)
            {
                _logger.LogWarning("Update task {TaskId}: msiexec finished ({ExitCode}) but this service was not replaced.", taskId, exitCode);
                await FailAsync(taskId, $"msiexec finished with code {exitCode}, but the agent still runs version {AppVersion.Current}.", MsiLogTail.Read(logPath), CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                _logger.LogError("Update task {TaskId}: msiexec failed with code {ExitCode}.", taskId, exitCode?.ToString() ?? "unknown");
                await FailAsync(taskId, $"msiexec failed with exit code {exitCode?.ToString() ?? "unknown"}.", MsiLogTail.Read(logPath), CancellationToken.None).ConfigureAwait(false);
            }
            DiscardDownload();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// A marker written by an earlier process means msiexec ran and this service was restarted: report a failure
    /// when we are not at the target version (msiexec failed or rolled back). Success is seen by the server in Hello.
    /// </summary>
    private async Task ReportPreviousInstallAsync(CancellationToken ct)
    {
        var marker = InstallMarker.TryRead(_paths.InstallMarker);
        if (marker is null || marker.StartedUtc >= _processStartedUtc) return;

        TryDelete(_paths.InstallMarker);
        if (marker.Succeeded(AppVersion.Current))
        {
            _logger.LogInformation("Update task {TaskId}: version {Version} is installed.", marker.TaskId, AppVersion.Current);
            PackageDownload.CleanFolder(_paths.Updates, null, KeepFilesFor, _time.GetUtcNow().UtcDateTime);
            return;
        }

        _logger.LogError("Update task {TaskId}: the install of version {Version} did not complete; still at {Current}.",
            marker.TaskId, marker.Version, AppVersion.Current);
        await FailAsync(marker.TaskId, $"The MSI did not install version {marker.Version}; the agent is still at {AppVersion.Current}.",
            MsiLogTail.Read(marker.LogPath), ct).ConfigureAwait(false);
    }

    private Task FailAsync(Guid taskId, string error, string? logTail, CancellationToken ct) =>
        SendStatusAsync(taskId, AgentUpdateState.Failed, error, logTail, ct);

    private async Task SendStatusAsync(Guid taskId, AgentUpdateState state, string? error, string? logTail, CancellationToken ct)
    {
        var link = _link;
        if (link is null) return;
        await link.TrySendAsync(new UpdateStatus { TaskId = taskId, State = state, Error = error, LogTail = logTail }, ct).ConfigureAwait(false);
    }

    private void DiscardDownload()
    {
        _download?.Discard();
        _download?.Dispose();
        _download = null;
    }

    /// <summary>
    /// <c>msiexec /i &lt;msi&gt; /qn /norestart /l*v &lt;log&gt;</c> as a detached process in its own process group,
    /// breaking away from any job, so stopping the service does not stop the install.
    /// </summary>
    private static Process StartDetachedMsiexec(string msiPath, string logPath)
    {
        var msiexec = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
        var commandLine = $"\"{msiexec}\" /i \"{msiPath}\" /qn /norestart /l*v \"{logPath}\"";
        var flags = NativeMethods.DetachedProcess | NativeMethods.CreateNewProcessGroup | NativeMethods.CreateNoWindow;

        if (TryCreate(msiexec, commandLine, flags | NativeMethods.CreateBreakawayFromJob, out var process, out var error)) return process!;
        if (error == NativeMethods.ErrorAccessDenied && TryCreate(msiexec, commandLine, flags, out process, out error)) return process!;
        throw new System.ComponentModel.Win32Exception(error, "CreateProcess(msiexec) failed");
    }

    private static bool TryCreate(string exe, string commandLine, uint flags, out Process? process, out int error)
    {
        var startup = new NativeMethods.StartupInfo { cb = Marshal.SizeOf<NativeMethods.StartupInfo>() };
        if (!NativeMethods.CreateProcess(exe, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false, flags,
                IntPtr.Zero, Environment.SystemDirectory, ref startup, out var info))
        {
            process = null;
            error = Marshal.GetLastWin32Error();
            return false;
        }

        try
        {
            // Open our own handle while the creation handle still pins the pid, so the exit code stays readable.
            process = Process.GetProcessById(info.dwProcessId);
            _ = process.Handle;
        }
        finally
        {
            NativeMethods.CloseHandle(info.hThread);
            NativeMethods.CloseHandle(info.hProcess);
        }
        error = 0;
        return true;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    public void Dispose()
    {
        _download?.Dispose();
        _gate.Dispose();
    }
}
