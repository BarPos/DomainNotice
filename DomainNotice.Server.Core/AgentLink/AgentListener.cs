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
    IOptions<AgentsOptions> options,
    ILogger<AgentListener> logger) : IHostedService
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<Task, byte> _connections = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;

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
                continue;
            }
            var task = Task.Run(() => protocol.RunAsync(client, _stopping.Token), CancellationToken.None);
            _connections.TryAdd(task, 0);
            _ = task.ContinueWith(t => _connections.TryRemove(t, out _), TaskScheduler.Default);
        }
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
