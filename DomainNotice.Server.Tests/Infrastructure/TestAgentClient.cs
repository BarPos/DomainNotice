using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DomainNotice.Server.Abstractions;
using DomainNotice.Server.AgentLink;
using DomainNotice.Server.Common;
using DomainNotice.Server.Data;
using DomainNotice.Server.Options;
using DomainNotice.Shared;
using DomainNotice.Shared.Protocol;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DomainNotice.Server.Tests.Infrastructure;

/// <summary>
/// A fake agent for tests: real TLS over loopback with its own ECDSA client certificate, pins the server
/// fingerprint, speaks <see cref="FrameConnection"/>. A background reader collects every frame; the Receive*
/// methods take the first matching one (others stay queued), with a timeout.
/// <code>
/// await using var server = await TestAgentServer.StartAsync(db);
/// await using var agent = await server.ConnectAsync();
/// await agent.EnrollAsync();                 // or: var (agent, id) = await server.ConnectEnrolledAsync("PC-1");
/// await agent.HelloAsync("PC-1", [TestAgentClient.Session(1, @"CORP\alice")]);
/// var show = await agent.ReceiveAsync&lt;Show&gt;();
/// </code>
/// </summary>
public sealed class TestAgentClient : IAsyncDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly TcpClient _tcp;
    private readonly List<IncomingFrame> _inbox = [];
    private readonly List<IncomingFrame> _all = [];
    private readonly CancellationTokenSource _cts = new();
    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _reader;

    private TestAgentClient(TcpClient tcp, SslStream ssl, X509Certificate2 certificate, int maxFrameBytes)
    {
        _tcp = tcp;
        Ssl = ssl;
        Certificate = certificate;
        Fingerprint = KeyFingerprint.Compute(certificate);
        Frames = new FrameConnection(ssl, maxFrameBytes);
        _reader = Task.Run(ReadLoopAsync);
    }

    /// <summary>The client certificate (reuse it to reconnect as the same computer).</summary>
    public X509Certificate2 Certificate { get; }
    /// <summary>The agent key fingerprint, as stored in Computer.AgentKeyFingerprint.</summary>
    public string Fingerprint { get; }
    public SslStream Ssl { get; }
    public FrameConnection Frames { get; }
    /// <summary>True once the server closed the connection (or it broke).</summary>
    public bool IsClosed { get; private set; }
    /// <summary>The ProtocolException or IOException that ended the reader, if any.</summary>
    public Exception? ReadError { get; private set; }

    /// <summary>Every frame received so far, in order (including ones already taken by Receive*).</summary>
    public IReadOnlyList<IncomingFrame> AllReceived
    {
        get { lock (_inbox) return _all.ToList(); }
    }

    /// <summary>A new self-signed ECDSA P-256 client certificate, re-imported so its key works with SslStream.</summary>
    public static X509Certificate2 CreateCertificate(string commonName = "DomainNotice Test Agent")
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var cert = KeyFingerprint.CreateSelfSigned(key, commonName);
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
    }

    /// <summary>Connects and completes TLS. Throws AuthenticationException if the server key does not match the pin.</summary>
    public static async Task<TestAgentClient> ConnectAsync(IPEndPoint endpoint, string serverFingerprint,
        X509Certificate2? certificate = null, int maxFrameBytes = ProtocolInfo.DefaultMaxFrameBytes, CancellationToken ct = default)
    {
        certificate ??= CreateCertificate();
        var tcp = new TcpClient(endpoint.AddressFamily);
        try
        {
            await tcp.ConnectAsync(endpoint, ct);
            var ssl = new SslStream(tcp.GetStream(), false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "domainnotice-server",
                ClientCertificates = new X509CertificateCollection { certificate },
                LocalCertificateSelectionCallback = (_, _, _, _, _) => certificate,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                // Pinning, no CA (plan 3.2).
                RemoteCertificateValidationCallback = (_, cert, _, _) => KeyFingerprint.Matches(serverFingerprint, cert),
            }, ct);
            return new TestAgentClient(tcp, ssl, certificate, maxFrameBytes);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var frame = await Frames.ReadAsync(_cts.Token);
                if (frame is null) break;
                lock (_inbox)
                {
                    _inbox.Add(frame);
                    _all.Add(frame);
                }
                Pulse();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ReadError = ex;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsClosed = true;
            Pulse();
        }
    }

    private void Pulse()
    {
        TaskCompletionSource old;
        lock (_inbox)
        {
            old = _signal;
            _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        old.TrySetResult();
    }

    // ---------------------------------------------------------------- send

    public Task<Guid> SendAsync<T>(T message, Guid? replyTo = null, CancellationToken ct = default) where T : IAgentMessage =>
        Frames.SendAsync(message, replyTo, ct);

    public Task SendChunkAsync(Guid transferId, long offset, ReadOnlyMemory<byte> data, CancellationToken ct = default) =>
        Frames.SendChunkAsync(transferId, offset, data, ct);

    // ---------------------------------------------------------------- receive

    /// <summary>Takes the first queued (or next arriving) frame that matches; throws TimeoutException otherwise.</summary>
    public async Task<IncomingFrame> ReceiveFrameAsync(Func<IncomingFrame, bool> match, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? DefaultTimeout);
        while (true)
        {
            Task wait;
            lock (_inbox)
            {
                var index = _inbox.FindIndex(f => match(f));
                if (index >= 0)
                {
                    var frame = _inbox[index];
                    _inbox.RemoveAt(index);
                    return frame;
                }
                if (IsClosed) throw new InvalidOperationException("The server closed the connection before the expected message arrived." +
                    (ReadError is null ? "" : " Read error: " + ReadError.Message));
                wait = _signal.Task;
            }
            try
            {
                await wait.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("No matching message arrived in time. Queued: " +
                    string.Join(", ", Pending.Select(f => f.Envelope?.Type ?? "chunk")));
            }
        }
    }

    /// <summary>The first message of type <typeparamref name="T"/> (other messages stay queued).</summary>
    public async Task<T> ReceiveAsync<T>(TimeSpan? timeout = null, CancellationToken ct = default) where T : class, IAgentMessage =>
        (T)(await ReceiveFrameAsync(f => f.Message is T, timeout, ct)).Message!;

    /// <summary>The first message of type T together with its envelope (for ReplyTo / Id).</summary>
    public async Task<(Envelope Envelope, T Message)> ReceiveEnvelopeAsync<T>(TimeSpan? timeout = null, CancellationToken ct = default)
        where T : class, IAgentMessage
    {
        var frame = await ReceiveFrameAsync(f => f.Message is T, timeout, ct);
        return (frame.Envelope!, (T)frame.Message!);
    }

    /// <summary>The next frame of any kind, in arrival order.</summary>
    public Task<IncomingFrame> ReceiveAnyAsync(TimeSpan? timeout = null, CancellationToken ct = default) =>
        ReceiveFrameAsync(_ => true, timeout, ct);

    public async Task<PackageChunk> ReceiveChunkAsync(TimeSpan? timeout = null, CancellationToken ct = default) =>
        (await ReceiveFrameAsync(f => f.Chunk is not null, timeout, ct)).Chunk!.Value;

    /// <summary>Frames received and not yet taken.</summary>
    public IReadOnlyList<IncomingFrame> Pending
    {
        get { lock (_inbox) return _inbox.ToList(); }
    }

    /// <summary>True when the server closed the connection within the timeout.</summary>
    public async Task<bool> WaitForCloseAsync(TimeSpan? timeout = null)
    {
        try
        {
            await _reader.WaitAsync(timeout ?? DefaultTimeout);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>Waits for the server to close the connection and returns the Refused it sent first (or null).</summary>
    public async Task<Refused?> WaitForRefusedAsync(TimeSpan? timeout = null)
    {
        await WaitForCloseAsync(timeout);
        lock (_inbox) return _all.Select(f => f.Message as Refused).FirstOrDefault(r => r is not null);
    }

    // ---------------------------------------------------------------- protocol helpers

    public static SessionInfo Session(int sessionId, string userName, SessionState state = SessionState.Active, DateTime? logonUtc = null) =>
        new() { SessionId = sessionId, UserName = userName, State = state, LogonTimeUtc = logonUtc ?? default };

    public static Hello CreateHello(string computerName = "PC-1", IEnumerable<SessionInfo>? sessions = null,
        IEnumerable<string>? localIps = null, string agentVersion = AppVersion.Current) => new()
    {
        AgentVersion = agentVersion,
        ComputerName = computerName,
        DnsName = computerName.ToLowerInvariant() + ".example.local",
        OsVersion = "Windows 11 Pro 24H2",
        LocalIps = localIps?.ToList() ?? ["10.0.0.10"],
        Sessions = sessions?.ToList() ?? [],
    };

    /// <summary>Sends Enroll and returns the EnrollResult.</summary>
    public async Task<EnrollResult> EnrollAsync(string? installPassword = null, string computerName = "PC-1",
        string agentVersion = AppVersion.Current, TimeSpan? timeout = null)
    {
        await SendAsync(new Enroll
        {
            InstallPassword = installPassword,
            ComputerName = computerName,
            DnsName = computerName.ToLowerInvariant() + ".example.local",
            OsVersion = "Windows 11 Pro 24H2",
            AgentVersion = agentVersion,
        });
        return await ReceiveAsync<EnrollResult>(timeout);
    }

    /// <summary>Sends Hello and returns the Welcome.</summary>
    public async Task<Welcome> HelloAsync(Hello hello, TimeSpan? timeout = null)
    {
        await SendAsync(hello);
        return await ReceiveAsync<Welcome>(timeout);
    }

    public Task<Welcome> HelloAsync(string computerName = "PC-1", IEnumerable<SessionInfo>? sessions = null,
        IEnumerable<string>? localIps = null, string agentVersion = AppVersion.Current, TimeSpan? timeout = null) =>
        HelloAsync(CreateHello(computerName, sessions, localIps, agentVersion), timeout);

    public Task SendStatusAsync(string computerName, IEnumerable<SessionInfo>? sessions = null, IEnumerable<string>? localIps = null) =>
        SendAsync(new Status { ComputerName = computerName, Sessions = sessions?.ToList() ?? [], LocalIps = localIps?.ToList() ?? ["10.0.0.10"] });

    /// <summary>Ping → Pong round trip (also a cheap way to wait until earlier messages were read by the server).</summary>
    public async Task PingAsync(TimeSpan? timeout = null)
    {
        var id = await SendAsync(new Ping());
        await ReceiveFrameAsync(f => f.Message is Pong && f.Envelope!.ReplyTo == id, timeout);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await Frames.DisposeAsync(); } catch { /* broken already */ }
        _tcp.Dispose();
        try { await _reader; } catch { /* ignored */ }
        _cts.Dispose();
    }
}

