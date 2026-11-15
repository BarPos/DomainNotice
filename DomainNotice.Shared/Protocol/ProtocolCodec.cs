using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace DomainNotice.Shared.Protocol;

/// <summary>Turns messages into envelope bytes and back. Unknown types decode to null (ignored by receivers).</summary>
public static class ProtocolCodec
{
    private static readonly ProtocolJsonContext Ctx = ProtocolJsonContext.Default;

    private static readonly Dictionary<string, JsonTypeInfo> ByType = new(StringComparer.Ordinal)
    {
        [Enroll.Type] = Ctx.Enroll,
        [EnrollResult.Type] = Ctx.EnrollResult,
        [Hello.Type] = Ctx.Hello,
        [Welcome.Type] = Ctx.Welcome,
        [Status.Type] = Ctx.Status,
        [Refused.Type] = Ctx.Refused,
        [Show.Type] = Ctx.Show,
        [Shown.Type] = Ctx.Shown,
        [Confirm.Type] = Ctx.Confirm,
        [ConfirmResult.Type] = Ctx.ConfirmResult,
        [Withdraw.Type] = Ctx.Withdraw,
        [AgentConfig.Type] = Ctx.AgentConfig,
        [ProveIdentity.Type] = Ctx.ProveIdentity,
        [IdentityResult.Type] = Ctx.IdentityResult,
        [UpdateAgent.Type] = Ctx.UpdateAgent,
        [PackageRequest.Type] = Ctx.PackageRequest,
        [UpdateStatus.Type] = Ctx.UpdateStatus,
        [Ping.Type] = Ctx.Ping,
        [Pong.Type] = Ctx.Pong,
    };

    public static IReadOnlyCollection<string> KnownTypes => ByType.Keys;

    public static JsonTypeInfo<T> TypeInfo<T>() where T : IAgentMessage => (JsonTypeInfo<T>)ByType[T.Type];

    public static Envelope Wrap<T>(T message, Guid? replyTo = null, Guid? id = null) where T : IAgentMessage =>
        new()
        {
            Type = T.Type,
            Id = id ?? Guid.NewGuid(),
            ReplyTo = replyTo,
            Payload = JsonSerializer.SerializeToElement(message, TypeInfo<T>()),
        };

    public static byte[] EncodeEnvelope(Envelope envelope) =>
        JsonSerializer.SerializeToUtf8Bytes(envelope, Ctx.Envelope);

    public static byte[] Encode<T>(T message, Guid? replyTo = null, Guid? id = null) where T : IAgentMessage =>
        EncodeEnvelope(Wrap(message, replyTo, id));

    /// <exception cref="ProtocolException">The body is not a valid envelope.</exception>
    public static Envelope DecodeEnvelope(ReadOnlySpan<byte> body)
    {
        try
        {
            var env = JsonSerializer.Deserialize(body, Ctx.Envelope);
            if (env is null || string.IsNullOrEmpty(env.Type))
                throw new ProtocolException("Envelope without a type.");
            return env;
        }
        catch (JsonException ex)
        {
            throw new ProtocolException("Malformed envelope.", ex);
        }
    }

    /// <summary>Decodes the payload into its message record, or null for an unknown type.</summary>
    /// <exception cref="ProtocolException">Known type with a malformed payload.</exception>
    public static IAgentMessage? DecodePayload(Envelope envelope)
    {
        if (!ByType.TryGetValue(envelope.Type, out var info)) return null;
        try
        {
            if (envelope.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                return (IAgentMessage?)JsonSerializer.Deserialize("{}", info);
            return (IAgentMessage?)envelope.Payload.Deserialize(info);
        }
        catch (JsonException ex)
        {
            throw new ProtocolException($"Malformed {envelope.Type} payload.", ex);
        }
    }

    public static bool TryDecode<T>(Envelope envelope, [NotNullWhen(true)] out T? message) where T : class, IAgentMessage
    {
        message = envelope.Type == T.Type ? DecodePayload(envelope) as T : null;
        return message is not null;
    }
}

public sealed class ProtocolException : Exception
{
    public ProtocolException(string message) : base(message) { }
    public ProtocolException(string message, Exception inner) : base(message, inner) { }
}
