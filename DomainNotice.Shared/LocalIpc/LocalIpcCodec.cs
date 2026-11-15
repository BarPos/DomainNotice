using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using DomainNotice.Shared.Protocol;

namespace DomainNotice.Shared.LocalIpc;

/// <summary>JSON body of a pipe frame: <c>{ type, payload }</c>. Framing is the agent-link <see cref="Framing"/> (kind 0).</summary>
public sealed record LocalEnvelope
{
    public string Type { get; init; } = "";
    public JsonElement Payload { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(LocalEnvelope))]
[JsonSerializable(typeof(ShowAnnouncement))]
[JsonSerializable(typeof(WithdrawAnnouncement))]
[JsonSerializable(typeof(ConnectionStatus))]
[JsonSerializable(typeof(ConfirmReply))]
[JsonSerializable(typeof(ShownReport))]
[JsonSerializable(typeof(ConfirmRequest))]
[JsonSerializable(typeof(RequestPending))]
[JsonSerializable(typeof(ServiceStopping))]
public sealed partial class LocalIpcJsonContext : JsonSerializerContext;

public static class LocalIpcCodec
{
    private static readonly LocalIpcJsonContext Ctx = LocalIpcJsonContext.Default;

    private static readonly Dictionary<string, JsonTypeInfo> ByType = new(StringComparer.Ordinal)
    {
        [ShowAnnouncement.Type] = Ctx.ShowAnnouncement,
        [WithdrawAnnouncement.Type] = Ctx.WithdrawAnnouncement,
        [ConnectionStatus.Type] = Ctx.ConnectionStatus,
        [ConfirmReply.Type] = Ctx.ConfirmReply,
        [ShownReport.Type] = Ctx.ShownReport,
        [ConfirmRequest.Type] = Ctx.ConfirmRequest,
        [RequestPending.Type] = Ctx.RequestPending,
        [ServiceStopping.Type] = Ctx.ServiceStopping,
    };

    public static JsonTypeInfo<T> TypeInfo<T>() where T : ILocalMessage => (JsonTypeInfo<T>)ByType[T.Type];

    /// <summary>Encodes a message into a complete frame (header + JSON body).</summary>
    public static byte[] EncodeFrame<T>(T message) where T : ILocalMessage
    {
        var envelope = new LocalEnvelope
        {
            Type = T.Type,
            Payload = JsonSerializer.SerializeToElement(message, TypeInfo<T>()),
        };
        var body = JsonSerializer.SerializeToUtf8Bytes(envelope, Ctx.LocalEnvelope);
        if (body.Length > LocalPipe.MaxFrameBytes) throw new ProtocolException("Local message too large.");
        return Framing.BuildJson(body);
    }

    /// <summary>Decodes a frame body. Unknown types return null (ignored by the receiver).</summary>
    /// <exception cref="ProtocolException">Malformed body.</exception>
    public static ILocalPayload? Decode(ReadOnlySpan<byte> body)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize(body, Ctx.LocalEnvelope);
            if (envelope is null || string.IsNullOrEmpty(envelope.Type))
                throw new ProtocolException("Local envelope without a type.");
            if (!ByType.TryGetValue(envelope.Type, out var info)) return null;
            if (envelope.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                return (ILocalPayload?)JsonSerializer.Deserialize("{}", info);
            return (ILocalPayload?)envelope.Payload.Deserialize(info);
        }
        catch (JsonException ex)
        {
            throw new ProtocolException("Malformed local message.", ex);
        }
    }
}

public sealed record LocalFrame(ILocalPayload? Message);

/// <summary>A framed local message channel over a pipe stream. Writes are serialised; one reader at a time.</summary>
public sealed class LocalMessageStream : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public LocalMessageStream(Stream stream) => _stream = stream;

    public Stream Stream => _stream;

    public async Task SendAsync<T>(T message, CancellationToken ct = default) where T : ILocalMessage
    {
        var frame = LocalIpcCodec.EncodeFrame(message);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(frame, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Reads the next message. Returns null at end of stream; <see cref="LocalFrame.Message"/> is null for an
    /// unknown message type or a non-JSON frame (ignored).
    /// </summary>
    /// <exception cref="ProtocolException">Bad frame; close the pipe.</exception>
    public async Task<LocalFrame?> ReadAsync(CancellationToken ct = default)
    {
        var frame = await Framing.ReadAsync(_stream, LocalPipe.MaxFrameBytes, ct).ConfigureAwait(false);
        if (frame is null) return null;
        if (frame.Value.Kind != FrameKind.Json) return new LocalFrame(null);
        return new LocalFrame(LocalIpcCodec.Decode(frame.Value.Body.Span));
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }
}
