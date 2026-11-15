namespace DomainNotice.Agent.Logic;

/// <summary>
/// Exponential reconnect backoff with jitter. The delay doubles per attempt up to <c>max</c>; jitter only
/// shortens it (by up to <c>jitter</c> of the value), so the delay never exceeds <c>max</c> and many agents
/// that lost the server at the same moment do not reconnect in lockstep.
/// </summary>
public sealed class ExponentialBackoff
{
    private readonly TimeSpan _initial;
    private readonly TimeSpan _max;
    private readonly double _jitter;
    private readonly Random _random;

    public ExponentialBackoff(TimeSpan initial, TimeSpan max, double jitter = 0.25, Random? random = null)
    {
        if (initial <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(initial));
        if (max < initial) throw new ArgumentOutOfRangeException(nameof(max));
        if (jitter is < 0 or >= 1) throw new ArgumentOutOfRangeException(nameof(jitter));
        _initial = initial;
        _max = max;
        _jitter = jitter;
        _random = random ?? Random.Shared;
    }

    public int Attempts { get; private set; }

    /// <summary>The delay before the next attempt; advances the attempt counter.</summary>
    public TimeSpan Next()
    {
        var exponent = Math.Min(Attempts, 30);
        var raw = _initial.TotalMilliseconds * Math.Pow(2, exponent);
        var capped = Math.Min(raw, _max.TotalMilliseconds);
        Attempts++;
        var factor = 1.0 - (_jitter * _random.NextDouble());
        return TimeSpan.FromMilliseconds(capped * factor);
    }

    public void Reset() => Attempts = 0;
}
