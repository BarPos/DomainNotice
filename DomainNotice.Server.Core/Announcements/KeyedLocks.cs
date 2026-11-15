using System.Collections.Concurrent;

namespace DomainNotice.Server.Announcements;

/// <summary>Async, non-reentrant locks by key (one SemaphoreSlim per key, kept for the process lifetime).</summary>
internal sealed class KeyedLocks<TKey> where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, SemaphoreSlim> _locks;

    public KeyedLocks(IEqualityComparer<TKey>? comparer = null) => _locks = new(comparer ?? EqualityComparer<TKey>.Default);

    public async Task<Releaser> AcquireAsync(TKey key, CancellationToken ct)
    {
        var sem = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(sem);
    }

    public readonly struct Releaser(SemaphoreSlim sem) : IDisposable
    {
        public void Dispose() => sem.Release();
    }
}
