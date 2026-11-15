using System.Text.Json;

namespace DomainNotice.Shared.Protocol;

/// <summary>
/// The JSON body of a kind-0 frame: <c>{ v, type, id, replyTo?, payload }</c>. FROZEN shape.
/// </summary>
public sealed record Envelope
{
    /// <summary>Protocol version of the sender (<see cref="ProtocolInfo.Version"/>).</summary>
    public int V { get; init; } = ProtocolInfo.Version;
    public string Type { get; init; } = "";
    public Guid Id { get; init; }
    public Guid? ReplyTo { get; init; }
    public JsonElement Payload { get; init; }
}

public static class ProtocolInfo
{
    /// <summary>Raise when the agent link gains messages or fields. Never lower.</summary>
    public const int Version = 1;

    /// <summary>Default TCP port for the agent link (Agents:Port).</summary>
    public const int DefaultPort = 5150;

    /// <summary>Default maximum frame body (Agents:MaxMessageBytes). Chunk frames must fit too.</summary>
    public const int DefaultMaxFrameBytes = 1024 * 1024;

    /// <summary>Size of an update package chunk (plan 3.3).</summary>
    public const int ChunkSize = 256 * 1024;

    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan StatusInterval = TimeSpan.FromMinutes(10);
}
