using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using DomainNotice.Server.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DomainNotice.Server.AgentLink;

/// <summary>
/// TCP listener for the agent link (plan 3.1) on Agents:ListenAddress / Agents:Port. Binds in StartAsync (after
/// crash recovery), so <see cref="AgentListenerEndpoint.Port"/> is known once the host has started (Port 0 works).
/// </summary>
internal sealed class AgentListener(
    AgentProtocol protocol,
    ComputerInventory inventory,
    AgentListenerEndpoint endpoint,
    ServerIdentity identity,
    ConnectionLimiter limiter,
    IOptions<AgentsOptions> options,
    TimeProvider time,
    ILogger<AgentListener> logger) : IHostedService
{
    /// <summary>Pause after a failed accept (e.g. out of file descriptors), so the loop does not spin.</summary>
    internal static readonly TimeSpan AcceptErrorDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan CapWarningInterval = TimeSpan.FromMinutes(1);

    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<Task, byte> _connections = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private DateTime _lastCapWarningUtc;
    private int _refusedSinceWarning;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _ = identity.Certificate; // create or load the key before accepting anything
        await inventory.RecoverAfterRestartAsync(cancellationToken);

        var o = options.Value;
        var address = string.IsNullOrWhiteSpace(o.ListenAddress) ? IPAddress.Any : IPAddress.Parse(o.ListenAddress.Trim());
        var listener = new TcpListener(address, o.Port);
        if (address.Equals(IPAddress.IPv6Any)) listener.Server.DualMode = true;
        listener.Start(512);
        _listener = listener;
        var bound = (IPEndPoint)listener.LocalEndpoint;
        endpoint.SetBound(bound);
        logger.LogInformation("Agent listener on {Endpoint} (TLS, key fingerprint {Fingerprint})", bound, identity.Fingerprint);
        _acceptLoop = Task.Run(() => AcceptLoopAsync(listener), CancellationToken.None);
    }

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(_stopping.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                logger.LogWarning(ex, "Accepting an agent connection failed");
                try { await Task.Delay(AcceptErrorDelay, _stopping.Token); }
                catch (OperationCanceledException) { break; }
                continue;
            }
            if (Admit(client) is not { } admission) continue;
            var task = Task.Run(() => protocol.RunAsync(client, admission, _stopping.Token), CancellationToken.None);
            _connections.TryAdd(task, 0);
            _ = task.ContinueWith(t => _connections.TryRemove(t, out _), TaskScheduler.Default);
        }
    }

    /// <summary>Applies the connection caps; an over-cap socket is closed at once, before any TLS work.</summary>
    private ConnectionLimiter.Admission? Admit(TcpClient client)
    {
        IPAddress? remote = null;
        try
        {
            remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
        }
        if (remote is null)
        {
            client.Dispose();
            return null;
        }
        var admission = limiter.TryAdmit(remote, out var reason);
        if (admission is not null) return admission;
        try
        {
            client.Client.LingerState = new LingerOption(true, 0); // reset: no TIME_WAIT for refused sockets
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
        }
        client.Dispose();
        // One warning per minute at most, with the number refused since the last one.
        var now = time.GetUtcNow().UtcDateTime;
        var refused = Interlocked.Increment(ref _refusedSinceWarning);
        if (now - _lastCapWarningUtc >= CapWarningInterval)
        {
            _lastCapWarningUtc = now;
            Interlocked.Exchange(ref _refusedSinceWarning, 0);
            logger.LogWarning("Agent connection from {Remote} closed: {Reason} ({Count} refused in the last minute)",
                remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote, reason, refused);
        }
        return null;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        _listener?.Stop();
        var pending = _connections.Keys.ToList();
        if (_acceptLoop is not null) pending.Add(_acceptLoop);
        try
        {
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            logger.LogWarning("Agent connections did not close in time");
        }
    }
}