/// <summary>
/// A running agent-link server for tests: a generic host with the AgentLink area (listener on 127.0.0.1:0,
/// key in a temp directory) on a fresh test database. Other areas add their services with
/// <c>configureServices</c> (e.g. <c>s =&gt; s.AddAnnouncements(config)</c>), or pass <c>fullCore: true</c> to
/// run <see cref="ServiceCollectionExtensions.AddDomainNoticeCore"/> instead.
/// </summary>
public sealed class TestAgentServer : IAsyncDisposable
{
    private TestAgentServer(IHost host, TestDatabase database, string dataDirectory)
    {
        Host = host;
        Database = database;
        DataDirectory = dataDirectory;
    }

    public IHost Host { get; }
    public IServiceProvider Services => Host.Services;
    public TestDatabase Database { get; }
    /// <summary>Paths:Data of this server (deleted on dispose).</summary>
    public string DataDirectory { get; }
    public IPEndPoint Endpoint => Services.GetRequiredService<AgentListenerEndpoint>().LocalEndPoint!;
    public string Fingerprint => Services.GetRequiredService<IServerIdentity>().Fingerprint;
    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    /// <summary>
    /// Settings used unless overridden: Agents:ListenAddress=127.0.0.1, Agents:Port=0, Paths:Data=temp,
    /// Enrolment:RequirePassword=false.
    /// </summary>
    public static async Task<TestAgentServer> StartAsync(TestDatabase database,
        IDictionary<string, string?>? settings = null,
        Action<IServiceCollection, IConfiguration>? configureServices = null,
        AgentLinkTimings? timings = null,
        bool fullCore = false,
        string? dataDirectory = null)
    {
        dataDirectory ??= Path.Combine(Path.GetTempPath(), "dn-test-" + Guid.NewGuid().ToString("N"));
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = database.ConnectionString,
            ["Agents:ListenAddress"] = "127.0.0.1",
            ["Agents:Port"] = "0",
            ["Paths:Data"] = dataDirectory,
            ["Paths:Backups"] = Path.Combine(dataDirectory, "backups"),
            ["Enrolment:RequirePassword"] = "false",
        };
        foreach (var (k, v) in settings ?? new Dictionary<string, string?>()) values[k] = v;

        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = "Testing",
            DisableDefaults = true,
        });
        builder.Configuration.AddInMemoryCollection(values);
        var config = builder.Configuration;
        builder.Services.AddLogging(l =>
        {
            l.SetMinimumLevel(LogLevel.Debug);
            if (Environment.GetEnvironmentVariable("DOMAINNOTICE_TEST_LOG") == "1") l.AddSimpleConsole();
        });
        if (timings is not null) builder.Services.AddSingleton(timings);
        if (fullCore)
        {
            builder.Services.AddDomainNoticeCore(config, builder.Environment);
            // Keep Data Protection keys in memory (the Testing environment does not persist them).
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        }
        else
        {
            builder.Services.Configure<PathsOptions>(config.GetSection(PathsOptions.Section));
            builder.Services.Configure<AgentsOptions>(config.GetSection(AgentsOptions.Section));
            builder.Services.Configure<EnrolmentOptions>(config.GetSection(EnrolmentOptions.Section));
            builder.Services.Configure<HistoryOptions>(config.GetSection(HistoryOptions.Section));
            builder.Services.Configure<DirectoryOptions>(config.GetSection(DirectoryOptions.Section));
            builder.Services.TryAddSingleton(TimeProvider.System);
            builder.Services.AddDbContextFactory<AppDbContext>(o => ServiceCollectionExtensions.ConfigureDb(o, database.ConnectionString));
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
            builder.Services.AddSingleton<ISettingsStore, SettingsStore>();
            builder.Services.AddSingleton<IAuditLog, AuditLog>();
            builder.Services.AddAgentLink(config);
        }
        configureServices?.Invoke(builder.Services, config);

        var host = builder.Build();
        await host.StartAsync();
        return new TestAgentServer(host, database, dataDirectory);
    }

    /// <summary>A new TLS connection (new key unless <paramref name="certificate"/> is given).</summary>
    public Task<TestAgentClient> ConnectAsync(X509Certificate2? certificate = null) =>
        TestAgentClient.ConnectAsync(Endpoint, Fingerprint, certificate);

    /// <summary>Connect, enrol (with <paramref name="installPassword"/>), and send Hello. Returns the online agent.</summary>
    public async Task<(TestAgentClient Agent, Guid ComputerId)> ConnectEnrolledAsync(string computerName = "PC-1",
        IEnumerable<SessionInfo>? sessions = null, string? installPassword = null, X509Certificate2? certificate = null)
    {
        var agent = await ConnectAsync(certificate);
        var result = await agent.EnrollAsync(installPassword, computerName);
        if (!result.Accepted) throw new InvalidOperationException("Enrolment refused: " + result.Error);
        var welcome = await agent.HelloAsync(computerName, sessions);
        if (!welcome.Compatible) throw new InvalidOperationException("Welcome{compatible:false}");
        await WaitUntilAsync(() => Get<IAgentHub>().IsOnline(result.ComputerId!.Value));
        return (agent, result.ComputerId!.Value);
    }

    /// <summary>Polls until the condition holds (e.g. a database change made by the server).</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null) =>
        await WaitUntilAsync(() => Task.FromResult(condition()), timeout);

    public static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TestAgentClient.DefaultTimeout);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met in time.");
            await Task.Delay(25);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Host.StopAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            Host.Dispose();
            try { System.IO.Directory.Delete(DataDirectory, true); } catch { /* best effort */ }
        }
    }
}
