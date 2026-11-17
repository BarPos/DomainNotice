using System.Net;
using DomainNotice.Server.Options;
using Microsoft.Extensions.Options;

namespace DomainNotice.Server.AgentLink;

/// <summary>
/// Caps on agent connections, checked right after accept (before the TLS handshake): at most
/// <see cref="AgentsOptions.MaxConnections"/> in total, and at most <see cref="AgentsOptions.MaxPendingConnectionsPerIp"/>
/// per remote IP that have not completed Hello yet. A connection leaves the per-IP count once Hello is accepted, so
/// many enrolled PCs behind one NAT address are not limited by it once they are online.
/// </summary>
internal sealed class ConnectionLimiter(IOptions<AgentsOptions> options)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<IPAddress, int> _pending = new();
    private int _total;

    internal int Total
    {
        get { lock (_lock) return _total; }
    }

    internal int PendingFrom(IPAddress address)
    {
        lock (_lock) return _pending.GetValueOrDefault(Normalize(address));
    }

    /// <summary>Returns null when a cap is reached (the caller closes the socket).</summary>
    public Admission? TryAdmit(IPAddress remote, out string? refusedBecause)
    {
        var o = options.Value;
        var ip = Normalize(remote);
        lock (_lock)
        {
            if (o.MaxConnections > 0 && _total >= o.MaxConnections)
            {
                refusedBecause = $"{o.MaxConnections} agent connections are open (Agents:MaxConnections)";
                return null;
            }
            var pending = _pending.GetValueOrDefault(ip);
            if (o.MaxPendingConnectionsPerIp > 0 && pending >= o.MaxPendingConnectionsPerIp)
            {
                refusedBecause = $"{pending} connections from this address have not completed Hello (Agents:MaxPendingConnectionsPerIp)";
                return null;
            }
            _total++;
            _pending[ip] = pending + 1;
        }
        refusedBecause = null;
        return new Admission(this, ip);
    }

    private void ReleasePending(IPAddress ip)
    {
        lock (_lock)
        {
            if (!_pending.TryGetValue(ip, out var n)) return;
            if (n <= 1) _pending.Remove(ip);
            else _pending[ip] = n - 1;
        }
    }

    private void ReleaseTotal()
    {
        lock (_lock) _total--;
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>One admitted connection. Dispose when the connection ends.</summary>
    internal sealed class Admission(ConnectionLimiter owner, IPAddress ip) : IDisposable
    {
        private int _pendingReleased;
        private int _disposed;

        /// <summary>Hello was accepted: the connection no longer counts against its IP's pending cap.</summary>
        public void MarkOnline()
        {
            if (Interlocked.Exchange(ref _pendingReleased, 1) == 0) owner.ReleasePending(ip);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            MarkOnline();
            owner.ReleaseTotal();
        }
    }
}
