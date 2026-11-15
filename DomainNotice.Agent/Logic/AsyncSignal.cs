namespace DomainNotice.Agent.Logic;

/// <summary>Auto-reset signal: many <see cref="Set"/> calls before a wait collapse into one wake-up.</summary>
public sealed class AsyncSignal
{
    private readonly SemaphoreSlim _semaphore = new(0, 1);

    public void Set()
    {
        try
        {
            if (_semaphore.CurrentCount == 0) _semaphore.Release();
        }
        catch (SemaphoreFullException)
        {
            // Another Set won the race; the signal is already set.
        }
    }

    /// <summary>True when the signal was set, false on timeout.</summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _semaphore.WaitAsync(timeout, ct);
}

/// <summary>Tracks when the last frame arrived on a link, to drop it after a period of silence.</summary>
public sealed class KeepaliveMonitor
{
    private readonly TimeProvider _time;
    private long _lastReceived;

    public KeepaliveMonitor(TimeProvider time, TimeSpan silenceLimit)
    {
        _time = time;
        SilenceLimit = silenceLimit;
        _lastReceived = time.GetTimestamp();
    }

    public TimeSpan SilenceLimit { get; }

    public void MarkReceived() => Interlocked.Exchange(ref _lastReceived, _time.GetTimestamp());

    public TimeSpan SinceLastReceived => _time.GetElapsedTime(Interlocked.Read(ref _lastReceived));

    public bool IsSilentTooLong => SinceLastReceived > SilenceLimit;
}
