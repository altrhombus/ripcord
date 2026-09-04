using System.Linq;
using System.Text;
using System.Text.Json;
using Ripcord.Protocol.Halyard.Common.Control;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The account-route control transport, against the real datagrams of a captured pairing.
///
/// <para>
/// This is the test that makes the derivation in <c>docs/protocol/ps5-session-transport.md</c> mean something:
/// the codec is fed the console's own bytes and has to agree about where every field sits. The synthetic
/// round-trips in <see cref="HalyardControlChunkTests"/> cannot do that — a codec is self-consistent by
/// construction, which is exactly the trap the account seed tests fell into before their live vector existed.
/// </para>
///
/// <para>
/// Fixture: <c>docs/protocol/captures/account_transport_vectors.json</c> (gitignored — it carries per-session
/// material and the encrypted pairing record), so these self-skip on a clean checkout.
/// </para>
/// </summary>
public class LiveAccountTransportVectorTests
{
    [SkippableFact]
    public void Prelude_ParsesTheCapturedInitExchange()
    {
        Fixture fx = LoadOrSkip();

        Assert.True(HalyardControlPrelude.TryParse(Hex.Bytes(fx.PreludeClientInit!), out var client));
        Assert.True(HalyardControlPrelude.TryParse(Hex.Bytes(fx.PreludeConsoleInit!), out var console));
        Assert.True(HalyardControlPrelude.TryParse(Hex.Bytes(fx.PreludeClientCookieEcho!), out var echo));

        Assert.Equal(HalyardControlPrelude.Init, client.Type);
        Assert.Equal(HalyardControlPrelude.Init, console.Type);
        Assert.Equal(HalyardControlPrelude.CookieEcho, echo.Type);

        // The identity claim: each side names itself first and its peer second, so the console's view is the
        // mirror of ours. This is what says the 20-byte blobs are the two localHashedIds rather than one
        // opaque 40-byte lump.
        Assert.Equal(client.SenderId.ToArray(), console.PeerId.ToArray());
        Assert.Equal(client.PeerId.ToArray(), console.SenderId.ToArray());

        // The pair comes back with its halves exchanged.
        Assert.Equal(client.SwappedTagPair, console.TagPair);

        // The token exchange: each side announces its own in its Init, and the echo returns the CONSOLE's.
        // Asserted in this direction because the first draft had it backwards — see HalyardControlPrelude.
        Assert.NotEqual(0u, client.Token);
        Assert.NotEqual(0u, console.Token);
        Assert.NotEqual(client.Token, console.Token);
        Assert.Equal(console.Token, echo.Token);

        // The echo re-asserts our own identity, unchanged from the Init.
        Assert.Equal(client.SenderId.ToArray(), echo.SenderId.ToArray());
        Assert.Equal(client.PeerId.ToArray(), echo.PeerId.ToArray());
    }

    [SkippableFact]
    public void Handshake_ChunksParseWithTheExpectedTypes()
    {
        Fixture fx = LoadOrSkip();

        Assert.Equal(HalyardControlChunkType.Hello, Single(fx.HelloClient!).Type);
        Assert.Equal(HalyardControlChunkType.Cookie, Single(fx.CookieConsole!).Type);
        Assert.Equal(HalyardControlChunkType.HelloEcho, Single(fx.HelloEchoClient!).Type);
        Assert.Equal(HalyardControlChunkType.Accept, Single(fx.AcceptConsole!).Type);
        Assert.Equal(HalyardControlChunkType.Close, Single(fx.CloseConsole!).Type);
    }

