using System.Collections.Concurrent;

namespace DomainNotice.Server.Accounts;

/// <summary>
/// In-memory sliding-window limit on failed sign-ins (plan 7.1), per account and per client IP, for local and domain
/// sign-ins. While a key is over its limit, attempts are refused without checking the password. A successful sign-in
/// clears the account's failures (not the IP's). State is per process and is lost on restart, which is acceptable for
/// a brute-force brake.
/// </summary>
internal sealed class SignInRateLimiter(TimeProvider time)
{
    public const int MaxFailuresPerAccount = 5;
    /// <summary>Higher than per account, so several admins behind one NAT address are not locked out together.</summary>
    public const int MaxFailuresPerIp = 20;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private const int SweepThreshold = 10_000;

    private readonly ConcurrentDictionary<string, Queue<DateTime>> _failures = new(StringComparer.Ordinal);

    public static string AccountKey(string kind, string normalizedName) => $"acct:{kind}:{normalizedName}";
    public static string? IpKey(string? ip) => string.IsNullOrWhiteSpace(ip) ? null : "ip:" + ip.Trim().ToLowerInvariant();

    public bool IsBlocked(string accountKey, string? remoteIp)
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (Count(accountKey, now) >= MaxFailuresPerAccount) return true;
        return IpKey(remoteIp) is { } ipKey && Count(ipKey, now) >= MaxFailuresPerIp;
    }

    public void RecordFailure(string accountKey, string? remoteIp)
    {
        var now = time.GetUtcNow().UtcDateTime;
        Add(accountKey, now);
        if (IpKey(remoteIp) is { } ipKey) Add(ipKey, now);
        if (_failures.Count > SweepThreshold) Sweep(now);
    }

    public void ResetAccount(string accountKey) => _failures.TryRemove(accountKey, out _);

    private int Count(string key, DateTime now)
    {
        if (!_failures.TryGetValue(key, out var q)) return 0;
        lock (q)
        {
            Prune(q, now);
            return q.Count;
        }
    }

    private void Add(string key, DateTime now)
    {
        var q = _failures.GetOrAdd(key, _ => new Queue<DateTime>());
        lock (q)
        {
            Prune(q, now);
            q.Enqueue(now);
        }
    }

    private static void Prune(Queue<DateTime> q, DateTime now)
    {
        while (q.Count > 0 && now - q.Peek() >= Window) q.Dequeue();
    }

    private void Sweep(DateTime now)
    {
        foreach (var (key, q) in _failures)
        {
            bool empty;
            lock (q)
            {
                Prune(q, now);
                empty = q.Count == 0;
            }
            if (empty) _failures.TryRemove(key, out _);
        }
    }
}
