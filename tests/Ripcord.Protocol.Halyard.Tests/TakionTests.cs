using Avstream;
using Google.Protobuf;
using Ripcord.Protocol.Halyard.Takion;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// Tests for the Takion transport foundation: the 13-byte common header, the SCTP handshake framing
/// (INIT / INIT_ACK / COOKIE_ECHO), and the generated control-plane protobuf. The INIT byte-for-byte
/// vector is the client's own structural packet (a connection tag + the observed constants), cross-checked
/// against the wire-validated handshake in <c>rudp_control_setup.pcapng</c>.
/// </summary>
public class TakionTests
{
    [Fact]
    public void Header_RoundTrips()
    {
        var header = new TakionMessageHeader(TakionMessageHeader.BaseTypeControl, 0x00b18ccf, gmacTag: 0x1a586cb3, keyPosition: 0x00000010);
        Span<byte> buffer = stackalloc byte[TakionMessageHeader.Length];
        header.Write(buffer);

        Assert.True(TakionMessageHeader.TryParse(buffer, out var parsed));
        Assert.Equal(header.BaseType, parsed.BaseType);
        Assert.Equal(header.VerificationTag, parsed.VerificationTag);
        Assert.Equal(header.GmacTag, parsed.GmacTag);
        Assert.Equal(header.KeyPosition, parsed.KeyPosition);
    }

    [Fact]
    public void BuildInit_MatchesWireStructure()
    {
        // The exact INIT a client emits for connection tag 0x00004823: 13-byte header (all-zero: unknown
        // server tag, no GMAC/keypos yet) + a 20-byte INIT chunk { tag, a_rwnd=0x19000, streams=0x64/0x64,
        // initial_tsn=tag }. Verified byte-for-byte against the wire-validated handshake capture.
        var handshake = new TakionHandshake();
        byte[] init = handshake.BuildInit(0x00004823);

        Assert.Equal(
            "000000000000000000000000000100001400004823000190000064006400004823",
            Hex.String(init));
        Assert.Equal(TakionHandshake.HandshakeState.InitSent, handshake.State);
        Assert.Equal(0x00004823u, handshake.LocalTag);
    }

    [Fact]
    public void HandshakeFlow_InitAckThenCookieEcho()
    {
        var handshake = new TakionHandshake();
        handshake.BuildInit(0x00004823);

        // Construct a well-formed INIT_ACK (server tag 0x00b18ccf + a synthetic 32-byte cookie).
        byte[] cookie = new byte[TakionHandshake.CookieLength];
        for (int i = 0; i < cookie.Length; i++) cookie[i] = (byte)(0xC0 + i);
        byte[] initAck = BuildInitAck(localTag: 0x00004823, serverTag: 0x00b18ccf, cookie);

        Assert.True(handshake.TryHandleInitAck(initAck, out byte[] recovered));
        Assert.Equal(Hex.String(cookie), Hex.String(recovered));
        Assert.Equal(0x00b18ccfu, handshake.RemoteTag);

        // COOKIE_ECHO uses the server's tag in the header and echoes the cookie verbatim.
        byte[] echo = handshake.BuildCookieEcho(recovered);
        Assert.True(TakionMessageHeader.TryParse(echo, out var echoHeader));
        Assert.Equal(0x00b18ccfu, echoHeader.VerificationTag);
        Assert.Equal((byte)SctpChunkType.CookieEcho, echo[TakionMessageHeader.Length]);
        Assert.Equal(Hex.String(cookie), Hex.String(echo.AsSpan(TakionMessageHeader.Length + 4)));

        // A COOKIE_ACK (echoing our tag) establishes the connection.
        byte[] cookieAck = BuildBareChunk(localTag: 0x00004823, SctpChunkType.CookieAck);
        Assert.True(handshake.TryComplete(cookieAck));
        Assert.Equal(TakionHandshake.HandshakeState.Established, handshake.State);
    }

    [Fact]
    public void TryHandleInitAck_RejectsWrongTag()
    {
        var handshake = new TakionHandshake();
        handshake.BuildInit(0x00004823);
        byte[] initAck = BuildInitAck(localTag: 0x00009999, serverTag: 0x00b18ccf, new byte[TakionHandshake.CookieLength]);
        Assert.False(handshake.TryHandleInitAck(initAck, out _));
    }

    [Fact]
    public void ControlMessage_ProtobufRoundTrips()
    {
        var message = new ControlMessage
        {
            Type = ControlMessage.Types.MessageType.SessionRequest,
            SessionRequestPayload = new SessionRequestPayload
            {
                ClientVersion = 9,
                SessionKey = "session-key",
                LaunchSpecJson = "{}",
                EcdhPublicKey = ByteString.CopyFrom(new byte[65]),
                EcdhSignature = ByteString.CopyFrom(new byte[32]),
            },
        };

        byte[] wire = message.ToByteArray();
        var parsed = ControlMessage.Parser.ParseFrom(wire);

        Assert.Equal(ControlMessage.Types.MessageType.SessionRequest, parsed.Type);
        Assert.Equal(9u, parsed.SessionRequestPayload.ClientVersion);
        Assert.Equal("session-key", parsed.SessionRequestPayload.SessionKey);
        Assert.Equal(65, parsed.SessionRequestPayload.EcdhPublicKey.Length);
    }

    // ---- helpers that synthesize server-side packets for the flow test ----

    private static byte[] BuildInitAck(uint localTag, uint serverTag, byte[] cookie)
    {
        int chunkLength = 4 + 16 + cookie.Length;
        var packet = new byte[TakionMessageHeader.Length + chunkLength];
        var span = packet.AsSpan();
        new TakionMessageHeader(TakionMessageHeader.BaseTypeControl, verificationTag: localTag).Write(span);

        var chunk = span[TakionMessageHeader.Length..];
        chunk[0] = (byte)SctpChunkType.InitAck;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(chunk[2..], (ushort)chunkLength);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(chunk[4..], serverTag);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(chunk[8..], TakionHandshake.DefaultReceiveWindow);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(chunk[12..], TakionHandshake.DefaultOutStreams);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(chunk[14..], TakionHandshake.DefaultInStreams);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(chunk[16..], serverTag);
        cookie.CopyTo(chunk[20..]);
        return packet;
    }

    private static byte[] BuildBareChunk(uint localTag, SctpChunkType type)
    {
        var packet = new byte[TakionMessageHeader.Length + 4];
        var span = packet.AsSpan();
        new TakionMessageHeader(TakionMessageHeader.BaseTypeControl, verificationTag: localTag).Write(span);
        span[TakionMessageHeader.Length] = (byte)type;
        return packet;
    }
}
