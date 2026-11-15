using System.Threading.Channels;
using NetworkChange = System.Net.NetworkInformation.NetworkChange;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using DomainNotice.Agent.Logic;
using DomainNotice.Agent.Platform;
using DomainNotice.Agent.Sessions;
using DomainNotice.Agent.Updates;
using DomainNotice.Shared;
using DomainNotice.Shared.LocalIpc;
using DomainNotice.Shared.Protocol;

namespace DomainNotice.Agent.Link;

/// <summary>
/// The long-lived outbound link to the server (plan 3): TCP → TLS 1.2+ with the agent's client certificate and the
/// server key pinned by fingerprint (no CA) → Enroll (first time) → Hello/Welcome → messages, keepalive, Status.
/// Reconnects with exponential backoff and jitter.
/// </summary>
public sealed class ServerLinkService : BackgroundService, IServerSender
{
    public const string ServerOlderMessage = LocalStatusMessages.ServerOlder;

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SlowRetry = TimeSpan.FromHours(1);
    private static readonly TimeSpan NotConfiguredRetry = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan EnrolRejectedRetry = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ReEnrolDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan EventLogRepeat = TimeSpan.FromHours(1);

    private readonly ILogger<ServerLinkService> _logger;
    private readonly ILogger _events;
    private readonly AgentSettingsStore _settings;
    private readonly AnnouncementRouter _router;
    private readonly ISessionsSource _sessions;
    private readonly StatusTrigger _statusTrigger;
    private readonly AgentUpdater _updater;
    private readonly TimeProvider _time;
    private readonly ExponentialBackoff _backoff = new(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5));

    private AgentIdentity? _identity;
    private bool _reKeyPending;
    private volatile FrameConnection? _link;
    private string _downMessage = "Connecting to the server";
    private TimeSpan? _delayOverride;
    private string? _lastEventError;
    private DateTime _lastEventErrorUtc;

    public ServerLinkService(
        ILogger<ServerLinkService> logger,
        ILoggerFactory loggerFactory,
        AgentSettingsStore settings,
        AnnouncementRouter router,
        ISessionsSource sessions,
        StatusTrigger statusTrigger,
        AgentUpdater updater,
        TimeProvider time)
    {
        _logger = logger;
        _events = loggerFactory.CreateLogger(AgentConstants.ServiceEventsCategory);
        _settings = settings;
        _router = router;
        _sessions = sessions;
        _statusTrigger = statusTrigger;
        _updater = updater;
        _time = time;
        _router.Log = message => _logger.LogInformation("{Message}", message);
    }

    /// <summary>Fingerprint of this agent's key, once loaded.</summary>
    public string? AgentFingerprint => _identity?.Fingerprint;

    public async Task<Guid?> TrySendAsync<T>(T message, CancellationToken ct) where T : IAgentMessage
    {
        var link = _link;
        if (link is null) return null;
        try
        {
            return await link.SendAsync(message, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or AuthenticationException)
        {
            _logger.LogDebug("Sending {Type} failed: {Error}", T.Type, ex.Message);
            return null;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TimeSpan delay;
                try
                {
                    delay = await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    ReportConnectionError(DescribeError(ex));
                    _downMessage = "Cannot reach the server";
                    delay = _backoff.Next();
                }

                _link = null;
                try
                {
                    var host = SafeLoad().ServerHost ?? "";
                    await _router.SetLinkAsync(null, new ConnectionStatus { Connected = false, ServerName = host, Message = _downMessage }, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                _logger.LogInformation("Next connection attempt in {Delay:g}.", delay);
                try
                {
                    await Task.Delay(delay, _time, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e) => _statusTrigger.Set();

    private AgentSettings SafeLoad()
    {
        try
        {
            return _settings.Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _logger.LogError("Reading the agent settings failed: {Error}", ex.Message);
            return new AgentSettings(null, ProtocolInfo.DefaultPort, null, null);
        }
    }

    /// <summary>One connection attempt and, if it succeeds, the whole connected period. Returns the wait before the next attempt.</summary>
    private async Task<TimeSpan> RunOnceAsync(CancellationToken stop)
    {
        _delayOverride = null;
        var settings = SafeLoad();
        if (!settings.IsConfigured)
        {
            _downMessage = "Not configured";
            ReportConnectionError("The server address or fingerprint is not configured (HKLM\\Software\\DomainNotice\\Agent).");
            return NotConfiguredRetry;
        }

        if (_reKeyPending)
        {
            ApplyReKey();
            settings = SafeLoad();
        }

        if (_identity is null)
        {
            try
            {
                _settings.ProtectInstallPassword();
            }
            catch (Exception ex)
            {
                _logger.LogError("Protecting the install password failed: {Error}", ex.Message);
            }

            if (_settings.ReenrolRequested())
            {
                _events.LogWarning("Re-enrolment requested by an administrator: creating a new agent key.");
                ApplyReKey();
                settings = SafeLoad();
            }

            _identity = AgentIdentity.LoadOrCreate(_settings);
            _events.LogInformation("Agent key {Action}; fingerprint {Fingerprint}.", _identity.KeyCreated ? "created" : "loaded", KeyFingerprint.Display(_identity.Fingerprint));
        }

        var host = settings.ServerHost!;
        _downMessage = "Cannot reach the server";
        using var tcp = new TcpClient { NoDelay = true };
        using (var connectCts = Timeout(stop, ConnectTimeout))
            await tcp.ConnectAsync(host, settings.ServerPort, connectCts.Token).ConfigureAwait(false);
        tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

        var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
        await using var connection = new FrameConnection(ssl, ProtocolInfo.DefaultMaxFrameBytes);

        string? presented = null;
        var certificate = _identity.Certificate;
        var options = new SslClientAuthenticationOptions
        {
            TargetHost = host,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            ClientCertificates = new X509CertificateCollection { certificate },
            LocalCertificateSelectionCallback = (_, _, _, _, _) => certificate,
            // Pinning only: the server certificate is accepted when its public key has the configured fingerprint.
            RemoteCertificateValidationCallback = (_, cert, _, _) =>
            {
                if (cert is null) return false;
                presented = KeyFingerprint.Compute(cert);
                return KeyFingerprint.Matches(settings.ServerFingerprint, cert);
            },
        };

        try
        {
            using var handshakeCts = Timeout(stop, HandshakeTimeout);
            await ssl.AuthenticateAsClientAsync(options, handshakeCts.Token).ConfigureAwait(false);
        }
        catch (AuthenticationException) when (presented is not null && presented != KeyFingerprint.Normalize(settings.ServerFingerprint))
        {
            _downMessage = "The server's key does not match";
            ReportConnectionError($"Refusing server {host}: its key fingerprint is {KeyFingerprint.Display(presented)}, " +
                                  $"expected {KeyFingerprint.Display(KeyFingerprint.Normalize(settings.ServerFingerprint) ?? "?")}.");
            return _backoff.Next();
        }

        // ---- enrolment (first contact only) ----
        if (settings.ComputerId is null)
        {
            var enrolDelay = await EnrolAsync(connection, settings, stop).ConfigureAwait(false);
            if (enrolDelay is { } d) return d;
        }

        // ---- Hello / Welcome ----
        await connection.SendAsync(BuildHello(), null, stop).ConfigureAwait(false);
        var reply = await ReadReplyAsync(connection, [Welcome.Type, Refused.Type], stop).ConfigureAwait(false);
        switch (reply?.Message)
        {
            case Refused refused:
                return HandleRefused(refused, settings);
            case Welcome { Compatible: false } welcome:
                _downMessage = ServerOlderMessage;
                ReportConnectionError($"Server {host} (version {welcome.ServerVersion}) is older than this agent ({AppVersion.Current}); retrying in an hour.");
                await _router.SetStatusAsync(new ConnectionStatus { Connected = false, ServerName = host, Message = ServerOlderMessage }, stop).ConfigureAwait(false);
                return SlowRetry;
            case Welcome welcome:
                await RunConnectedAsync(connection, ssl, host, settings.ServerPort, welcome, stop).ConfigureAwait(false);
                return _delayOverride ?? _backoff.Next();
            default:
                _downMessage = "The server closed the connection";
                ReportConnectionError($"Server {host} closed the connection during Hello.");
                return _backoff.Next();
        }
    }

    /// <summary>Returns a delay when the attempt ends here, or null to continue with Hello on the same connection.</summary>
    private async Task<TimeSpan?> EnrolAsync(FrameConnection connection, AgentSettings settings, CancellationToken stop)
    {
        string? password = null;
        try
        {
            password = _settings.ReadInstallPassword();
        }
        catch (Exception ex)
        {
            _logger.LogError("Reading the install password failed: {Error}", ex.Message);
        }

        _logger.LogInformation("Not enrolled yet; sending Enroll ({WithPassword}).", password is null ? "without install password" : "with install password");
        await connection.SendAsync(new Enroll
        {
            InstallPassword = password,
            ComputerName = MachineInfo.ComputerName,
            DnsName = MachineInfo.DnsName,
            OsVersion = MachineInfo.OsVersion,
            AgentVersion = AppVersion.Current,
        }, null, stop).ConfigureAwait(false);

        var reply = await ReadReplyAsync(connection, [EnrollResult.Type, Refused.Type], stop).ConfigureAwait(false);
        switch (reply?.Message)
        {
            case EnrollResult { Accepted: true } accepted:
                if (accepted.ComputerId is { } id && id != Guid.Empty) _settings.SaveComputerId(id);
                else _settings.SaveComputerId(Guid.NewGuid()); // marks "enrolled"; the server identifies us by key anyway
                _settings.DeleteInstallPassword();
                _events.LogInformation("Enrolled with server {Host} as computer {ComputerId}.", settings.ServerHost, accepted.ComputerId);
                return null;
            case EnrollResult rejected:
                _downMessage = "Enrolment was refused by the server";
                ReportConnectionError($"Enrolment refused by {settings.ServerHost}: {rejected.Error ?? "no reason given"}.");
                return password is null ? SlowRetry : EnrolRejectedRetry;
            case Refused refused:
                return HandleRefused(refused, settings);
            default:
                _downMessage = "The server closed the connection";
                ReportConnectionError($"Server {settings.ServerHost} closed the connection during enrolment.");
                return _backoff.Next();
        }
    }

    private TimeSpan HandleRefused(Refused refused, AgentSettings settings)
    {
        var detail = string.IsNullOrWhiteSpace(refused.Message) ? "" : $" ({refused.Message})";
        switch (refused.Reason)
        {
            case RefusedReasons.UnknownKey when settings.ComputerId is not null:
                // The server does not know our key (record deleted, or a new server): enrol again.
                _settings.SaveComputerId(null);
                _downMessage = "Enrolling again";
                ReportConnectionError($"Server {settings.ServerHost} does not know this agent's key{detail}; enrolling again.");
                return ReEnrolDelay;
            case RefusedReasons.UnknownKey:
                _downMessage = "Not enrolled";
                ReportConnectionError($"Server {settings.ServerHost} refused this agent as unknown{detail}.");
                return SafeHasPassword() ? EnrolRejectedRetry : SlowRetry;
            case RefusedReasons.Revoked when SafeHasPassword():
                // A revoked key stays refused forever; with an install password we can come back under a new key.
                _downMessage = "Enrolling again with a new key";
                ReportConnectionError($"Server {settings.ServerHost}: this agent's key is revoked{detail}; creating a new key and enrolling again.");
                ReKey();
                return ReEnrolDelay;
            case RefusedReasons.Revoked:
                _downMessage = "This computer was revoked on the server";
                ReportConnectionError($"Server {settings.ServerHost}: this agent's key is revoked{detail}. To enrol again, reinstall with " +
                                      "INSTALLPASSWORD (or run DomainNotice.Agent.exe --reenrol when the server does not require a password).");
                return SlowRetry;
            case RefusedReasons.Replaced:
                _downMessage = "This computer was replaced on the server";
                ReportConnectionError($"Server {settings.ServerHost}: this computer record was replaced{detail}.");
                return SlowRetry;
            case RefusedReasons.RateLimited:
                _downMessage = "Too many attempts; waiting";
                ReportConnectionError($"Server {settings.ServerHost} rate-limited this agent{detail}.");
                return EnrolRejectedRetry;
            default:
                _downMessage = "Refused by the server";
                ReportConnectionError($"Server {settings.ServerHost} refused the connection: {refused.Reason}{detail}.");
                return _backoff.Next();
        }
    }

    /// <summary>
    /// Drops the current key and enrolment; the next attempt creates a new key pair and sends Enroll. Called from
    /// the read loop it only marks the request; the key is replaced at the start of the next attempt, after the
    /// connection that uses the old certificate is gone.
    /// </summary>
    private void ReKey()
    {
        _reKeyPending = true;
    }

    private void ApplyReKey()
    {
        _reKeyPending = false;
        try
        {
            _identity?.Dispose();
            _identity = null;
            AgentIdentity.DeleteKey();
            _settings.ClearEnrolment();
        }
        catch (Exception ex)
        {
            _logger.LogError("Re-keying failed: {Error}", ex.Message);
        }
    }

    private bool SafeHasPassword()
    {
        try
        {
            return _settings.ReadInstallPassword() is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task RunConnectedAsync(FrameConnection connection, SslStream ssl, string host, int port, Welcome welcome, CancellationToken stop)
    {
        _backoff.Reset();
        _lastEventError = null;
        _link = connection;
        _downMessage = "Disconnected from the server";
        _events.LogInformation("Connected to server {Host}:{Port} (server version {ServerVersion}).", host, port, welcome.ServerVersion);
        await _router.SetLinkAsync(this, new ConnectionStatus { Connected = true, ServerName = host }, stop).ConfigureAwait(false);
        await _updater.OnLinkUpAsync(this, stop).ConfigureAwait(false);

        using var linkCts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var keepalive = new KeepaliveMonitor(_time, ProtocolInfo.OfflineAfter);

        if (!string.IsNullOrWhiteSpace(welcome.KerberosServicePrincipal))
            _ = Task.Run(() => ProveIdentityAsync(connection, ssl, welcome.KerberosServicePrincipal!, linkCts.Token), CancellationToken.None);

        var work = Channel.CreateUnbounded<IncomingFrame>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var tasks = new[]
        {
            ReadLoopAsync(connection, keepalive, work.Writer, linkCts.Token),
            DispatchLoopAsync(work.Reader, linkCts.Token),
            PingLoopAsync(connection, keepalive, linkCts.Token),
            StatusLoopAsync(connection, linkCts.Token),
        };

        try
        {
            var first = await Task.WhenAny(tasks).ConfigureAwait(false);
            await first.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or ProtocolException or OperationCanceledException or ObjectDisposedException or AuthenticationException or SocketException)
        {
            _logger.LogWarning("Link to {Host} failed: {Error}", host, ex.Message);
        }
        finally
        {
            _link = null;
            _updater.OnLinkDown();
            linkCts.Cancel();
            // The remaining loops end on cancellation; the connection is disposed by the caller either way.
            await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None)).ConfigureAwait(false);
            _events.LogWarning("Disconnected from server {Host}.", host);
        }
    }

    /// <summary>
    /// Reads frames and answers keepalives only. Everything else goes to <see cref="DispatchLoopAsync"/> through a
    /// queue, so slow local work (the Session UI pipe, writing or hashing an update package) can never stop the
    /// reading, which would starve the keepalive and stall the server's writes.
    /// </summary>
    private async Task ReadLoopAsync(FrameConnection connection, KeepaliveMonitor keepalive, ChannelWriter<IncomingFrame> work, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await connection.ReadAsync(ct).ConfigureAwait(false);
                if (frame is null)
                {
                    _logger.LogInformation("The server closed the connection.");
                    return;
                }

                keepalive.MarkReceived();
                switch (frame.Message)
                {
                    case Ping:
                        await connection.SendAsync(new Pong(), frame.Envelope!.Id, ct).ConfigureAwait(false);
                        break;
                    case Pong:
                        break;
                    case Refused refused:
                        _delayOverride = HandleRefused(refused, SafeLoad());
                        return;
                    default:
                        work.TryWrite(frame); // unbounded
                        break;
                }
            }
        }
        finally
        {
            work.TryComplete();
        }
    }

    private async Task DispatchLoopAsync(ChannelReader<IncomingFrame> work, CancellationToken ct)
    {
        await foreach (var frame in work.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (frame.Chunk is { } chunk)
            {
                await _updater.OnChunkAsync(chunk, ct).ConfigureAwait(false);
                continue;
            }

            var envelope = frame.Envelope!;
            switch (frame.Message)
            {
                case Show show:
                    await _router.OnServerShowAsync(show, ct).ConfigureAwait(false);
                    break;
                case Withdraw withdraw:
                    await _router.OnServerWithdrawAsync(withdraw, ct).ConfigureAwait(false);
                    break;
                case ConfirmResult result:
                    await _router.OnServerConfirmResultAsync(result, envelope.ReplyTo, ct).ConfigureAwait(false);
                    break;
                case IdentityResult identity when identity.Linked:
                    _logger.LogInformation("Kerberos proof accepted; linked to AD computer {AdComputer}.", identity.AdComputer);
                    break;
                case IdentityResult identity:
                    _logger.LogWarning("Kerberos proof not accepted: {Error}", identity.Error ?? "no reason given");
                    break;
                case UpdateAgent update:
                    await _updater.OnUpdateAgentAsync(update, ct).ConfigureAwait(false);
                    break;
                case null:
                    _logger.LogDebug("Ignored unknown message type {Type}.", envelope.Type);
                    break;
                default:
                    _logger.LogDebug("Ignored unexpected message {Type}.", envelope.Type);
                    break;
            }
        }
        // The reader completed the queue: the link is gone. Keep this task pending until cancellation so that
        // Task.WhenAny picks the read loop's own outcome (closed / refused / error).
        await Task.Delay(System.Threading.Timeout.Infinite, ct).ConfigureAwait(false);
    }

    private async Task PingLoopAsync(FrameConnection connection, KeepaliveMonitor keepalive, CancellationToken ct)
    {
        var lastPing = _time.GetTimestamp();
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), _time, ct).ConfigureAwait(false);
            if (keepalive.IsSilentTooLong)
            {
                _logger.LogWarning("Nothing received from the server for {Seconds} s; dropping the link.", (int)keepalive.SinceLastReceived.TotalSeconds);
                return;
            }

            if (_time.GetElapsedTime(lastPing) >= ProtocolInfo.PingInterval)
            {
                await connection.SendAsync(new Ping(), null, ct).ConfigureAwait(false);
                lastPing = _time.GetTimestamp();
            }
        }
    }

    private async Task StatusLoopAsync(FrameConnection connection, CancellationToken ct)
    {
        while (true)
        {
            var signaled = await _statusTrigger.Signal.WaitAsync(ProtocolInfo.StatusInterval, ct).ConfigureAwait(false);
            if (signaled)
            {
                // Coalesce bursts (logon fires several events, IP changes come in groups).
                await Task.Delay(TimeSpan.FromSeconds(2), _time, ct).ConfigureAwait(false);
            }

            await connection.SendAsync(new Status
            {
                ComputerName = MachineInfo.ComputerName,
                LocalIps = MachineInfo.LocalIps(),
                Sessions = [.. _sessions.Current],
            }, null, ct).ConfigureAwait(false);
        }
    }

    private async Task ProveIdentityAsync(FrameConnection connection, SslStream ssl, string spn, CancellationToken ct)
    {
        try
        {
            var proof = KerberosProof.Create(spn, ssl);
            if (proof.TokenBase64 is null)
            {
                // Not fatal: a PC outside the domain, no SPN, or no DC in reach. The server falls back to name rules.
                _logger.LogInformation("No Kerberos proof sent: {Error}", proof.Error);
                return;
            }

            await connection.SendAsync(new ProveIdentity { KerberosToken = proof.TokenBase64 }, null, ct).ConfigureAwait(false);
            _logger.LogInformation("Sent Kerberos proof for {Spn}.", spn);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogInformation("Sending the Kerberos proof failed: {Error}", ex.Message);
        }
    }

    private Hello BuildHello() => new()
    {
        AgentVersion = AppVersion.Current,
        ComputerName = MachineInfo.ComputerName,
        DnsName = MachineInfo.DnsName,
        OsVersion = MachineInfo.OsVersion,
        LocalIps = MachineInfo.LocalIps(),
        Sessions = [.. _sessions.Current],
    };

    private async Task<IncomingFrame?> ReadReplyAsync(FrameConnection connection, string[] types, CancellationToken stop)
    {
        using var cts = Timeout(stop, HandshakeTimeout);
        while (true)
        {
            var frame = await connection.ReadAsync(cts.Token).ConfigureAwait(false);
            if (frame is null) return null;
            if (frame.Envelope is not { } envelope) continue;
            if (frame.Message is not null && types.Contains(envelope.Type)) return frame;
            if (frame.Message is Ping) await connection.SendAsync(new Pong(), envelope.Id, cts.Token).ConfigureAwait(false);
            else _logger.LogDebug("Ignored {Type} while waiting for {Expected}.", envelope.Type, string.Join('/', types));
        }
    }

    private static CancellationTokenSource Timeout(CancellationToken stop, TimeSpan timeout)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        cts.CancelAfter(timeout);
        return cts;
    }

    private static string DescribeError(Exception ex) => ex switch
    {
        SocketException s => $"Connection failed: {s.Message} ({s.SocketErrorCode}).",
        OperationCanceledException => "Connection attempt timed out.",
        AuthenticationException a => $"TLS handshake failed: {a.InnerException?.Message ?? a.Message}",
        _ => $"Connection failed: {ex.GetType().Name}: {ex.Message}",
    };

    /// <summary>Logs a connection error; the Event Log gets it when it is new or once an hour, so retries do not flood it.</summary>
    private void ReportConnectionError(string message)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (message != _lastEventError || now - _lastEventErrorUtc >= EventLogRepeat)
        {
            _events.LogWarning("{Message}", message);
            _lastEventError = message;
            _lastEventErrorUtc = now;
        }
        else
        {
            _logger.LogWarning("{Message}", message);
        }
    }

    public override void Dispose()
    {
        _identity?.Dispose();
        base.Dispose();
    }
}
