using System.Buffers.Binary;
using System.Numerics;

namespace DomainNotice.Server.AgentLink;

/// <summary>
/// MD4 (RFC 1320), only to derive the RC4-HMAC Kerberos key (NT hash = MD4 of the UTF-16LE password), because
/// .NET has no MD4 on Linux/macOS. Not used for anything else.
/// </summary>
internal static class Md4
{
    public static byte[] Hash(ReadOnlySpan<byte> data)
    {
        var bitLength = (ulong)data.Length * 8;
        var padded = new byte[((data.Length + 8) / 64 + 1) * 64];
        data.CopyTo(padded);
        padded[data.Length] = 0x80;
        BinaryPrimitives.WriteUInt64LittleEndian(padded.AsSpan(padded.Length - 8), bitLength);

        uint a = 0x67452301, b = 0xefcdab89, c = 0x98badcfe, d = 0x10325476;
        Span<uint> x = stackalloc uint[16];
        for (var offset = 0; offset < padded.Length; offset += 64)
        {
            for (var i = 0; i < 16; i++) x[i] = BinaryPrimitives.ReadUInt32LittleEndian(padded.AsSpan(offset + i * 4));
            uint aa = a, bb = b, cc = c, dd = d;

            static uint F(uint x, uint y, uint z) => (x & y) | (~x & z);
            static uint G(uint x, uint y, uint z) => (x & y) | (x & z) | (y & z);
            static uint H(uint x, uint y, uint z) => x ^ y ^ z;

            ReadOnlySpan<int> s1 = [3, 7, 11, 19];
            for (var i = 0; i < 16; i++)
            {
                var t = BitOperations.RotateLeft(a + F(b, c, d) + x[i], s1[i % 4]);
                (a, b, c, d) = (d, t, b, c);
            }
            ReadOnlySpan<int> s2 = [3, 5, 9, 13];
            ReadOnlySpan<int> o2 = [0, 4, 8, 12, 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15];
            for (var i = 0; i < 16; i++)
            {
                var t = BitOperations.RotateLeft(a + G(b, c, d) + x[o2[i]] + 0x5a827999, s2[i % 4]);
                (a, b, c, d) = (d, t, b, c);
            }
            ReadOnlySpan<int> s3 = [3, 9, 11, 15];
            ReadOnlySpan<int> o3 = [0, 8, 4, 12, 2, 10, 6, 14, 1, 9, 5, 13, 3, 11, 7, 15];
            for (var i = 0; i < 16; i++)
            {
                var t = BitOperations.RotateLeft(a + H(b, c, d) + x[o3[i]] + 0x6ed9eba1, s3[i % 4]);
                (a, b, c, d) = (d, t, b, c);
            }
            a += aa;
            b += bb;
            c += cc;
            d += dd;
        }

        var result = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(result, a);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), b);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), c);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12), d);
        return result;
    }
}
