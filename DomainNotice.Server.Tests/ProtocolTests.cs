using DomainNotice.Shared;
using DomainNotice.Shared.Protocol;

namespace DomainNotice.Server.Tests;

public class ProtocolTests
{
    [Fact]
    public async Task Json_frame_round_trips()
    {
        var ms = new MemoryStream();
        var conn = new FrameConnection(ms);
        var hello = new Hello
        {
            AgentVersion = "1.0.0", ComputerName = "PC-1", LocalIps = ["10.0.0.5"],
            Sessions = [new SessionInfo { SessionId = 2, UserName = @"CORP\alice", State = SessionState.Locked }],
        };
        var id = await conn.SendAsync(hello, ct: TestContext.Current.CancellationToken);

        ms.Position = 0;
        var frame = await new FrameConnection(ms).ReadAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(frame);
        Assert.Equal(id, frame.Envelope!.Id);
        Assert.Equal("Hello", frame.Envelope.Type);
        Assert.Equal(ProtocolInfo.Version, frame.Envelope.V);
        var decoded = Assert.IsType<Hello>(frame.Message);
        Assert.Equal("PC-1", decoded.ComputerName);
        Assert.Equal(SessionState.Locked, decoded.Sessions[0].State);
    }

    [Fact]
    public async Task Chunk_frame_round_trips()
    {
        var ms = new MemoryStream();
        var task = Guid.NewGuid();
        await new FrameConnection(ms).SendChunkAsync(task, 262144, new byte[] { 1, 2, 3 }, TestContext.Current.CancellationToken);
        ms.Position = 0;
        var frame = await new FrameConnection(ms).ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(task, frame!.Chunk!.Value.TransferId);
        Assert.Equal(262144, frame.Chunk.Value.Offset);
        Assert.Equal(new byte[] { 1, 2, 3 }, frame.Chunk.Value.Data.ToArray());
    }

    [Fact]
    public async Task Oversized_frame_is_refused()
    {
        var bytes = Framing.BuildJson(new byte[2000]);
        await Assert.ThrowsAsync<ProtocolException>(async () =>
            await Framing.ReadAsync(new MemoryStream(bytes), 1000, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Wire_format_is_frozen()
    {
        // 4-byte big-endian body length, kind byte, body.
        var frame = Framing.BuildJson("{}"u8);
        Assert.Equal(new byte[] { 0, 0, 0, 2, 0, (byte)'{', (byte)'}' }, frame);
        var json = System.Text.Encoding.UTF8.GetString(ProtocolCodec.Encode(new Ping(), id: Guid.Empty));
        Assert.Equal("""{"v":1,"type":"Ping","id":"00000000-0000-0000-0000-000000000000","payload":{}}""", json);
    }

    [Fact]
    public void Unknown_type_decodes_to_null()
    {
        var env = ProtocolCodec.DecodeEnvelope("""{"v":9,"type":"FutureThing","id":"00000000-0000-0000-0000-000000000001","payload":{"x":1}}"""u8);
        Assert.Null(ProtocolCodec.DecodePayload(env));
    }

    [Fact]
    public void Fingerprint_normalizes()
    {
        var fp = new string('a', 64);
        Assert.Equal(fp, KeyFingerprint.Normalize(KeyFingerprint.Display(fp)));
        Assert.Null(KeyFingerprint.Normalize("abc"));
    }
}
