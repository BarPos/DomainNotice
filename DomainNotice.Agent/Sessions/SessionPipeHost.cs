using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using DomainNotice.Agent.Logic;
using DomainNotice.Agent.Platform;
using DomainNotice.Shared.LocalIpc;
using DomainNotice.Shared.Protocol;

namespace DomainNotice.Agent.Sessions;

/// <summary>
/// The named pipe <c>DomainNotice.Agent.Session.&lt;sessionId&gt;</c> for one user session (plan 5.2).
/// ACL: SYSTEM full control, the session user read/write (no CreateNewInstance), network logons denied, owner
/// SYSTEM (the Session UI checks the owner to detect a squatted pipe). One instance at a time, created with
/// FirstPipeInstance, re-created after every disconnect. A client whose process is not in the expected session
/// is dropped.
/// </summary>
internal sealed class SessionPipeHost
{
    private readonly int _sessionId;
    private readonly string _userName;
    private readonly SecurityIdentifier _userSid;
    private readonly AnnouncementRouter _router;
    private readonly ILogger _logger;

    public SessionPipeHost(int sessionId, string userName, SecurityIdentifier userSid, AnnouncementRouter router, ILogger logger)
    {
        _sessionId = sessionId;
        _userName = userName;
        _userSid = userSid;
        _router = router;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var name = LocalPipe.NameFor(_sessionId);
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(
                    name, PipeDirection.InOut, maxNumberOfServerInstances: 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, inBufferSize: 0, outBufferSize: 0,
                    CreateSecurity(), HandleInheritability.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Someone else created the name first (squatting) or the previous instance is still closing.
                _logger.LogWarning("Session {SessionId}: cannot create pipe {Pipe}: {Error}", _sessionId, name, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                continue;
            }

            await using var stream = new LocalMessageStream(pipe);
            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                var clientSession = NativeMethods.GetPipeClientSessionId(pipe);
                if (clientSession != _sessionId)
                {
                    _logger.LogWarning("Session {SessionId}: refused a pipe client from session {ClientSession}.", _sessionId, clientSession?.ToString() ?? "unknown");
                    continue;
                }

                await ServeAsync(stream, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or ProtocolException or ObjectDisposedException)
            {
                _logger.LogInformation("Session {SessionId}: pipe closed: {Error}", _sessionId, ex.Message);
            }
        }
    }

    private async Task ServeAsync(LocalMessageStream stream, CancellationToken ct)
    {
        var endpoint = new PipeEndpoint(_sessionId, _userName, stream);
        _logger.LogInformation("Session {SessionId} ({User}): Session UI connected.", _sessionId, _userName);
        try
        {
            await _router.OnSessionConnectedAsync(endpoint, ct).ConfigureAwait(false);
            while (true)
            {
                var frame = await stream.ReadAsync(ct).ConfigureAwait(false);
                if (frame is null) break;
                if (frame.Message is { } message)
                    await _router.OnSessionMessageAsync(endpoint, message, ct).ConfigureAwait(false);
            }
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

        public Task SendAsync<T>(T message, CancellationToken ct) where T : ILocalMessage => stream.SendAsync(message, ct);
    }
}
