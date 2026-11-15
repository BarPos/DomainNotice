using System.Diagnostics;
using DomainNotice.Agent.Logic;
using DomainNotice.Shared.Protocol;

namespace DomainNotice.Agent.Sessions;

public interface ISessionsSource
{
    /// <summary>Current user sessions, as sent in Hello/Status.</summary>
    IReadOnlyList<SessionInfo> Current { get; }
}

/// <summary>Raised whenever something that goes into Status changed (sessions, IPs). The server link sends Status.</summary>
public sealed class StatusTrigger
{
    public AsyncSignal Signal { get; } = new();
    public void Set() => Signal.Set();
}

/// <summary>
/// Follows the user sessions (WTS polling every few seconds plus SCM session-change events), keeps one Session UI
/// process and one pipe per session, restarts a Session UI that died within a few seconds, and stops on logoff.
/// </summary>
public sealed class SessionManager : BackgroundService, ISessionsSource
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private readonly ILogger<SessionManager> _logger;
    private readonly AnnouncementRouter _router;
    private readonly SessionChangeEvents _events;
    private readonly StatusTrigger _status;
    private readonly TimeProvider _time;
    private readonly Dictionary<int, SessionWorker> _workers = [];
    private volatile IReadOnlyList<SessionInfo> _current = [];

    public SessionManager(ILogger<SessionManager> logger, AnnouncementRouter router, SessionChangeEvents events, StatusTrigger status, TimeProvider time)
    {
        _logger = logger;
        _router = router;
        _events = events;
        _status = status;
        _time = time;
    }

    public IReadOnlyList<SessionInfo> Current => _current;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var sessionEvent = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Refresh(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Reading user sessions failed.");
            }

            // Every SCM session event (logon, logoff, lock, unlock, remote connect/disconnect) also sends Status,
            // even when the session list looks the same afterwards.
            if (sessionEvent) _status.Set();

            try
            {
                sessionEvent = await _events.Changed.WaitAsync(PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void Refresh(CancellationToken stoppingToken)
    {
        var previous = _current;
        var sessions = WtsSessions.Enumerate(_events);
        var diff = SessionSnapshot.Diff(previous, sessions);
        _current = sessions;

        if (!diff.IsEmpty)
        {
            foreach (var s in diff.Added) _logger.LogInformation("Session {SessionId}: {User} logged on ({State}).", s.SessionId, s.UserName, s.State);
            foreach (var s in diff.Removed) _logger.LogInformation("Session {SessionId}: {User} logged off.", s.SessionId, s.UserName);
            foreach (var s in diff.StateChanged) _logger.LogInformation("Session {SessionId}: {User} is now {State}.", s.SessionId, s.UserName, s.State);
            _status.Set();
        }

        foreach (var removed in diff.Removed)
        {
            if (_workers.Remove(removed.SessionId, out var worker)) worker.Stop(killUi: false);
            _events.Forget(removed.SessionId);
        }

        foreach (var user in SessionSnapshot.UsersGone(previous, sessions))
        {
            var dropped = _router.OnUserLoggedOff(user);
            if (dropped > 0) _logger.LogInformation("Dropped {Count} pending announcement(s) of {User} after logoff.", dropped, user);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var session in sessions)
        {
            if (!_workers.TryGetValue(session.SessionId, out var worker))
            {
                var sid = WtsSessions.QueryUserSid(session.SessionId);
                if (sid is null)
                {
                    _logger.LogDebug("Session {SessionId}: no user token yet.", session.SessionId);
                    continue;
                }

                worker = new SessionWorker(session.SessionId, session.UserName, new SessionPipeHost(session.SessionId, session.UserName, sid, _router, _logger), _logger, stoppingToken);
                _workers[session.SessionId] = worker;
            }

            worker.EnsureUiRunning(now);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // First ask every Session UI to exit (it closes its windows and removes its tray icon cleanly), give them a
        // moment, then kill whatever is left. The service starts them again in every session when it comes back.
        try
        {
            using var notify = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            notify.CancelAfter(TimeSpan.FromSeconds(2));
            await _router.NotifyServiceStoppingAsync(notify.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
        WaitForSessionUisToExit(TimeSpan.FromSeconds(3));

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        foreach (var worker in _workers.Values) worker.Stop(killUi: true);
        _workers.Clear();

        // Also stop instances this process did not start (adopted ones, or leftovers), so an MSI upgrade can
        // replace the files. The service starts them again when it comes back.
        foreach (var process in SessionUiLauncher.FindAll())
        {
            using (process)
            {
                try
                {
                    process.Kill();
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
        }
    }

    private static void WaitForSessionUisToExit(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        foreach (var process in SessionUiLauncher.FindAll())
        {
            using (process)
            {
                try
                {
                    var left = deadline - DateTime.UtcNow;
                    if (left > TimeSpan.Zero) process.WaitForExit(left);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
        }
    }

    private sealed class SessionWorker
    {
        private readonly int _sessionId;
        private readonly string _userName;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _cts;
        private readonly LaunchThrottle _throttle = new();
        private Process? _process;

        public SessionWorker(int sessionId, string userName, SessionPipeHost pipe, ILogger logger, CancellationToken stoppingToken)
        {
            _sessionId = sessionId;
            _userName = userName;
            _logger = logger;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _ = Task.Run(async () =>
            {
                try
                {
                    await pipe.RunAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Session {SessionId}: pipe host failed.", _sessionId);
                }
            });
        }

        public void EnsureUiRunning(DateTime nowUtc)
        {
            if (_process is not null)
            {
                bool exited;
                try
                {
                    exited = _process.HasExited;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    exited = true;
                }

                if (!exited) return;
                _logger.LogWarning("Session {SessionId}: Session UI exited; it will be restarted.", _sessionId);
                _process.Dispose();
                _process = null;
                _throttle.Exited(nowUtc);
            }

            var running = SessionUiLauncher.FindRunning(_sessionId);
            if (running is not null)
            {
                _logger.LogInformation("Session {SessionId}: adopted running Session UI (pid {Pid}).", _sessionId, running.Id);
                _process = running;
                _throttle.Started(nowUtc);
                return;
            }

            if (!_throttle.CanStart(nowUtc)) return;
            try
            {
                _process = SessionUiLauncher.Start(_sessionId);
                _throttle.Started(nowUtc);
                _logger.LogInformation("Session {SessionId} ({User}): started Session UI (pid {Pid}).", _sessionId, _userName, _process.Id);
            }
            catch (Exception ex)
            {
                _throttle.StartFailed(nowUtc);
                _logger.LogWarning("Session {SessionId}: starting the Session UI failed: {Error}", _sessionId, ex.Message);
            }
        }

        public void Stop(bool killUi)
        {
            _cts.Cancel();
            if (_process is { } process)
            {
                if (killUi)
                {
                    try
                    {
                        if (!process.HasExited) process.Kill();
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                    }
                }
                process.Dispose();
                _process = null;
            }
            // _cts is not disposed: the pipe task may still observe its token while it unwinds.
        }
    }
}
