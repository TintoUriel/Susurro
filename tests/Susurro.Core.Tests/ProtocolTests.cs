using System.Security.Cryptography;
using System.Text;
using Susurro.Core.Protocol;

namespace Susurro.Core.Tests;

public class ProtocolTests
{
    [Fact]
    public void Packet_roundtrips_and_omits_nulls()
    {
        var p = new Packet { T = PacketType.Message, MsgId = new string('a', 32), Seq = 7, Text = "Traé los papeles", Urgent = true, Ts = 123 };
        var bytes = SusurroJson.Serialize(p);
        var json = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("null", json);
        Assert.Contains("\"t\":\"msg\"", json);

        var back = SusurroJson.Deserialize(bytes);
        Assert.Equal(p.T, back.T);
        Assert.Equal(p.MsgId, back.MsgId);
        Assert.Equal(7, back.Seq);
        Assert.Equal("Traé los papeles", back.Text);
        Assert.True(back.Urgent);
        Assert.Null(back.Receipt);
    }

    [Fact]
    public void Byte_fields_are_base64()
    {
        var nonce = HandshakeCrypto.NewNonce();
        var back = SusurroJson.Deserialize(SusurroJson.Serialize(new Packet { T = PacketType.Hello, Nonce = nonce }));
        Assert.Equal(nonce, back.Nonce);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"t\":\"\"}")]
    [InlineData("{\"x\":1}")]
    public void Invalid_json_throws_protocol_exception(string json)
    {
        Assert.Throws<ProtocolException>(() => SusurroJson.Deserialize(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Unknown_fields_are_ignored_for_forward_compatibility()
    {
        var p = SusurroJson.Deserialize(Encoding.UTF8.GetBytes("{\"t\":\"ping\",\"futureField\":{\"a\":1}}"));
        Assert.Equal(PacketType.Ping, p.T);
    }

    [Fact]
    public async Task Frames_roundtrip()
    {
        using var ms = new MemoryStream();
        await FrameIO.WriteFrameAsync(ms, new byte[] { 1, 2, 3 }, default);
        await FrameIO.WriteFrameAsync(ms, new byte[300], default);
        ms.Position = 0;
        Assert.Equal(new byte[] { 1, 2, 3 }, await FrameIO.ReadFrameAsync(ms, 1024, default));
        Assert.Equal(300, (await FrameIO.ReadFrameAsync(ms, 1024, default))!.Length);
        Assert.Null(await FrameIO.ReadFrameAsync(ms, 1024, default)); // EOF limpio
    }

    [Fact]
    public async Task Oversized_frame_is_rejected_before_allocating()
    {
        using var ms = new MemoryStream(new byte[] { 0x7F, 0xFF, 0xFF, 0xFF, 0 });
        await Assert.ThrowsAsync<ProtocolException>(() => FrameIO.ReadFrameAsync(ms, 4096, default));
    }

    [Fact]
    public async Task Truncated_frame_throws()
    {
        using var ms = new MemoryStream(new byte[] { 0, 0, 0, 10, 1, 2 });
        await Assert.ThrowsAnyAsync<EndOfStreamException>(() => FrameIO.ReadFrameAsync(ms, 4096, default));
    }

    [Fact]
    public void Secure_channel_roundtrip_in_both_directions()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var nd = HandshakeCrypto.NewNonce();
        var nl = HandshakeCrypto.NewNonce();
        using var dialer = SecureChannel.Create(key, true, nd, nl);
        using var listener = SecureChannel.Create(key, false, nd, nl);

        for (var i = 0; i < 3; i++)
        {
            var msg = Encoding.UTF8.GetBytes("hola " + i);
            Assert.Equal(msg, listener.Open(dialer.Seal(msg)));
            Assert.Equal(msg, dialer.Open(listener.Seal(msg)));
        }
    }

    [Fact]
    public void Secure_channel_detects_tampering()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var nd = HandshakeCrypto.NewNonce();
        var nl = HandshakeCrypto.NewNonce();
        using var a = SecureChannel.Create(key, true, nd, nl);
        using var b = SecureChannel.Create(key, false, nd, nl);
        var frame = a.Seal(Encoding.UTF8.GetBytes("secreto"));
        frame[0] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => b.Open(frame));
    }

    [Fact]
    public void Secure_channel_detects_replay_and_reordering()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var nd = HandshakeCrypto.NewNonce();
        var nl = HandshakeCrypto.NewNonce();
        using var a = SecureChannel.Create(key, true, nd, nl);
        using var b = SecureChannel.Create(key, false, nd, nl);
        var f1 = a.Seal(new byte[] { 1 });
        var f2 = a.Seal(new byte[] { 2 });
        Assert.ThrowsAny<CryptographicException>(() => b.Open(f2)); // fuera de orden

        using var c = SecureChannel.Create(key, false, nd, nl);
        c.Open(f1);
        Assert.ThrowsAny<CryptographicException>(() => c.Open(f1)); // repetida
    }

    [Fact]
    public void Secure_channel_with_other_key_or_nonces_fails()
    {
        var nd = HandshakeCrypto.NewNonce();
        var nl = HandshakeCrypto.NewNonce();
        using var a = SecureChannel.Create(RandomNumberGenerator.GetBytes(32), true, nd, nl);
        using var b = SecureChannel.Create(RandomNumberGenerator.GetBytes(32), false, nd, nl);
        Assert.ThrowsAny<CryptographicException>(() => b.Open(a.Seal(new byte[] { 1 })));
    }

    [Fact]
    public void Session_proofs_depend_on_role_key_and_nonces()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var nd = HandshakeCrypto.NewNonce();
        var nl = HandshakeCrypto.NewNonce();
        var id1 = new string('1', 32);
        var id2 = new string('2', 32);
        var d = HandshakeCrypto.SessionProof(key, 'D', id1, id2, nd, nl);
        Assert.True(HandshakeCrypto.FixedTimeEquals(d, HandshakeCrypto.SessionProof(key, 'D', id1, id2, nd, nl)));
        Assert.False(HandshakeCrypto.FixedTimeEquals(d, HandshakeCrypto.SessionProof(key, 'L', id1, id2, nd, nl)));
        Assert.False(HandshakeCrypto.FixedTimeEquals(d, HandshakeCrypto.SessionProof(key, 'D', id1, id2, HandshakeCrypto.NewNonce(), nl)));
        Assert.False(HandshakeCrypto.FixedTimeEquals(d, HandshakeCrypto.SessionProof(RandomNumberGenerator.GetBytes(32), 'D', id1, id2, nd, nl)));
        Assert.False(HandshakeCrypto.FixedTimeEquals(d, null));
    }

    [Fact]
    public void Discovery_packet_roundtrip_and_garbage_is_ignored()
    {
        var p = new DiscoveryPacket { T = DiscoveryPacket.Query, Id = new string('c', 32), Name = "Tinto", Port = 47810 };
        var back = SusurroJson.TryDeserializeDiscovery(SusurroJson.Serialize(p));
        Assert.NotNull(back);
        Assert.Equal("Tinto", back!.Name);
        Assert.Equal(47810, back.Port);
        Assert.Equal(ProtocolConstants.Version, back.V);

        Assert.Null(SusurroJson.TryDeserializeDiscovery(Encoding.UTF8.GetBytes("hola")));
        Assert.Null(SusurroJson.TryDeserializeDiscovery(Encoding.UTF8.GetBytes("{\"s\":\"otra-app\",\"id\":\"x\"}")));
    }
}