    [SkippableFact]
    public void HelloEcho_RepeatsTheHelloBodyAndAppendsTheConsolesCookieRegion()
    {
        // The cookie-echo shape, and the reason the handshake is four packets rather than two: the console
        // commits no state until it has seen its own cookie come back.
        Fixture fx = LoadOrSkip();

        HalyardControlChunk hello = Single(fx.HelloClient!);
        HalyardControlChunk echo = Single(fx.HelloEchoClient!);
        HalyardControlChunk cookie = Single(fx.CookieConsole!);

        Assert.Equal(hello.Body.ToArray(), echo.Body[..hello.Body.Length].ToArray());

        byte[] appended = echo.Body[hello.Body.Length..].ToArray();
        Assert.Equal(cookie.Body[^appended.Length..].ToArray(), appended);
    }

    [SkippableFact]
    public void Request_IsAnAcknowledgementFollowedByTheHttpPost_InOneDatagram()
    {
        // Concatenation is not a theoretical case in this protocol; the registration POST is exactly this
        // shape, and a reader that stopped after the first chunk would never see the request at all.
        Fixture fx = LoadOrSkip();

        List<HalyardControlChunk> chunks = HalyardControlChunkCodec.ReadAll(Hex.Bytes(fx.RequestClient!));

        Assert.Equal(2, chunks.Count);
        Assert.Equal(HalyardControlChunkType.Ack, chunks[0].Type);
        Assert.Equal(HalyardControlChunkType.Data, chunks[1].Type);

        // The data chunk's body is a 2-byte sequence then the HTTP request itself.
        string http = Encoding.ASCII.GetString(chunks[1].Body[2..].Span);
        Assert.StartsWith("POST /sie/ps5/rp/sess/rgst HTTP/1.1\r\n", http);
    }

    [SkippableFact]
    public void Response_IsOneDataChunkCarryingTheHttpReply()
    {
        Fixture fx = LoadOrSkip();

        HalyardControlChunk response = Single(fx.ResponseConsole!);

        Assert.Equal(HalyardControlChunkType.Data, response.Type);
        string http = Encoding.ASCII.GetString(response.Body[2..].Span);
        Assert.StartsWith("HTTP/1.1 200 OK\r\n", http);

        // And the existing HTTP splitter reads it, which is the point of the whole exercise: the transport is
        // new, the application layer above it is not.
        Assert.True(Common.Crypto.HalyardRegistrationMessage.TrySplitResponse(
            Encoding.ASCII.GetBytes(http), out int status, out byte[] body, out _));
        Assert.Equal(200, status);
        Assert.NotEmpty(body);
    }

    [SkippableFact]
    public void EveryCapturedDatagram_ParsesWholly_WithNothingLeftOver()
    {
        // The strongest single statement the fixture can make about the framing: every byte of every datagram
        // is accounted for by the chunk layer, so no field is the wrong width.
        Fixture fx = LoadOrSkip();

        foreach ((string name, string hex) in new[]
                 {
                     (nameof(fx.HelloClient), fx.HelloClient!),
                     (nameof(fx.CookieConsole), fx.CookieConsole!),
                     (nameof(fx.HelloEchoClient), fx.HelloEchoClient!),
                     (nameof(fx.AcceptConsole), fx.AcceptConsole!),
                     (nameof(fx.RequestClient), fx.RequestClient!),
                     (nameof(fx.ResponseConsole), fx.ResponseConsole!),
                     (nameof(fx.CloseConsole), fx.CloseConsole!),
                 })
        {
            byte[] datagram = Hex.Bytes(hex);

            int consumed = 0;
            while (consumed < datagram.Length
                   && HalyardControlChunkCodec.TryRead(datagram.AsSpan(consumed), out _, out int step))
            {
                consumed += step;
            }

            Assert.Equal(datagram.Length, consumed);
        }
    }

    // ---- fixture ------------------------------------------------------------------------------

    private static HalyardControlChunk Single(string hex)
    {
        List<HalyardControlChunk> chunks = HalyardControlChunkCodec.ReadAll(Hex.Bytes(hex));
        return Assert.Single(chunks);
    }

