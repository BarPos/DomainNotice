using System.Net;

namespace DomainNotice.Server.AgentLink;

/// <summary>
/// Internal timings of the agent link that are not in plan section 12. Registered as a singleton with these
/// defaults; tests replace it (register their own instance before or after <c>AddAgentLink</c>).
/// </summary>
public sealed class AgentLinkTimings
{
    /// <summary>TLS handshake must finish within this time.</summary>
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(15);
    /// <summary>Before Hello was accepted (enrolment phase), each message must arrive within this time.</summary>
    public TimeSpan PreHelloTimeout { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>How often the offline sweeper runs.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>LastSeenAtUtc is written for online computers at most this often (besides Hello/Status/offline).</summary>
    public TimeSpan LastSeenFlushInterval { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>Re-evaluation of the AD name fallback for unlinked computers (directory sync may arrive later).</summary>
    public TimeSpan AdLinkInterval { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>First run of the history retention job after start, then every <see cref="RetentionInterval"/>.</summary>
    public TimeSpan RetentionFirstDelay { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan RetentionInterval { get; set; } = TimeSpan.FromDays(1);
    /// <summary>Failed enrolments per remote IP within <see cref="EnrolFailureWindow"/> before Refused{rate-limited}.</summary>
    public int EnrolMaxFailures { get; set; } = 5;
    public TimeSpan EnrolFailureWindow { get; set; } = TimeSpan.FromMinutes(15);
    /// <summary>After sending Refused, wait this long for the peer to close before closing the socket.</summary>
    public TimeSpan RefuseLinger { get; set; } = TimeSpan.FromSeconds(2);
    /// <summary>Kerberos clock skew; sizes the authenticator replay cache (2 x skew). Ticket times use Kerberos.NET's 5-minute default.</summary>
    public TimeSpan KerberosSkew { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Where the agent listener is bound. Use <see cref="Port"/> after the host started (it supports Agents:Port = 0).
/// </summary>
public sealed class AgentListenerEndpoint
{
    private readonly TaskCompletionSource<IPEndPoint> _bound = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Null until the listener is bound.</summary>
    public IPEndPoint? LocalEndPoint { get; private set; }

    /// <summary>The bound TCP port, or 0 when not listening yet.</summary>
    public int Port => LocalEndPoint?.Port ?? 0;

    /// <summary>Completes when the listener is bound.</summary>
    public Task<IPEndPoint> WhenBound => _bound.Task;

    internal void SetBound(IPEndPoint endPoint)
    {
        LocalEndPoint = endPoint;
        _bound.TrySetResult(endPoint);
    }
}
