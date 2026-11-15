using System.Collections.Concurrent;
using DomainNotice.Server.Abstractions;
using DomainNotice.Shared.Protocol;
using Microsoft.Extensions.Logging;

namespace DomainNotice.Server.AgentLink;

/// <summary>
/// Registry of online agents (Hello accepted). One connection per computer: a newer connection replaces the
/// older one. Also owns the per-computer lock that serialises inventory writes (Hello, Status, offline).
/// </summary>
internal sealed class AgentHub(ILogger<AgentHub> logger) : IAgentHub
{
    private readonly ConcurrentDictionary<Guid, AgentConnection> _online = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public bool IsOnline(Guid computerId) => _online.ContainsKey(computerId);

    public IReadOnlyCollection<Guid> OnlineComputerIds => _online.Keys.ToArray();

    public IAgentConnection? Get(Guid computerId) => _online.GetValueOrDefault(computerId);

    internal IReadOnlyCollection<AgentConnection> Connections => _online.Values.ToArray();

    public async Task<bool> TrySendAsync<T>(Guid computerId, T message, CancellationToken ct = default) where T : IAgentMessage
    {
        if (!_online.TryGetValue(computerId, out var conn) || conn.IsClosing) return false;
        try
        {
            await conn.SendAsync(message, null, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Send of {Type} to {Computer} failed", T.Type, computerId);
            return false;
        }
    }

    public async Task DisconnectAsync(Guid computerId, string refusedReason, string? message = null)
    {
        if (_online.TryGetValue(computerId, out var conn))
            await conn.CloseAsync(refusedReason, message).ConfigureAwait(false);
    }

    /// <summary>Serialises inventory writes for one computer.</summary>
    internal async Task<IDisposable> LockAsync(Guid computerId, CancellationToken ct)
    {
        var sem = _locks.GetOrAdd(computerId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(sem);
    }

    /// <summary>Registers the connection; returns the one it replaced, if any. Call under <see cref="LockAsync"/>.</summary>
    internal AgentConnection? Register(AgentConnection connection)
    {
        AgentConnection? previous = null;
        _online.AddOrUpdate(connection.ComputerId, connection, (_, old) =>
        {
            previous = old;
            return connection;
        });
        return ReferenceEquals(previous, connection) ? null : previous;
    }

    /// <summary>Removes the connection only if it is still the registered one. Call under <see cref="LockAsync"/>.</summary>
    internal bool TryRemove(AgentConnection connection) =>
        _online.TryRemove(new KeyValuePair<Guid, AgentConnection>(connection.ComputerId, connection));

    private sealed class Releaser(SemaphoreSlim sem) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) sem.Release();
        }
    }
}
