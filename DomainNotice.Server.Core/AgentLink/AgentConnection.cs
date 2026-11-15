using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using DomainNotice.Server.Abstractions;
using DomainNotice.Shared;
using DomainNotice.Shared.Protocol;
using Microsoft.Extensions.Logging;

namespace DomainNotice.Server.AgentLink;

/// <summary>
/// One TLS connection from an agent. <see cref="ComputerId"/> is set only from the client key fingerprint
/// (plan 3.4), never from a payload.
/// </summary>
internal sealed class AgentConnection : IAgentConnection, IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private readonly SslStream _ssl;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly TimeSpan _linger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts;
    private long _lastActivityTicks;
    private int _closing;

    public AgentConnection(TcpClient tcp, SslStream ssl, int maxMessageBytes, string fingerprint, byte[]? channelBinding,
        TimeProvider time, ILogger logger, TimeSpan linger, CancellationToken stopping)
    {
        _tcp = tcp;
        _ssl = ssl;
        _time = time;
        _logger = logger;
        _linger = linger;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        Frames = new FrameConnection(ssl, maxMessageBytes);
        Fingerprint = fingerprint;
        ChannelBinding = channelBinding;
        var remote = (IPEndPoint)tcp.Client.RemoteEndPoint!;
        RemoteEndPoint = remote.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(remote.Address.MapToIPv4(), remote.Port) : remote;
        ConnectedAtUtc = time.GetUtcNow().UtcDateTime;
        Touch();
    }

    public FrameConnection Frames { get; }
    public string Fingerprint { get; }
    public Guid ComputerId { get; internal set; }
    public string AgentVersion { get; internal set; } = "";
    public int ProtocolVersion { get; internal set; }
    public IPEndPoint RemoteEndPoint { get; }
    /// <summary>RFC 5929 tls-server-end-point application data: "tls-server-end-point:" + certificate hash.</summary>
    public byte[]? ChannelBinding { get; }
    public DateTime ConnectedAtUtc { get; }
    public IReadOnlyList<SessionInfo> Sessions { get; internal set; } = [];

    /// <summary>Hello accepted; registered in the hub.</summary>
    public bool IsOnline { get; internal set; }
    /// <summary>Hello was written to the inventory (Online = true), even if Welcome or registration then failed.</summary>
    public bool HelloApplied { get; internal set; }
    public bool IsClosing => Volatile.Read(ref _closing) != 0;
    public CancellationToken Aborted => _cts.Token;
    public DateTime LastActivityUtc => new(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);
    /// <summary>LastSeenAtUtc last written to the database (sweeper flush).</summary>
    public DateTime LastSeenPersistedUtc { get; set; }

    public void Touch() => Interlocked.Exchange(ref _lastActivityTicks, _time.GetUtcNow().UtcDateTime.Ticks);

    public async Task SendAsync<T>(T message, Guid? replyTo = null, CancellationToken ct = default) where T : IAgentMessage
    {
        if (IsClosing) throw new InvalidOperationException("The agent connection is closing.");
        await SendCoreAsync(message, replyTo, ct).ConfigureAwait(false);
    }

    private async Task SendCoreAsync<T>(T message, Guid? replyTo, CancellationToken ct) where T : IAgentMessage
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        await _sendLock.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            await Frames.SendAsync(message, replyTo, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task SendChunkAsync(Guid transferId, long offset, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (IsClosing) throw new InvalidOperationException("The agent connection is closing.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        await _sendLock.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            await Frames.SendChunkAsync(transferId, offset, data, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Sends Refused (when a reason is given), shuts TLS down and half-closes the socket. The read loop keeps
    /// draining until the peer closes or <c>RefuseLinger</c> passes, so the Refused frame is not lost to a reset.
    /// </summary>
    public async Task CloseAsync(string? refusedReason = null, string? message = null)
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (refusedReason is not null)
                await SendCoreAsync(new Refused { Reason = refusedReason, Message = message }, null, timeout.Token).ConfigureAwait(false);
            await _sendLock.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                await _ssl.ShutdownAsync().WaitAsync(timeout.Token).ConfigureAwait(false);
                _tcp.Client.Shutdown(SocketShutdown.Send);
            }
            finally
            {
                _sendLock.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Closing agent connection from {Remote} did not complete cleanly", RemoteEndPoint);
        }
        finally
        {
            try { _cts.CancelAfter(_linger); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>Aborts at once (no Refused, no linger).</summary>
    public void Abort()
    {
        Interlocked.Exchange(ref _closing, 1);
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _closing, 1);
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        try { await Frames.DisposeAsync().ConfigureAwait(false); } catch { /* already broken */ }
        _tcp.Dispose();
        _cts.Dispose();
    }

    public override string ToString() => $"{ComputerId} ({RemoteEndPoint})";
}