    [SkippableFact]
    public void Prelude_TheEchosTailReflectsTheConsolesOwnEndpoint()
    {
        // The field that was carried as "[X] unexplained, six populated bytes" and sent as zeroes. It is the
        // peer's address and port XORed with the tag pair — STUN's XOR-MAPPED-ADDRESS trick with the tag pair
        // standing in for the magic cookie. This is the assertion that makes that claim mean something: the
        // tail is recomputed from the console endpoint the captured client was sending to, and has to equal
        // the captured bytes exactly.
        Fixture fx = LoadOrSkip();
        Skip.If(string.IsNullOrEmpty(fx.ConsoleAddress), "Fixture predates consoleAddress/consolePort.");

        Assert.True(HalyardControlPrelude.TryParse(Hex.Bytes(fx.PreludeClientCookieEcho!), out var echo));

        byte[] address = [.. fx.ConsoleAddress!.Split('.').Select(byte.Parse)];
        byte[] expected = HalyardControlPrelude.ReflectPeerEndpoint(
            address, (ushort)fx.ConsolePort, echo.TagPair);

        Assert.Equal(expected, echo.Tail.ToArray());

        // And the Init carries none of it — only the echo reflects, which is what makes it a confirmation
        // rather than an advertisement.
        Assert.True(HalyardControlPrelude.TryParse(Hex.Bytes(fx.PreludeClientInit!), out var init));
        Assert.True(init.Tail.ToArray().All(b => b == 0));
    }

    [SkippableFact]
    public void HelloEcho_ReturnsTheCookieMinusItsEightByteHeader()
    {
        // The echo is our hello body followed by the cookie's body *from offset 8*, not the whole body. Pinned
        // against the captured bytes because the arithmetic is the whole point: 14 + 34 = 48 and a 56-byte
        // chunk, where returning all 42 gives 64 and a console that will not accept it.
        Fixture fx = LoadOrSkip();

        HalyardControlChunk hello = Assert.Single(
            HalyardControlChunkCodec.ReadAll(Hex.Bytes(fx.HelloClient!)));
        HalyardControlChunk cookie = Assert.Single(
            HalyardControlChunkCodec.ReadAll(Hex.Bytes(fx.CookieConsole!)));
        HalyardControlChunk echo = Assert.Single(
            HalyardControlChunkCodec.ReadAll(Hex.Bytes(fx.HelloEchoClient!)));

        byte[] expected = [.. hello.Body.ToArray(), .. cookie.Body.ToArray()[8..]];

        Assert.Equal(expected, echo.Body.ToArray());
        Assert.Equal(hello.Body.Length + cookie.Body.Length - 8, echo.Body.Length);
        Assert.Equal(new byte[] { 0, 0, 0, 2, 0, 0, 0, 0 }, cookie.Body.ToArray()[..8]);
    }

    private static Fixture LoadOrSkip()
    {
        string path = Locate();
        Skip.IfNot(File.Exists(path), $"no account-transport fixture at {path}");
        return JsonSerializer.Deserialize<Fixture>(File.ReadAllText(path), JsonOpts)
               ?? throw new InvalidOperationException("Fixture failed to deserialize.");
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static string Locate()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            string candidate = Path.Combine(dir, "docs", "protocol", "captures", "account_transport_vectors.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        return Path.Combine(AppContext.BaseDirectory, "account_transport_vectors.json");
    }

    private sealed class Fixture
    {
        public string? PreludeClientInit { get; set; }
        public string? PreludeConsoleInit { get; set; }
        public string? PreludeClientCookieEcho { get; set; }
        public string? ConsoleAddress { get; set; }
        public int ConsolePort { get; set; }
        public string? HelloClient { get; set; }
        public string? CookieConsole { get; set; }
        public string? HelloEchoClient { get; set; }
        public string? AcceptConsole { get; set; }
        public string? RequestClient { get; set; }
        public string? ResponseConsole { get; set; }
        public string? CloseConsole { get; set; }
    }
}
