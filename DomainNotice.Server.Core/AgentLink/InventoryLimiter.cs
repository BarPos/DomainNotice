using System.Collections.Concurrent;

namespace DomainNotice.Server.AgentLink;

/// <summary>
/// Per-computer limits on what an agent may add to the inventory within a sliding window (in memory, keyed by
/// computer id, so a reconnect does not reset them): new session user names and computer name changes. Protects the
/// history tables and the delivery table from an enrolled key that invents users or names.
/// </summary>
internal sealed class InventoryLimiter(TimeProvider time)
{
    private readonly ConcurrentDictionary<Guid, Window> _windows = new();

    private sealed class Window
    {
        /// <summary>User keys accepted as new in the window, with the time they were first accepted.</summary>
        public readonly Dictionary<string, DateTime> NewUsers = new(StringComparer.Ordinal);
        public readonly Queue<DateTime> NameChanges = new();
        public DateTime LastUserWarningUtc = DateTime.MinValue;
        public DateTime LastNameWarningUtc = DateTime.MinValue;
    }

    /// <summary>
    /// Returns true when <paramref name="userKey"/> may be accepted as a new user of this computer (and records it).
    /// A key already accepted within the window is always allowed again. <paramref name="limit"/> 0 = no limit.
    /// </summary>
    public bool TryAddUser(Guid computerId, string userKey, int limit, TimeSpan window)
    {
        if (limit <= 0) return true;
        var w = _windows.GetOrAdd(computerId, _ => new Window());
        var now = time.GetUtcNow().UtcDateTime;
        lock (w)
        {
            var cutoff = now - window;
            foreach (var old in w.NewUsers.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
                w.NewUsers.Remove(old);
            if (w.NewUsers.ContainsKey(userKey)) return true;
            if (w.NewUsers.Count >= limit) return false;
            w.NewUsers[userKey] = now;
            return true;
        }
    }

    /// <summary>Returns true when one more name change fits in the window (and records it).</summary>
    public bool TryChangeName(Guid computerId, int limit, TimeSpan window)
    {
        if (limit <= 0) return true;
        var w = _windows.GetOrAdd(computerId, _ => new Window());
        var now = time.GetUtcNow().UtcDateTime;
        lock (w)
        {
            while (w.NameChanges.Count > 0 && w.NameChanges.Peek() < now - window) w.NameChanges.Dequeue();
            if (w.NameChanges.Count >= limit) return false;
            w.NameChanges.Enqueue(now);
            return true;
        }
    }

    /// <summary>True at most once per window per computer and kind, so the warning log stays small.</summary>
    public bool ShouldWarn(Guid computerId, bool users, TimeSpan window)
    {
        var w = _windows.GetOrAdd(computerId, _ => new Window());
        var now = time.GetUtcNow().UtcDateTime;
        lock (w)
        {
            ref var last = ref users ? ref w.LastUserWarningUtc : ref w.LastNameWarningUtc;
            if (now - last < window) return false;
            last = now;
            return true;
        }
    }
}
