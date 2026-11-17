namespace DomainNotice.Server.Accounts;

/// <summary>
/// In-memory sliding-window limit on failed sign-ins (plan 7.1), for local and domain sign-ins. Three counters:
/// <list type="bullet">
/// <item>per (account, client IP): <see cref="MaxFailuresPerAccountAndIp"/>, the brake on guessing one password;</item>
/// <item>per client IP: <see cref="MaxFailuresPerIp"/>, across all accounts;</item>
/// <item>per account across all IPs: <see cref="MaxFailuresPerAccount"/>, much higher, so one attacking address cannot
/// lock a named account out for everyone else (it hits its own (account, IP) and IP limits long before).</item>
/// </list>
/// An attempt is reserved atomically before the password is checked (<see cref="TryBegin"/>): in-flight attempts count
/// as failures, so parallel requests cannot all pass the check before any failure is recorded. A success removes its
/// reservation and clears the account's failures (overall and from that IP, not the IP's); an attempt that verified
/// nothing (directory unreachable, cancelled) removes only its reservation. Password checks and LDAP binds also share
/// a small concurrency limit (<see cref="EnterVerificationAsync"/>). State is per process and is lost on restart,
/// which is acceptable for a brute-force brake.
/// </summary>
internal sealed class SignInRateLimiter(TimeProvider time)
{
    public const int MaxFailuresPerAccountAndIp = 5;
    /// <summary>Higher than per (account, IP), so several admins behind one NAT address are not locked out together.</summary>
    public const int MaxFailuresPerIp = 20;
    public const int MaxFailuresPerAccount = 50;
    /// <summary>Password verifications (PBKDF2) and domain binds running at the same time; more wait their turn.</summary>
    public const int MaxConcurrentVerifications = 4;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private const int SweepThreshold = 10_000;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, LinkedList<DateTime>> _failures = new(StringComparer.Ordinal);
    /// <summary>Blocking counter key → when a "rate limited" audit row was last written for it.</summary>
    private readonly Dictionary<string, DateTime> _auditedBlocks = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _verifications = new(MaxConcurrentVerifications, MaxConcurrentVerifications);

    public static string AccountKey(string kind, string normalizedName) => $"acct:{kind}:{normalizedName}";
    public static string? IpKey(string? ip) => string.IsNullOrWhiteSpace(ip) ? null : "ip:" + ip.Trim().ToLowerInvariant();
    private static string AccountIpKey(string accountKey, string? ip) => accountKey + "|from:" + (ip ?? "").Trim().ToLowerInvariant();

