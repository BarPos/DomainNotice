using System.Collections.Concurrent;
using System.ServiceProcess;
using DomainNotice.Agent.Logic;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;

namespace DomainNotice.Agent.Sessions;

/// <summary>
/// Session-change notifications from the SCM (logon, logoff, lock, unlock, remote connect/disconnect), plus the
/// lock state they imply. The session monitor also polls WTS, so missing events only delay an update.
/// </summary>
public sealed class SessionChangeEvents
{
    private readonly ConcurrentDictionary<int, bool> _locked = new();

    public AsyncSignal Changed { get; } = new();

    public void Raise(SessionChangeReason reason, int sessionId)
    {
        switch (reason)
        {
            case SessionChangeReason.SessionLock:
                _locked[sessionId] = true;
                break;
            case SessionChangeReason.SessionUnlock:
            case SessionChangeReason.SessionLogon:
            case SessionChangeReason.SessionLogoff:
                _locked.TryRemove(sessionId, out _);
                break;
        }
        Changed.Set();
    }

    /// <summary>Lock state known from events, or null when no event was seen for the session.</summary>
    public bool? IsLocked(int sessionId) => _locked.TryGetValue(sessionId, out var locked) ? locked : null;

    public void Forget(int sessionId) => _locked.TryRemove(sessionId, out _);
}

/// <summary>
/// Windows service lifetime that also receives session-change events (plan 5.1). Registered as IHostLifetime only
/// when the process runs as a service.
/// </summary>
public sealed class AgentServiceLifetime : WindowsServiceLifetime
{
    private readonly SessionChangeEvents _events;

    public AgentServiceLifetime(
        IHostEnvironment environment,
        IHostApplicationLifetime applicationLifetime,
        ILoggerFactory loggerFactory,
        IOptions<HostOptions> optionsAccessor,
        IOptions<WindowsServiceLifetimeOptions> windowsServiceOptionsAccessor,
        SessionChangeEvents events)
        : base(environment, applicationLifetime, loggerFactory, optionsAccessor, windowsServiceOptionsAccessor)
    {
        _events = events;
        CanHandleSessionChangeEvent = true;
        CanShutdown = true;
    }

    protected override void OnSessionChange(SessionChangeDescription changeDescription)
    {
        _events.Raise(changeDescription.Reason, changeDescription.SessionId);
        base.OnSessionChange(changeDescription);
    }
}
