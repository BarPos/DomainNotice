using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using DomainNotice.Shared.LocalIpc;
using DomainNotice.Shared.Protocol;

namespace DomainNotice.Agent.Session;

/// <summary>
/// Connects to the service pipe of this session (<c>DomainNotice.Agent.Session.&lt;sessionId&gt;</c>) and keeps
/// reconnecting. The pipe must be owned by SYSTEM, which only the service can arrange; a pipe created by
/// another user-level process (squatting) is refused. Events are raised on a background thread.
/// </summary>
internal sealed class ServicePipeClient
{
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private volatile LocalMessageStream? _stream;

    public event Action<ILocalPayload>? MessageReceived;
    public event Action<bool>? ConnectionChanged;

    public bool IsConnected => _stream is not null;

    public async Task RunAsync(CancellationToken ct)
    {
        var name = LocalPipe.NameFor(System.Diagnostics.Process.GetCurrentProcess().SessionId);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Ask for read/write without FILE_CREATE_PIPE_INSTANCE, which the pipe ACL does not grant.
                await using var pipe = new NamedPipeClientStream(
                    ".", name,
                    PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
                    PipeOptions.Asynchronous,
                    TokenImpersonationLevel.Identification,
                    HandleInheritability.None);
                await pipe.ConnectAsync(5000, ct).ConfigureAwait(false);

                var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
                if (owner is null || !owner.Equals(LocalSystem))
                {
                    SessionLog.Write($"Refusing pipe {name}: owner is {owner?.Value ?? "unknown"}, not SYSTEM.");
                    await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                    continue;
                }

                var stream = new LocalMessageStream(pipe);
                _stream = stream;
                ConnectionChanged?.Invoke(true);
                // Keep reading while the request is written, so neither side waits on the other.
                var request = stream.SendAsync(new RequestPending(), ct);
                while (true)
                {
                    var frame = await stream.ReadAsync(ct).ConfigureAwait(false);
                    if (frame is null) break;
                    if (frame.Message is { } message) MessageReceived?.Invoke(message);
                }
                await request.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or ProtocolException or ObjectDisposedException or OperationCanceledException)
            {
                // Service not running (yet), restarting, or the pipe broke. Try again shortly.
            }
            finally
            {
                if (_stream is not null)
                {
                    _stream = null;
                    ConnectionChanged?.Invoke(false);
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task<bool> SendAsync<T>(T message) where T : ILocalMessage
    {
        var stream = _stream;
        if (stream is null) return false;
        try
        {
            await stream.SendAsync(message).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
    }
}
