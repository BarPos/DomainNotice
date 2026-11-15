using System.Net;
using DomainNotice.Shared;
using DomainNotice.Shared.Protocol;

namespace DomainNotice.Server.Abstractions;

/// <summary>
/// One authenticated, enrolled agent connection. ComputerId comes ONLY from the TLS client key fingerprint
/// (plan 3.4); any name or id inside a payload is ignored for identity.
/// </summary>
public interface IAgentConnection
{
    Guid ComputerId { get; }
    string AgentVersion { get; }
    /// <summary>Envelope.V the agent sent in Hello.</summary>
    int ProtocolVersion { get; }
    IPEndPoint RemoteEndPoint { get; }
    /// <summary>tls-server-end-point channel binding of this connection (for the Kerberos proof), if available.</summary>
    byte[]? ChannelBinding { get; }
    DateTime ConnectedAtUtc { get; }
    /// <summary>Current Windows sessions as last reported (Hello/Status).</summary>
    IReadOnlyList<SessionInfo> Sessions { get; }

    Task SendAsync<T>(T message, Guid? replyTo = null, CancellationToken ct = default) where T : IAgentMessage;
    Task SendChunkAsync(Guid transferId, long offset, ReadOnlyMemory<byte> data, CancellationToken ct = default);
    /// <summary>Optionally sends Refused, then closes.</summary>
    Task CloseAsync(string? refusedReason = null, string? message = null);
}

/// <summary>Registry of online agents. Implemented by the agent link (Core/AgentLink).</summary>
public interface IAgentHub
{
    bool IsOnline(Guid computerId);
    IReadOnlyCollection<Guid> OnlineComputerIds { get; }
    IAgentConnection? Get(Guid computerId);

    /// <summary>Sends to the computer if online. Returns false when it is offline or the send failed.</summary>
    Task<bool> TrySendAsync<T>(Guid computerId, T message, CancellationToken ct = default) where T : IAgentMessage;

    /// <summary>Closes the connection, if any (e.g. after revoke).</summary>
    Task DisconnectAsync(Guid computerId, string refusedReason, string? message = null);
}

/// <summary>
/// Handles agent → server messages after Hello was accepted. The agent link dispatches each envelope to the
/// handler that lists its type. Handlers are singletons; use IDbContextFactory for data access.
/// Owners: Shown/Confirm → Announcements; PackageRequest/UpdateStatus → Updates; ProveIdentity/Status → AgentLink.
/// </summary>
public interface IAgentMessageHandler
{
    IReadOnlyCollection<string> MessageTypes { get; }
    Task HandleAsync(IAgentConnection connection, Envelope envelope, IAgentMessage message, CancellationToken ct);
}

public sealed record SessionsChange(
    IReadOnlyList<SessionInfo> Current,
    IReadOnlyList<SessionInfo> LoggedOn,
    IReadOnlyList<SessionInfo> LoggedOff);

/// <summary>
/// Notifications from the agent link, called after the database (inventory/history) is updated.
/// All members have default no-op implementations; override what you need.
/// </summary>
public interface IAgentLifecycleObserver
{
    /// <summary>Hello accepted and Welcome{compatible:true} sent. Announcements push due items; Updates resend pending tasks.</summary>
    Task OnAgentOnlineAsync(IAgentConnection connection, CancellationToken ct) => Task.CompletedTask;

    /// <summary>A Status (or Hello) changed the set of sessions. Not called for the initial Hello (use OnAgentOnline).</summary>
    Task OnSessionsChangedAsync(IAgentConnection connection, SessionsChange change, CancellationToken ct) => Task.CompletedTask;

    /// <summary>The connection closed or timed out; the computer is now offline.</summary>
    Task OnAgentOfflineAsync(Guid computerId, CancellationToken ct) => Task.CompletedTask;

    /// <summary>The AD link or approval state of a computer changed (Kerberos proof, name match, admin decision).</summary>
    Task OnComputerIdentityChangedAsync(Guid computerId, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>The server's agent-link key (plan 3.2). Generated on first start into Paths:Data/keys.</summary>
public interface IServerIdentity
{
    System.Security.Cryptography.X509Certificates.X509Certificate2 Certificate { get; }
    /// <summary>Lowercase hex SHA-256 of the SubjectPublicKeyInfo.</summary>
    string Fingerprint { get; }
}

/// <summary>Raised by area services so observers (e.g. announcements) can react; implemented by the agent link.</summary>
public interface IAgentEvents
{
    Task RaiseComputerIdentityChangedAsync(Guid computerId, CancellationToken ct = default);
}

public static class UserKeys
{
    /// <summary>Trimmed, lowercased user name used for matching and counting (DOMAIN\user).</summary>
    public static string Normalize(string? userName) => (userName ?? "").Trim().ToLowerInvariant();
}
