using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using DomainNotice.Server.Abstractions;
using DomainNotice.Shared;
using DomainNotice.Shared.Protocol;

namespace DomainNotice.Server.Tests.Announcements;

public sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
    public void Set(DateTimeOffset at) => _now = at;
}

public sealed record SentMessage(object Message, Guid? ReplyTo);

public sealed class FakeConnection(Guid computerId) : IAgentConnection
{
    public Guid ComputerId { get; } = computerId;
    public string AgentVersion { get; set; } = AppVersion.Current;
    public int ProtocolVersion => ProtocolInfo.Version;
    public IPEndPoint RemoteEndPoint { get; } = new(IPAddress.Loopback, 50000);
    public byte[]? ChannelBinding => null;
    public DateTime ConnectedAtUtc { get; } = DateTime.UtcNow;
    public List<SessionInfo> SessionList { get; } = [];
    public IReadOnlyList<SessionInfo> Sessions => SessionList.ToList();
    public ConcurrentQueue<SentMessage> Sent { get; } = new();

    public IEnumerable<T> SentOf<T>() => Sent.Select(s => s.Message).OfType<T>();

    public FakeConnection WithSession(string user, SessionState state = SessionState.Active, int? id = null)
    {
        SessionList.Add(new SessionInfo { SessionId = id ?? SessionList.Count + 1, UserName = user, State = state, LogonTimeUtc = DateTime.UtcNow });
        return this;
    }

    public Task SendAsync<T>(T message, Guid? replyTo = null, CancellationToken ct = default) where T : IAgentMessage
    {
        Sent.Enqueue(new SentMessage(message!, replyTo));
        return Task.CompletedTask;
    }

    public Task SendChunkAsync(Guid transferId, long offset, ReadOnlyMemory<byte> data, CancellationToken ct = default) => Task.CompletedTask;
    public Task CloseAsync(string? refusedReason = null, string? message = null) => Task.CompletedTask;
}

public sealed class FakeHub : IAgentHub
{
    private readonly ConcurrentDictionary<Guid, FakeConnection> _connections = new();

    public FakeConnection Connect(Guid computerId)
    {
        var c = new FakeConnection(computerId);
        _connections[computerId] = c;
        return c;
    }

    public void Disconnect(Guid computerId) => _connections.TryRemove(computerId, out _);

    public bool IsOnline(Guid computerId) => _connections.ContainsKey(computerId);
    public IReadOnlyCollection<Guid> OnlineComputerIds => _connections.Keys.ToList();
    public IAgentConnection? Get(Guid computerId) => _connections.GetValueOrDefault(computerId);

    public async Task<bool> TrySendAsync<T>(Guid computerId, T message, CancellationToken ct = default) where T : IAgentMessage
    {
        if (!_connections.TryGetValue(computerId, out var c)) return false;
        await c.SendAsync(message, null, ct);
        return true;
    }

    public Task DisconnectAsync(Guid computerId, string refusedReason, string? message = null)
    {
        Disconnect(computerId);
        return Task.CompletedTask;
    }
}

public sealed class FakeSettings : ISettingsStore
{
    private readonly ConcurrentDictionary<string, string?> _values = new();
    public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult(_values.GetValueOrDefault(key));
    public Task SetAsync(string key, string? value, string actor, CancellationToken ct = default) { _values[key] = value; return Task.CompletedTask; }
    public Task<string?> GetSecretAsync(string key, CancellationToken ct = default) => GetAsync(key, ct);
    public Task SetSecretAsync(string key, string? value, string actor, CancellationToken ct = default) => SetAsync(key, value, actor, ct);
}

public static class EnvelopeFor
{
    public static Envelope Of<T>(T message) where T : IAgentMessage =>
        new() { Type = T.Type, Id = Guid.NewGuid(), Payload = JsonSerializer.SerializeToElement(message) };
}
