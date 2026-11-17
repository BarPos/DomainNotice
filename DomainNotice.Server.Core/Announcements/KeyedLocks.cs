namespace DomainNotice.Server.Announcements;

/// <summary>
/// Async, non-reentrant locks by key. An entry exists only while someone holds or waits for its lock (reference
/// counted), so keys taken from agent reports (user names) do not accumulate for the process lifetime.
/// </summary>
internal sealed class KeyedLocks<TKey> where TKey : notnull
{
    private readonly Lock _lock = new();
    private readonly Dictionary<TKey, Entry> _entries;

    public KeyedLocks(IEqualityComparer<TKey>? comparer = null) => _entries = new(comparer ?? EqualityComparer<TKey>.Default);

    /// <summary>Keys that currently have an entry (held or awaited). For tests.</summary>
    internal int Count
    {
        get { lock (_lock) return _entries.Count; }
    }

    public async Task<Releaser> AcquireAsync(TKey key, CancellationToken ct)
    {
        Entry entry;
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                _entries[key] = entry;
            }
            entry.References++;
        }
        try
        {
            await entry.Semaphore.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            Unreference(key, entry);
            throw;
        }
        return new Releaser(this, key, entry);
    }

    private void Unreference(TKey key, Entry entry)
    {
        lock (_lock)
        {
            if (--entry.References == 0) _entries.Remove(key);
        }
    }

    internal sealed class Entry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        /// <summary>Holders plus waiters; guarded by the owner's lock.</summary>
        public int References;
    }

    public readonly struct Releaser : IDisposable
    {
        private readonly KeyedLocks<TKey>? _owner;
        private readonly TKey _key;
        private readonly Entry? _entry;

        internal Releaser(KeyedLocks<TKey> owner, TKey key, Entry entry)
        {
            _owner = owner;
            _key = key;
            _entry = entry;
        }

        public void Dispose()
        {
            if (_owner is null || _entry is null) return;
            _entry.Semaphore.Release();
            _owner.Unreference(_key, _entry);
        }
    }
}