    /// <summary>
    /// Reserves one attempt for (account, IP), or returns null when a limit is reached; <paramref name="blockedBy"/>
    /// is then the counter that blocked (for <see cref="ShouldAuditBlock"/>).
    /// </summary>
    public Attempt? TryBegin(string accountKey, string? remoteIp, out string? blockedBy)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var accountIp = AccountIpKey(accountKey, remoteIp);
        var ip = IpKey(remoteIp);
        lock (_gate)
        {
            blockedBy = Count(accountIp, now) >= MaxFailuresPerAccountAndIp ? accountIp
                : ip is not null && Count(ip, now) >= MaxFailuresPerIp ? ip
                : Count(accountKey, now) >= MaxFailuresPerAccount ? accountKey
                : null;
            if (blockedBy is not null) return null;
            var nodes = new List<(string, LinkedListNode<DateTime>)>(3)
            {
                (accountIp, Add(accountIp, now)),
                (accountKey, Add(accountKey, now)),
            };
            if (ip is not null) nodes.Add((ip, Add(ip, now)));
            if (_failures.Count > SweepThreshold) Sweep(now);
            return new Attempt(this, accountKey, accountIp, nodes);
        }
    }

    /// <summary>True for the first block of this counter in a window: at most one "rate limited" audit row each.</summary>
    public bool ShouldAuditBlock(string? blockedBy)
    {
        if (blockedBy is null) return false;
        var now = time.GetUtcNow().UtcDateTime;
        lock (_gate)
        {
            if (_auditedBlocks.TryGetValue(blockedBy, out var at) && now - at < Window) return false;
            if (_auditedBlocks.Count > SweepThreshold)
            {
                foreach (var (k, v) in _auditedBlocks.ToList())
                    if (now - v >= Window) _auditedBlocks.Remove(k);
                if (_auditedBlocks.Count > SweepThreshold) _auditedBlocks.Clear();
            }
            _auditedBlocks[blockedBy] = now;
            return true;
        }
    }

    /// <summary>Waits (with the request's cancellation) for one of the <see cref="MaxConcurrentVerifications"/> slots.</summary>
    public async Task<IDisposable> EnterVerificationAsync(CancellationToken ct)
    {
        await _verifications.WaitAsync(ct);
        return new Releaser(_verifications);
    }

    /// <summary>Test hook: whether a new attempt would be refused now.</summary>
    internal bool IsBlocked(string accountKey, string? remoteIp)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var ip = IpKey(remoteIp);
        lock (_gate)
        {
            return Count(AccountIpKey(accountKey, remoteIp), now) >= MaxFailuresPerAccountAndIp
                   || (ip is not null && Count(ip, now) >= MaxFailuresPerIp)
                   || Count(accountKey, now) >= MaxFailuresPerAccount;
        }
    }

    // Callers hold _gate.
    private int Count(string key, DateTime now)
    {
        if (!_failures.TryGetValue(key, out var list)) return 0;
        Prune(list, now);
        return list.Count;
    }

    private LinkedListNode<DateTime> Add(string key, DateTime now)
    {
        if (!_failures.TryGetValue(key, out var list)) _failures[key] = list = new LinkedList<DateTime>();
        Prune(list, now);
        return list.AddLast(now);
    }

    private static void Prune(LinkedList<DateTime> list, DateTime now)
    {
        while (list.First is { } first && now - first.Value >= Window) list.RemoveFirst();
    }

    private void Sweep(DateTime now)
    {
        foreach (var (key, list) in _failures.ToList())
        {
            Prune(list, now);
            if (list.Count == 0) _failures.Remove(key);
        }
    }

    private void Complete(Attempt attempt, bool succeeded)
    {
        lock (_gate)
        {
            foreach (var (key, node) in attempt.Nodes)
                if (node.List is { } list)
                {
                    list.Remove(node);
                    // The list may have been dropped (a success cleared the key) and replaced by a newer one.
                    if (list.Count == 0 && _failures.TryGetValue(key, out var current) && ReferenceEquals(current, list))
                        _failures.Remove(key);
                }
            if (succeeded)
            {
                _failures.Remove(attempt.AccountKey);
                _failures.Remove(attempt.AccountIpKey);
            }
        }
    }

    /// <summary>
    /// One reserved attempt. <see cref="Failed"/> keeps it as a failure, <see cref="Succeeded"/> clears the account's
    /// failures; disposing without either (nothing verified) just removes the reservation.
    /// </summary>
    internal sealed class Attempt : IDisposable
    {
        private readonly SignInRateLimiter _owner;
        private int _done;

        internal Attempt(SignInRateLimiter owner, string accountKey, string accountIpKey, List<(string, LinkedListNode<DateTime>)> nodes)
        {
            _owner = owner;
            AccountKey = accountKey;
            AccountIpKey = accountIpKey;
            Nodes = nodes;
        }

        internal string AccountKey { get; }
        internal string AccountIpKey { get; }
        internal List<(string Key, LinkedListNode<DateTime> Node)> Nodes { get; }

        public void Failed() => Interlocked.Exchange(ref _done, 1);

        public void Succeeded()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) _owner.Complete(this, succeeded: true);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) _owner.Complete(this, succeeded: false);
        }
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) semaphore.Release();
        }
    }
}
