using System.Collections.Concurrent;
using System.DirectoryServices.Protocols;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using DomainNotice.Server.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DomainNotice.Server.Directory;

/// <summary>An LDAP failure with a message meant for the admin.</summary>
public sealed class DirectoryConnectException(string message, bool invalidCredentials = false, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>The DC rejected the user name or password (LDAP 49). No other DC was tried.</summary>
    public bool InvalidCredentials { get; } = invalidCredentials;
}

/// <summary>A bound connection. Dispose returns pooled service connections to the pool.</summary>
internal sealed class LdapLease : IAsyncDisposable
{
    private readonly Action<LdapLease>? _return;
    internal LdapLease(LdapConnection connection, string host, int generation, DateTime createdUtc, Action<LdapLease>? returnToPool)
    {
        Connection = connection;
        Host = host;
        Generation = generation;
        CreatedUtc = createdUtc;
        _return = returnToPool;
    }

    public LdapConnection Connection { get; }
    public string Host { get; }
    internal int Generation { get; }
    internal DateTime CreatedUtc { get; }
    /// <summary>Set when an operation failed in a way that may leave the connection unusable.</summary>
    public bool Broken { get; set; }

    public ValueTask DisposeAsync()
    {
        if (_return is not null) _return(this);
        else Connection.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Opens LDAP connections with System.DirectoryServices.Protocols: Directory:Hosts in order on connection failures,
/// LDAPS or StartTLS (None only in Development), certificate trust per plan 7 item 2, async requests with timeouts,
/// and a small short-lived pool of service-account connections (plan 7 item 9). Blocking calls (bind, StartTLS) run on
/// the thread pool behind a concurrency cap so request threads never wait on a slow DC.
/// </summary>
internal sealed class LdapConnector : IDisposable
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PoolIdleTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PoolMaxLifetime = TimeSpan.FromMinutes(10);
    private const int PoolMaxIdle = 4;

    private readonly IOptions<DirectoryOptions> _options;
    private readonly IHostEnvironment _env;
    private readonly BindPasswordProvider _passwords;
    private readonly TimeProvider _time;
    private readonly ILogger<LdapConnector> _logger;
    private readonly SemaphoreSlim _blockingGate = new(8, 8);
    private readonly ConcurrentQueue<(LdapLease Lease, DateTime IdleSinceUtc)> _idle = new();
    private int _generation;
    private static int _unknownSecurityWarned;
    private X509Certificate2Collection? _trusted;

    public LdapConnector(IOptions<DirectoryOptions> options, IHostEnvironment env, BindPasswordProvider passwords,
        TimeProvider time, ILogger<LdapConnector> logger)
    {
        _options = options;
        _env = env;
        _passwords = passwords;
        _time = time;
        _logger = logger;
    }

    private DirectoryOptions O => _options.Value;
    public bool IsDevelopment => _env.IsDevelopment();

    /// <summary>Drops idle pooled connections (e.g. after the bind password changed).</summary>
    public void ClearPool()
    {
        Interlocked.Increment(ref _generation);
        while (_idle.TryDequeue(out var item)) item.Lease.Connection.Dispose();
    }

    /// <summary>A bound service-account connection, from the pool when possible.</summary>
    public async Task<LdapLease> LeaseServiceAsync(CancellationToken ct)
    {
        var error = LdapSecurityPolicy.ValidateServiceAccount(O, IsDevelopment);
        if (error is not null) throw new DirectoryConnectException(error);
        var now = _time.GetUtcNow().UtcDateTime;
        var generation = Volatile.Read(ref _generation);
        while (_idle.TryDequeue(out var item))
        {
            if (item.Lease.Generation == generation && now - item.IdleSinceUtc < PoolIdleTimeout
                && now - item.Lease.CreatedUtc < PoolMaxLifetime)
                return item.Lease;
            item.Lease.Connection.Dispose();
        }
        string password;
        try
        {
            password = await _passwords.GetAsync(ct);
        }
        catch (InvalidOperationException e)
        {
            throw new DirectoryConnectException(e.Message);
        }
        var (conn, host) = await ConnectAndBindAsync(BindNames.Format(O.BindUser, O.BaseDn), password, ct);
        return new LdapLease(conn, host, generation, now, ReturnToPool);
    }

    /// <summary>Binds as the given user (domain sign-in). Not pooled. Throws <see cref="DirectoryConnectException"/>.</summary>
    public async Task<LdapLease> BindAsUserAsync(string userName, string password, CancellationToken ct)
    {
        var error = LdapSecurityPolicy.ValidateConnection(O, IsDevelopment);
        if (error is not null) throw new DirectoryConnectException(error);
        // An empty simple bind is an anonymous bind and would "succeed".
        if (string.IsNullOrEmpty(password) || string.IsNullOrWhiteSpace(userName))
            throw new DirectoryConnectException("User name and password are required.", invalidCredentials: true);
        var (conn, host) = await ConnectAndBindAsync(BindNames.Format(userName, O.BaseDn), password, ct);
        return new LdapLease(conn, host, -1, _time.GetUtcNow().UtcDateTime, null);
    }

    private void ReturnToPool(LdapLease lease)
    {
        if (lease.Broken || lease.Generation != Volatile.Read(ref _generation) || _idle.Count >= PoolMaxIdle
            || _time.GetUtcNow().UtcDateTime - lease.CreatedUtc >= PoolMaxLifetime)
        {
            lease.Connection.Dispose();
            return;
        }
        _idle.Enqueue((lease, _time.GetUtcNow().UtcDateTime));
    }

    private async Task<(LdapConnection Connection, string Host)> ConnectAndBindAsync(string bindName, string password, CancellationToken ct)
    {
        LdapNativeEnvironment.EnsureApplied(O, IsDevelopment, Path.Combine(Path.GetTempPath(), "domainnotice"), _logger);
        if (LdapSecurityPolicy.UnknownSecurityWarning(O) is { } warning && Interlocked.Exchange(ref _unknownSecurityWarned, 1) == 0)
            _logger.LogWarning("{Warning}", warning);
        var defaultPort = LdapSecurityPolicy.EffectivePort(O);
        var failures = new List<string>();
        foreach (var entry in O.Hosts)
        {
            ct.ThrowIfCancellationRequested();
            var (host, port) = LdapSecurityPolicy.ParseHost(entry, defaultPort);
            LdapConnection? conn = null;
            try
            {
                conn = CreateConnection(host, port);
                var c = conn;
                await RunBlockingAsync(() =>
                {
                    if (O.Security == DirectorySecurity.StartTls)
                    {
                        // On Linux S.DS.P only connects on the first request, and StartTLS on an unconnected handle
                        // fails with 81. Read the rootDSE anonymously first (AD allows this), then start TLS.
                        c.AutoBind = false;
                        c.SendRequest(new SearchRequest("", "(objectClass=*)", SearchScope.Base, "supportedLDAPVersion"));
                        c.SessionOptions.StartTransportLayerSecurity(null);
                    }
                    c.Bind(new NetworkCredential(bindName, password));
                }, ct);
                return (conn, host);
            }
            catch (LdapException e) when (e.ErrorCode == 49)
            {
                conn?.Dispose();
                throw new DirectoryConnectException("The user name or password is incorrect.", invalidCredentials: true, e);
            }
            catch (Exception e) when (e is LdapException or DirectoryOperationException or TlsOperationException)
            {
                conn?.Dispose();
                var msg = e is LdapException le ? $"{le.Message} (LDAP {le.ErrorCode})" : e.Message;
                failures.Add($"{host}:{port}: {msg}");
                _logger.LogWarning("LDAP connection to {Host}:{Port} failed: {Error}", host, port, msg);
                if (!IsConnectivityError(e)) throw new DirectoryConnectException($"{host}:{port}: {msg}", inner: e);
            }
            catch
            {
                conn?.Dispose();
                throw;
            }
        }
        throw new DirectoryConnectException(
            "No domain controller could be reached: " + string.Join("; ", failures) +
            ". Check Directory:Hosts, Directory:Port, Directory:Security and the certificate trust (Directory:TrustedCertificate).");
    }

    private static bool IsConnectivityError(Exception e) => e switch
    {
        // 81 server down (also how a failed TLS handshake shows on Linux), 91 connect error, 85 timeout,
        // 52 unavailable, 51 busy, 80 other/TLS.
        LdapException le => le.ErrorCode is 81 or 91 or 85 or 52 or 51 or 80,
        TlsOperationException => true,
        _ => false,
    };

    private LdapConnection CreateConnection(string host, int port)
    {
        var conn = new LdapConnection(new LdapDirectoryIdentifier(host, port, fullyQualifiedDnsHostName: true, connectionless: false))
        {
            AuthType = AuthType.Basic,
            Timeout = RequestTimeout,
        };
        conn.SessionOptions.ProtocolVersion = 3;
        conn.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
        if (OperatingSystem.IsWindows() && (O.AcceptInvalidCertificate || !string.IsNullOrWhiteSpace(O.TrustedCertificate)))
        {
            // Windows (development): the callback works there. On Linux/macOS trust comes from LdapNativeEnvironment.
            conn.SessionOptions.VerifyServerCertificate = (_, cert) => VerifyOnWindows(host, cert);
        }
        if (O.Security == DirectorySecurity.Ldaps) conn.SessionOptions.SecureSocketLayer = true;
        return conn;
    }

    private bool VerifyOnWindows(string host, X509Certificate certificate)
    {
        if (O.AcceptInvalidCertificate && IsDevelopment) return true;
        using var cert = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        if (!cert.MatchesHostname(host)) return false;
        _trusted ??= LdapSecurityPolicy.LoadTrusted(O.TrustedCertificate);
        foreach (var t in _trusted)
            if (t.RawDataMemory.Span.SequenceEqual(cert.RawDataMemory.Span)) return true; // pinned DC certificate
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(_trusted);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(cert);
    }

    private async Task RunBlockingAsync(Action action, CancellationToken ct)
    {
        await _blockingGate.WaitAsync(ct);
        try
        {
            await Task.Run(action, ct);
        }
        finally
        {
            _blockingGate.Release();
        }
    }

    /// <summary>Sends a request asynchronously (BeginSendRequest/EndSendRequest) with the request timeout; aborts on cancel.</summary>
    public static async Task<DirectoryResponse> SendAsync(LdapConnection connection, DirectoryRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var tcs = new TaskCompletionSource<DirectoryResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ar = connection.BeginSendRequest(request, RequestTimeout, PartialResultProcessing.NoPartialResultSupport, a =>
        {
            try
            {
                tcs.TrySetResult(connection.EndSendRequest(a));
            }
            catch (Exception e)
            {
                tcs.TrySetException(e);
            }
        }, null);
        await using var reg = ct.Register(() =>
        {
            try
            {
                connection.Abort(ar);
            }
            catch
            {
                // already finished
            }
            tcs.TrySetCanceled(ct);
        });
        return await tcs.Task;
    }

    public void Dispose() => ClearPool();
}
