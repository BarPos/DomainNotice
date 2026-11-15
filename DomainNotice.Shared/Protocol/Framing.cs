using System.Buffers.Binary;

namespace DomainNotice.Shared.Protocol;

/// <summary>Frame kinds. FROZEN.</summary>
public enum FrameKind : byte
{
    /// <summary>UTF-8 JSON <see cref="Envelope"/>.</summary>
    Json = 0,
    /// <summary>Binary update package chunk, see <see cref="PackageChunk"/>.</summary>
    Chunk = 1,
}

/// <summary>
/// Kind-1 body: 16-byte transfer id (the update task id, RFC 4122 big-endian byte order),
/// 8-byte big-endian offset, then the data. FROZEN.
/// </summary>
public readonly record struct PackageChunk(Guid TransferId, long Offset, ReadOnlyMemory<byte> Data)
{
    public const int HeaderSize = 16 + 8;

    public static PackageChunk Parse(ReadOnlyMemory<byte> body)
    {
        if (body.Length < HeaderSize) throw new ProtocolException("Chunk frame too short.");
        var span = body.Span;
        var id = new Guid(span[..16], bigEndian: true);
        var offset = BinaryPrimitives.ReadInt64BigEndian(span.Slice(16, 8));
        if (offset < 0) throw new ProtocolException("Negative chunk offset.");
        return new PackageChunk(id, offset, body[HeaderSize..]);
    }

    public void WriteHeader(Span<byte> destination)
    {
        if (!TransferId.TryWriteBytes(destination[..16], bigEndian: true, out _))
            throw new InvalidOperationException();
        BinaryPrimitives.WriteInt64BigEndian(destination.Slice(16, 8), Offset);
    }
}

public readonly record struct Frame(FrameKind Kind, ReadOnlyMemory<byte> Body);

/// <summary>
/// Wire framing (plan 3.1), FROZEN: 4-byte big-endian body length, 1-byte <see cref="FrameKind"/>, body.
/// The length counts the body only (not the kind byte). A body larger than the limit closes the connection.
/// </summary>
public static class Framing
{
    public const int HeaderSize = 5;

    /// <summary>Reads one frame. Returns null on a clean end of stream before a header.</summary>
    /// <exception cref="ProtocolException">Oversized frame, unknown kind, or truncated frame.</exception>
    public static async ValueTask<Frame?> ReadAsync(Stream stream, int maxBodyBytes, CancellationToken ct)
    {
        var header = new byte[HeaderSize];
        var read = await ReadAtLeastAsync(stream, header, ct).ConfigureAwait(false);
        if (read == 0) return null;
        if (read < HeaderSize) throw new ProtocolException("Truncated frame header.");

        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length > (uint)maxBodyBytes) throw new ProtocolException($"Frame of {length} bytes exceeds limit {maxBodyBytes}.");
        var kind = (FrameKind)header[4];
        if (kind is not (FrameKind.Json or FrameKind.Chunk)) throw new ProtocolException($"Unknown frame kind {header[4]}.");

        var body = new byte[length];
        if (length > 0 && await ReadAtLeastAsync(stream, body, ct).ConfigureAwait(false) < length)
            throw new ProtocolException("Truncated frame body.");
        return new Frame(kind, body);
    }

    public static byte[] BuildJson(ReadOnlySpan<byte> envelopeUtf8)
    {
        var buffer = new byte[HeaderSize + envelopeUtf8.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)envelopeUtf8.Length);
        buffer[4] = (byte)FrameKind.Json;
        envelopeUtf8.CopyTo(buffer.AsSpan(HeaderSize));
        return buffer;
    }

    public static byte[] BuildChunk(Guid transferId, long offset, ReadOnlySpan<byte> data)
    {
        var bodyLength = PackageChunk.HeaderSize + data.Length;
        var buffer = new byte[HeaderSize + bodyLength];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)bodyLength);
        buffer[4] = (byte)FrameKind.Chunk;
        new PackageChunk(transferId, offset, default).WriteHeader(buffer.AsSpan(HeaderSize));
        data.CopyTo(buffer.AsSpan(HeaderSize + PackageChunk.HeaderSize));
        return buffer;
    }

    private static async ValueTask<int> ReadAtLeastAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[total..], ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
