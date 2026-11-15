namespace DomainNotice.Shared.Protocol;

/// <summary>
/// A framed message channel over an (already authenticated) stream, e.g. an SslStream.
/// Writes are serialised; one reader at a time. Used by both Server and Agent.
/// </summary>
public sealed class FrameConnection : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly int _maxBodyBytes;

    public FrameConnection(Stream stream, int maxBodyBytes = ProtocolInfo.DefaultMaxFrameBytes)
    {
        _stream = stream;
        _maxBodyBytes = maxBodyBytes;
    }

    public Stream Stream => _stream;

    /// <summary>Sends a message; returns the envelope id (use it to match a reply).</summary>
    public async Task<Guid> SendAsync<T>(T message, Guid? replyTo = null, CancellationToken ct = default) where T : IAgentMessage
    {
        var envelope = ProtocolCodec.Wrap(message, replyTo);
        await WriteRawAsync(Framing.BuildJson(ProtocolCodec.EncodeEnvelope(envelope)), ct).ConfigureAwait(false);
        return envelope.Id;
    }

    public Task SendChunkAsync(Guid transferId, long offset, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (data.Length + PackageChunk.HeaderSize > _maxBodyBytes)
            throw new ArgumentException("Chunk does not fit in a frame.", nameof(data));
        return WriteRawAsync(Framing.BuildChunk(transferId, offset, data.Span), ct);
    }

    private async Task WriteRawAsync(byte[] frame, CancellationToken ct)
    {
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
    /// Reads the next frame. Returns null at end of stream.
    /// </summary>
    /// <exception cref="ProtocolException">Bad frame; the caller must close the connection.</exception>
    public async Task<IncomingFrame?> ReadAsync(CancellationToken ct = default)
    {
        var frame = await Framing.ReadAsync(_stream, _maxBodyBytes, ct).ConfigureAwait(false);
        if (frame is null) return null;
        return frame.Value.Kind switch
        {
            FrameKind.Json => IncomingFrame.FromEnvelope(ProtocolCodec.DecodeEnvelope(frame.Value.Body.Span)),
            FrameKind.Chunk => IncomingFrame.FromChunk(PackageChunk.Parse(frame.Value.Body)),
            _ => throw new ProtocolException("Unknown frame kind."),
        };
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }
}

public sealed class IncomingFrame
{
    public Envelope? Envelope { get; private init; }
    /// <summary>Decoded payload, or null for an unknown message type (receivers ignore it).</summary>
    public IAgentMessage? Message { get; private init; }
    public PackageChunk? Chunk { get; private init; }

    public static IncomingFrame FromEnvelope(Envelope envelope) =>
        new() { Envelope = envelope, Message = ProtocolCodec.DecodePayload(envelope) };

    public static IncomingFrame FromChunk(PackageChunk chunk) => new() { Chunk = chunk };
}
