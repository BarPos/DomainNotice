using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using DomainNotice.Agent.Logic;
using DomainNotice.Agent.Platform;
using DomainNotice.Shared.LocalIpc;
using DomainNotice.Shared.Protocol;

namespace DomainNotice.Agent.Sessions;

/// <summary>
/// The named pipe <c>DomainNotice.Agent.Session.&lt;sessionId&gt;.&lt;suffix&gt;</c> for one user session (plan 5.2). The
/// suffix is random per session and service start and reaches the Session UI only on its command line, so another account cannot
/// create the name first (squatting). ACL: SYSTEM full control, the session user read/write (no CreateNewInstance),
/// network logons denied, owner SYSTEM (the Session UI checks the owner too). The first instance is created with
/// FirstPipeInstance; after a disconnect the next instance is created before the old one is closed, so the name never
/// becomes free for someone else to take while the service runs. A client whose process is not in the expected
/// session is dropped.
/// </summary>
internal sealed class SessionPipeHost
{
    private readonly int _sessionId;
    private readonly string _userName;
    private readonly SecurityIdentifier _userSid;
    private readonly string _pipeSuffix;
    private readonly AnnouncementRouter _router;
    private readonly ILogger _logger;

    public SessionPipeHost(int sessionId, string userName, SecurityIdentifier userSid, string pipeSuffix, AnnouncementRouter router, ILogger logger)
    {
        _sessionId = sessionId;
        _userName = userName;
        _userSid = userSid;
        _pipeSuffix = pipeSuffix;
        _router = router;
        _logger = logger;
    }

    // Real buffers: with zero-size buffers a write completes only when the other side reads, and the service and the
    // Session UI both write first after connecting (status / RequestPending), which deadlocked the pipe.
    private const int PipeBufferSize = 64 * 1024;

    /// <summary>Two: the instance being closed and its replacement exist together for a moment. Only one ever listens.</summary>
    private const int MaxInstances = 2;

    /// <summary>Consecutive create failures after which they are logged as errors.</summary>
    private const int ErrorAfterFailures = 3;

    /// <summary>A Session UI that does not read for this long is disconnected (it reconnects) instead of blocking us.</summary>
    internal static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

    public async Task RunAsync(CancellationToken ct)
    {
        var name = LocalPipe.NameFor(_sessionId, _pipeSuffix);
        // The current instance and its message stream; closed after the next instance exists (or at the end).
        (NamedPipeServerStream Pipe, LocalMessageStream Stream)? previous = null;
        var failures = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // While the previous instance is still open the name stays ours, so the next one needs no
                // FirstPipeInstance (and could not get it). Without one, FirstPipeInstance refuses a squatted name.
                var keepsName = previous is { } open && !open.Pipe.SafePipeHandle.IsClosed;
                NamedPipeServerStream pipe;
                try
                {
                    pipe = NamedPipeServerStreamAcl.Create(
                        name, PipeDirection.InOut, MaxInstances, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | (keepsName ? PipeOptions.None : PipeOptions.FirstPipeInstance),
                        inBufferSize: PipeBufferSize, outBufferSize: PipeBufferSize, CreateSecurity(), HandleInheritability.None);
                    failures = 0;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Someone else holds the name (squatting), or the previous instance is still closing.
                    failures++;
                    if (failures >= ErrorAfterFailures)
                        _logger.LogError("Session {SessionId}: cannot create pipe {Pipe} ({Failures} attempts in a row): {Error}", _sessionId, name, failures, ex.Message);
                    else
                        _logger.LogWarning("Session {SessionId}: cannot create pipe {Pipe}: {Error}", _sessionId, name, ex.Message);
                    if (previous is { } stale)
                    {
                        await stale.Stream.DisposeAsync().ConfigureAwait(false);
                        previous = null;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                    continue;
                }

                if (previous is { } replaced)
                {
                    await replaced.Stream.DisposeAsync().ConfigureAwait(false);
                    previous = null;
                }

                var stream = new LocalMessageStream(pipe);
                previous = (pipe, stream);
                try
                {
                    await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    var clientSession = NativeMethods.GetPipeClientSessionId(pipe);
                    if (clientSession == _sessionId)
                        await ServeAsync(stream, ct).ConfigureAwait(false);
                    else
                        _logger.LogWarning("Session {SessionId}: refused a pipe client from session {ClientSession}.", _sessionId, clientSession?.ToString() ?? "unknown");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (ex is IOException or ProtocolException or ObjectDisposedException)
                {
                    _logger.LogInformation("Session {SessionId}: pipe closed: {Error}", _sessionId, ex.Message);
                }

                // Drop the client now; the instance itself stays open until the next one is created.
                try
                {
                    if (pipe.IsConnected) pipe.Disconnect();
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
                {
                }
            }
        }
        finally
        {
            if (previous is { } last) await last.Stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ServeAsync(LocalMessageStream stream, CancellationToken ct)
    {
        var endpoint = new PipeEndpoint(_sessionId, _userName, stream);
        _logger.LogInformation("Session {SessionId} ({User}): Session UI connected.", _sessionId, _userName);
        try
        {
            // Send the initial status and pending announcements while already reading what the Session UI sends.
            var initial = _router.OnSessionConnectedAsync(endpoint, ct);
            while (true)
            {
                var frame = await stream.ReadAsync(ct).ConfigureAwait(false);
                if (frame is null) break;
                if (frame.Message is { } message)
                    await _router.OnSessionMessageAsync(endpoint, message, ct).ConfigureAwait(false);
            }
            await initial.ConfigureAwait(false);
        }
        finally
        {
            _router.OnSessionDisconnected(endpoint);
            _logger.LogInformation("Session {SessionId}: Session UI disconnected.", _sessionId);
        }
    }

    private PipeSecurity CreateSecurity()
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(system);
        security.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(_userSid, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return security;
    }

    private sealed class PipeEndpoint(int sessionId, string userName, LocalMessageStream stream) : ISessionEndpoint
    {
        public int SessionId { get; } = sessionId;
        public string UserName { get; } = userName;

        public async Task SendAsync<T>(T message, CancellationToken ct) where T : ILocalMessage
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(SendTimeout);
            try
            {
                await stream.SendAsync(message, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The Session UI stopped reading: break the pipe so it reconnects and gets the pending items again.
                await stream.DisposeAsync().ConfigureAwait(false);
                throw new IOException($"The Session UI did not read for {SendTimeout.TotalSeconds:0} s; pipe closed.");
            }
        }
    }
}
