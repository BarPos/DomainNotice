using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading.Channels;
using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Data;
using DomainNotice.Server.Options;
using DomainNotice.Shared;
using DomainNotice.Shared.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DomainNotice.Server.AgentLink;

/// <summary>
/// Runs one agent connection: TLS with a required client certificate, identity by key fingerprint (plan 3.4),
/// enrolment (plan 4), Hello/Welcome, then the message loop. Frames are read continuously (every frame counts
/// as activity, Ping is answered at once); all other messages go through an ordered per-connection queue so a
/// slow handler never stalls reading.
/// </summary>
internal sealed class AgentProtocol(
    IServiceProvider services,
    ServerIdentity identity,
    AgentHub hub,
    ComputerInventory inventory,
    Enrolment enrolment,
    AdLinkService adLink,
    KerberosProofValidator kerberos,
    AgentObservers observers,
    IDbContextFactory<AppDbContext> dbFactory,
    IOptions<AgentsOptions> agentsOptions,
    IOptions<DirectoryOptions> directoryOptions,
    AgentLinkTimings timings,
    TimeProvider time,
    ILogger<AgentProtocol> logger)
{
    private static readonly HashSet<string> OwnTypes =
        [Enroll.Type, Hello.Type, Status.Type, Ping.Type, Pong.Type, ProveIdentity.Type];

    private Dictionary<string, IAgentMessageHandler>? _handlers;

    private Dictionary<string, IAgentMessageHandler> Handlers => _handlers ??= BuildHandlers();

    private Dictionary<string, IAgentMessageHandler> BuildHandlers()
    {
        var map = new Dictionary<string, IAgentMessageHandler>(StringComparer.Ordinal);
        foreach (var handler in services.GetServices<IAgentMessageHandler>())
            foreach (var type in handler.MessageTypes)
            {
                if (OwnTypes.Contains(type))
                {
                    logger.LogWarning("{Handler} lists {Type}, which the agent link handles itself; ignored", handler.GetType().Name, type);
                    continue;
                }
                if (!map.TryAdd(type, handler))
                    logger.LogWarning("Two handlers for {Type}: {First} and {Second}; the first wins", type, map[type].GetType().Name, handler.GetType().Name);
            }
        return map;
    }

    private enum Phase { Unknown, Known, Online }

    /// <summary>Largest frame read from a key that is not enrolled (only Enroll is allowed).</summary>
    private int EnrolReadLimit => Math.Clamp(agentsOptions.Value.MaxEnrolMessageBytes, 1024, Math.Max(1024, agentsOptions.Value.MaxMessageBytes));

    /// <summary>Largest frame read from an enrolled key.</summary>
    private int InboundReadLimit => Math.Clamp(agentsOptions.Value.MaxInboundMessageBytes, 1024, Math.Max(1024, agentsOptions.Value.MaxMessageBytes));

    public async Task RunAsync(TcpClient tcp, ConnectionLimiter.Admission? admission, CancellationToken stopping)
    {
        using var _ = admission;
        var remote = tcp.Client.RemoteEndPoint;
        AgentConnection? conn = null;
        try
        {
            tcp.NoDelay = true;
            var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(stopping))
            {
                handshake.CancelAfter(timings.HandshakeTimeout);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = identity.Certificate,
                    ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
                    AllowRenegotiation = false,
                    // No CA: any certificate is accepted here, but one is required. Trust = fingerprint lookup below.
                    RemoteCertificateValidationCallback = (_, cert, _, _) => cert is not null,
                }, handshake.Token);
            }
            if (ssl.RemoteCertificate is not { } clientCert)
            {
                await ssl.DisposeAsync();
                return;
            }
            var fingerprint = KeyFingerprint.Compute(clientCert);
            conn = new AgentConnection(tcp, ssl, agentsOptions.Value.MaxMessageBytes, fingerprint, identity.EndpointChannelBinding,
                time, logger, timings.RefuseLinger, stopping);
            conn.Frames.MaxReadBodyBytes = EnrolReadLimit;
            conn.Frames.InitialBodyBytes = Framing.InitialBodyBuffer; // untrusted peer: grow as bytes arrive
            await RunConnectionAsync(conn, admission, stopping);
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException or SocketException or ObjectDisposedException)
        {
            logger.LogDebug(ex, "Agent connection from {Remote} ended during setup", remote);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Agent connection from {Remote} failed", remote);
        }
        finally
        {
            if (conn is not null) await conn.DisposeAsync();
            else tcp.Dispose();
        }
    }

    private async Task RunConnectionAsync(AgentConnection conn, ConnectionLimiter.Admission? admission, CancellationToken stopping)
    {
        var phase = Phase.Unknown;
        await using (var db = await dbFactory.CreateDbContextAsync(stopping))
        {
            var computer = await db.Computers.AsNoTracking()
                .Where(c => c.AgentKeyFingerprint == conn.Fingerprint)
                .Select(c => new { c.Id, c.Name, c.RevokedAtUtc, c.ReplacedByComputerId })
                .FirstOrDefaultAsync(stopping);
            if (computer is not null)
            {
                conn.ComputerId = computer.Id;
                if (computer.RevokedAtUtc is not null)
                {
                    logger.LogWarning("Refused revoked computer {Computer} ({Name}) from {Remote}", computer.Id, computer.Name, conn.RemoteEndPoint);
                    await conn.CloseAsync(RefusedReasons.Revoked, "This computer was revoked.");
                }
                else if (computer.ReplacedByComputerId is not null)
                {
                    logger.LogWarning("Refused replaced computer {Computer} ({Name}) from {Remote}", computer.Id, computer.Name, conn.RemoteEndPoint);
                    await conn.CloseAsync(RefusedReasons.Replaced, "This computer record was replaced by a newer installation.");
                }
                else
                {
                    phase = Phase.Known;
                    conn.Frames.MaxReadBodyBytes = InboundReadLimit;
                }
            }
        }

        InboundQueue? queue = null;
        Task? worker = null;
        try
        {
            while (true)
            {
                IncomingFrame? frame;
                using (var read = CancellationTokenSource.CreateLinkedTokenSource(conn.Aborted))
                {
                    if (phase != Phase.Online && !conn.IsClosing) read.CancelAfter(timings.PreHelloTimeout);
                    try
                    {
                        frame = await conn.Frames.ReadAsync(read.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (ProtocolException ex)
                    {
                        var error = LogText.Clip(ex.Message, 200);
                        logger.LogInformation("Protocol error from {Conn}: {Error}", conn, error);
                        if (!conn.IsClosing) await conn.CloseAsync(RefusedReasons.ProtocolError, error);
                        break;
                    }
                    catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or AuthenticationException)
                    {
                        break;
                    }
                }
                if (frame is null) break; // peer closed
                conn.Touch();
                if (conn.IsClosing) continue; // draining until the peer closes or the linger passes

                switch (phase)
                {
                    case Phase.Unknown:
                        phase = await HandleUnknownKeyAsync(conn, frame, stopping);
                        if (phase == Phase.Known) conn.Frames.MaxReadBodyBytes = InboundReadLimit;
                        break;
                    case Phase.Known:
                        if (frame.Message is Hello hello)
                        {
                            if (await HandleHelloAsync(conn, frame.Envelope!, hello, stopping))
                            {
                                phase = Phase.Online;
                                admission?.MarkOnline();
                                queue = new InboundQueue(time, timings.StatusMinInterval);
                                var q = queue;
                                worker = Task.Run(() => WorkerAsync(conn, q), CancellationToken.None);
                                await q.WriteMarkerAsync(OnlineMarker, conn.Aborted);
                            }
                        }
                        else if (frame.Message is Enroll)
                            // Already enrolled (e.g. the agent lost the EnrollResult): answer again.
                            await conn.SendAsync(new EnrollResult { Accepted = true, ComputerId = conn.ComputerId }, frame.Envelope!.Id, stopping);
                        else if (frame.Message is Ping)
                            await conn.SendAsync(new Pong(), frame.Envelope!.Id, stopping);
                        else
                            await conn.CloseAsync(RefusedReasons.ProtocolError, "Hello expected.");
                        break;
                    case Phase.Online:
                        if (frame.Message is Ping)
                            await conn.SendAsync(new Pong(), frame.Envelope!.Id, stopping);
                        else if (frame.Message is not null)
                            await queue!.EnqueueAsync(frame, conn.Aborted);
                        // Chunks and unknown types from an agent are ignored.
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            logger.LogDebug(ex, "Agent connection {Conn} ended", conn);
        }
        finally
        {
            queue?.Complete();
            conn.Abort();
            if (worker is not null)
            {
                try { await worker; } catch (Exception ex) { logger.LogDebug(ex, "Worker of {Conn} ended with an error", conn); }
            }
            if (conn.HelloApplied)
            {
                try
                {
                    if (await inventory.MarkOfflineAsync(hub, conn, CancellationToken.None) && conn.IsOnline)
                    {
                        logger.LogInformation("Computer {Computer} offline", conn.ComputerId);
                        await observers.OfflineAsync(conn.ComputerId, CancellationToken.None);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Marking computer {Computer} offline failed", conn.ComputerId);
                }
            }
        }
    }

    private async Task<Phase> HandleUnknownKeyAsync(AgentConnection conn, IncomingFrame frame, CancellationToken ct)
    {
        if (frame.Message is not Enroll enroll)
        {
            logger.LogInformation("Unknown agent key from {Remote} sent {Type}; refused", conn.RemoteEndPoint,
                frame.Envelope is { } env ? LogText.Clip(env.Type) : "a chunk");
            await conn.CloseAsync(RefusedReasons.UnknownKey, "This agent key is not enrolled. Only Enroll is allowed.");
            return Phase.Unknown;
        }
        EnrolDecision decision;
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, conn.Aborted))
            decision = await enrolment.CheckAsync(conn.RemoteEndPoint.Address, enroll.InstallPassword, linked.Token);
        switch (decision.Outcome)
        {
            case EnrolOutcome.Busy:
                await conn.CloseAsync();
                return Phase.Unknown;
            case EnrolOutcome.RateLimited:
                logger.LogWarning("Enrolment from {Remote} rate-limited", conn.RemoteEndPoint);
                await conn.CloseAsync(RefusedReasons.RateLimited, decision.Error);
                return Phase.Unknown;
            case EnrolOutcome.Rejected:
                await conn.SendAsync(new EnrollResult { Accepted = false, Error = decision.Error }, frame.Envelope!.Id, ct);
                await conn.CloseAsync();
                return Phase.Unknown;
        }

        Guid id;
        try
        {
            id = await inventory.CreateComputerAsync(conn.Fingerprint, enroll, conn.RemoteEndPoint.Address, ct);
        }
        catch (DbUpdateException)
        {
            // Same key enrolled concurrently on another connection: use that record.
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var existing = await db.Computers.AsNoTracking().FirstOrDefaultAsync(c => c.AgentKeyFingerprint == conn.Fingerprint, ct);
            if (existing is null || existing.IsRevoked || existing.IsReplaced) throw;
            id = existing.Id;
        }
        conn.ComputerId = id;
        logger.LogInformation("Enrolled computer {Computer} ({Name}) from {Remote}, key {Fingerprint}", id, LogText.Clip(enroll.ComputerName),
            conn.RemoteEndPoint, conn.Fingerprint);
        await conn.SendAsync(new EnrollResult { Accepted = true, ComputerId = id }, frame.Envelope!.Id, ct);
        return Phase.Known;
    }

    /// <summary>Returns true when the agent is now online.</summary>
    private async Task<bool> HandleHelloAsync(AgentConnection conn, Envelope envelope, Hello hello, CancellationToken ct)
    {
        if (!AppVersion.IsAgentSupported(hello.AgentVersion))
        {
            logger.LogWarning("Computer {Computer} runs agent {Version}, which this server ({Server}) does not support", conn.ComputerId,
                LogText.Clip(hello.AgentVersion, 32), AppVersion.Current);
            await conn.SendAsync(new Welcome { Compatible = false, ServerVersion = AppVersion.Current }, envelope.Id, ct);
            await conn.CloseAsync();
            return false;
        }
        conn.AgentVersion = ComputerInventory.Clip(hello.AgentVersion, 32);
        conn.ProtocolVersion = envelope.V;

        AgentConnection? previous;
        using (await hub.LockAsync(conn.ComputerId, ct))
        {
            // Session changes found here are not reported: OnAgentOnline covers the initial Hello.
            var update = await inventory.ApplyHelloAsync(conn, hello, ct);
            if (update.RefusedReason is { } reason)
            {
                await conn.CloseAsync(reason);
                return false;
            }
            conn.HelloApplied = true;
            var spn = directoryOptions.Value.KerberosServicePrincipal;
            await conn.SendAsync(new Welcome
            {
                Compatible = true,
                ServerVersion = AppVersion.Current,
                KerberosServicePrincipal = string.IsNullOrWhiteSpace(spn) ? null : spn.Trim(),
            }, envelope.Id, ct);
            previous = hub.Register(conn);
            conn.IsOnline = true;
        }
        if (previous is not null)
        {
            logger.LogInformation("Computer {Computer}: new connection from {Remote} replaces the one from {Old}", conn.ComputerId,
                conn.RemoteEndPoint, previous.RemoteEndPoint);
            previous.Abort();
        }
        logger.LogInformation("Computer {Computer} online from {Remote}, agent {Version}", conn.ComputerId, conn.RemoteEndPoint, conn.AgentVersion);
        return true;
    }

    /// <summary>Queued first after Hello: the AD name check and OnAgentOnline run before any queued message.</summary>
    private static readonly IncomingFrame OnlineMarker = IncomingFrame.FromChunk(new PackageChunk(Guid.Empty, 0, default));

    private async Task WorkerAsync(AgentConnection conn, InboundQueue queue)
    {
        var ct = conn.Aborted;
        try
        {
            await foreach (var item in queue.ReadAllAsync(ct))
            {
                if (ct.IsCancellationRequested) break;
                var frame = item.Frame;
                try
                {
                    if (ReferenceEquals(frame, OnlineMarker))
                    {
                        await adLink.EvaluateNameAsync(conn.ComputerId, ct);
                        await observers.OnlineAsync(conn, ct);
                        continue;
                    }
                    await DispatchAsync(conn, frame.Envelope!, frame.Message!, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Handling {Type} from {Conn} failed", LogText.Clip(frame.Envelope?.Type), conn);
                }
                finally
                {
                    queue.Done(item);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Aborted while waiting for the Status interval.
        }
    }

    private async Task DispatchAsync(AgentConnection conn, Envelope envelope, IAgentMessage message, CancellationToken ct)
    {
        switch (message)
        {
            case Status status:
                await HandleStatusAsync(conn, status, ct);
                return;
            case ProveIdentity prove:
                await HandleProveIdentityAsync(conn, envelope, prove, ct);
                return;
            case Pong or Hello or Enroll:
                return;
        }
        if (Handlers.TryGetValue(envelope.Type, out var handler))
            await handler.HandleAsync(conn, envelope, message, ct);
        else
            logger.LogDebug("No handler for {Type} from {Conn}; ignored", envelope.Type, conn);
    }

    private async Task HandleStatusAsync(AgentConnection conn, Status status, CancellationToken ct)
    {
        InventoryUpdate update;
        using (await hub.LockAsync(conn.ComputerId, ct))
        {
            if (!ReferenceEquals(hub.Get(conn.ComputerId), conn)) return; // replaced meanwhile
            update = await inventory.ApplyStatusAsync(conn, status, ct);
        }
        if (update.RefusedReason is { } reason)
        {
            await conn.CloseAsync(reason);
            return;
        }
        if (update.NameChanged) await adLink.EvaluateNameAsync(conn.ComputerId, ct);
        if (update.SessionsChanged) await observers.SessionsChangedAsync(conn, update.Change, ct);
    }

    private async Task HandleProveIdentityAsync(AgentConnection conn, Envelope envelope, ProveIdentity prove, CancellationToken ct)
    {
        var result = await kerberos.ValidateAsync(prove.KerberosToken, conn.ChannelBinding, ct);
        if (!result.Ok)
        {
            logger.LogInformation("Kerberos proof of computer {Computer} refused: {Error}", conn.ComputerId, LogText.Clip(result.Error, 300));
            await conn.SendAsync(new IdentityResult { Linked = false, Error = result.Error }, envelope.Id, ct);
            return;
        }
        var (linked, obj, error) = await adLink.ApplyProofAsync(conn.ComputerId, result.ClientSamLower!, ct);
        await conn.SendAsync(new IdentityResult { Linked = linked, AdComputer = obj?.DistinguishedName, Error = error }, envelope.Id, ct);
    }
}
