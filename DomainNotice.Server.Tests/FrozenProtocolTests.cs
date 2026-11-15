using System.Text;
using DomainNotice.Shared;
using DomainNotice.Shared.Protocol;

namespace DomainNotice.Server.Tests;

/// <summary>
/// Plan 11.1: framing, chunk layout and Hello, Welcome, UpdateAgent, PackageRequest, UpdateStatus are frozen from
/// 1.0.0 so any old agent can always be told to update. These golden 1.0.0 payloads must keep decoding and the
/// encoder must keep producing the same field names. New optional fields may be added (and appear in these strings
/// only when set); never rename or remove one. When a new agent version ships, add its recorded messages here.
/// </summary>
public class FrozenProtocolTests
{
    private static readonly Guid Id = Guid.Parse("11111111-2222-3333-4444-555555555555");

    public static TheoryData<string, string> Golden_1_0_0 => new()
    {
        { "Hello", """{"v":1,"type":"Hello","id":"11111111-2222-3333-4444-555555555555","payload":{"agentVersion":"1.0.0","computerName":"PC-1","dnsName":"pc-1.corp.example","osVersion":"Windows 11 Pro 24H2","localIps":["10.0.0.5"],"sessions":[{"sessionId":2,"userName":"CORP\\alice","state":"Active","logonTimeUtc":"2026-11-15T08:00:00Z"}]}}""" },
        { "Welcome", """{"v":1,"type":"Welcome","id":"11111111-2222-3333-4444-555555555555","payload":{"compatible":true,"serverVersion":"1.0.0"}}""" },
        { "UpdateAgent", """{"v":1,"type":"UpdateAgent","id":"11111111-2222-3333-4444-555555555555","payload":{"taskId":"11111111-2222-3333-4444-555555555555","version":"1.0.0","size":123456,"sha256":"aa"}}""" },
        { "PackageRequest", """{"v":1,"type":"PackageRequest","id":"11111111-2222-3333-4444-555555555555","payload":{"taskId":"11111111-2222-3333-4444-555555555555","offset":262144}}""" },
        { "UpdateStatus", """{"v":1,"type":"UpdateStatus","id":"11111111-2222-3333-4444-555555555555","payload":{"taskId":"11111111-2222-3333-4444-555555555555","state":"Failed","error":"hash mismatch"}}""" },
    };

    [Theory]
    [MemberData(nameof(Golden_1_0_0))]
    public void Frozen_messages_decode_and_re_encode_identically(string type, string json)
    {
        var envelope = ProtocolCodec.DecodeEnvelope(Encoding.UTF8.GetBytes(json));
        Assert.Equal(type, envelope.Type);
        var message = ProtocolCodec.DecodePayload(envelope);
        Assert.NotNull(message);

        var reencoded = message switch
        {
            Hello m => ProtocolCodec.Encode(m, id: Id),
            Welcome m => ProtocolCodec.Encode(m, id: Id),
            UpdateAgent m => ProtocolCodec.Encode(m, id: Id),
            PackageRequest m => ProtocolCodec.Encode(m, id: Id),
            UpdateStatus m => ProtocolCodec.Encode(m, id: Id),
            _ => throw new InvalidOperationException(type),
        };
        Assert.Equal(json, Encoding.UTF8.GetString(reencoded));
    }

    [Fact]
    public void Decoded_values_match()
    {
        var json = Golden_1_0_0.Select(r => r.Data).First(d => d.Item1 == "Hello").Item2;
        var hello = (Hello)ProtocolCodec.DecodePayload(ProtocolCodec.DecodeEnvelope(Encoding.UTF8.GetBytes(json)))!;
        Assert.Equal(@"CORP\alice", hello.Sessions[0].UserName);
        Assert.Equal(SessionState.Active, hello.Sessions[0].State);
        Assert.Equal(DateTimeKind.Utc, hello.Sessions[0].LogonTimeUtc.Kind);
    }

    [Fact]
    public void Chunk_layout_is_frozen()
    {
        var frame = Framing.BuildChunk(Id, 0x0102030405060708, [0xAB]);
        // length (4, BE) = 16 + 8 + 1, kind 1, RFC 4122 big-endian GUID bytes, BE offset, data.
        Assert.Equal(
            "00000019" + "01" + "11111111222233334444555555555555" + "0102030405060708" + "ab",
            Convert.ToHexStringLower(frame));
    }
}
