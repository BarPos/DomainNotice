namespace DomainNotice.Agent.Logic;

/// <summary>
/// Decides when the Session UI of one Windows session may be started again after it exited. A process that ran
/// for a while (e.g. killed in Task Manager) is restarted within a couple of seconds; one that keeps dying right
/// after start (crash loop, or another instance holding the mutex) is restarted ever more slowly.
/// </summary>
public sealed class LaunchThrottle
{
    public static readonly TimeSpan QuickRestart = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan HealthyRun = TimeSpan.FromSeconds(30);

    private DateTime? _startedUtc;
    private DateTime _nextAllowedUtc = DateTime.MinValue;

    public int QuickExits { get; private set; }

    public bool CanStart(DateTime nowUtc) => nowUtc >= _nextAllowedUtc;

    public DateTime NextAllowedUtc => _nextAllowedUtc;

    public void Started(DateTime nowUtc) => _startedUtc = nowUtc;

    public void Exited(DateTime nowUtc)
    {
        var ran = _startedUtc is { } s ? nowUtc - s : TimeSpan.Zero;
        _startedUtc = null;
        QuickExits = ran < HealthyRun ? QuickExits + 1 : 0;
        var delay = QuickExits <= 2
            ? QuickRestart
            : TimeSpan.FromSeconds(Math.Min(MaxDelay.TotalSeconds, QuickRestart.TotalSeconds * Math.Pow(2, QuickExits - 2)));
        _nextAllowedUtc = nowUtc + delay;
    }

    /// <summary>A start attempt failed (e.g. CreateProcessAsUser error): wait like after a quick exit.</summary>
    public void StartFailed(DateTime nowUtc)
    {
        _startedUtc = nowUtc;
        Exited(nowUtc);
    }
}
