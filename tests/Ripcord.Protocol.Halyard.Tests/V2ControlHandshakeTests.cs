using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Ripcord.Core.Net.Crypto;
using Ripcord.Protocol.Halyard.Common.Control;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Crypto.V1;
using Ripcord.Protocol.Halyard.Common.Crypto.V2;
using Ripcord.Protocol.Halyard.Session;
using Ripcord.Protocol.Halyard.Transport;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The v2 control handshake (init → establish → init2 → ctrl) driven end-to-end against a <see cref="MockV2Console"/>
/// that plays the console side of Path A. This validates the whole control sequence offline, before the live
/// ConnectAsync wiring and the hardware run.
/// </summary>
public class V2ControlHandshakeTests
{
    private static readonly IPEndPoint Endpoint = new(IPAddress.Loopback, 9295);

    [Fact]
    public async Task Handshake_CompletesAgainstAMockConsole_AndDeliversTheVerifyKey()
    {
        byte[] k16 = RandomNumberGenerator.GetBytes(16);
        await using var console = new MockV2Console(k16);
        using var crypto = new HalyardV2SessionCrypto(TestSecrets.SyntheticControlSecrets());
        byte[] verifyKey = HalyardSessionToken.Mint();

        V2ControlHandshakeResult result = await HalyardV2ControlHandshake.RunAsync(
            console, crypto, Endpoint, "192.0.2.1:9295", HalyardConsolePlatform.Ps5,
            registrationKey: "6c9b1c31"u8.ToArray(), companion: new byte[16], k16: k16, deviceId: new byte[16],
            osMajor: 10, osMinor: 0, startBitrate: 0, streamingType: 1, connectionPath: 1, verifyKey: verifyKey,
            CancellationToken.None);

        Assert.True(result.Success, result.Failure);
        Assert.False(result.TokenDesync);
        Assert.NotNull(console.ReceivedVerifyKey);
        Assert.Equal(Convert.ToHexString(verifyKey), Convert.ToHexString(console.ReceivedVerifyKey!));
        Assert.NotNull(result.CtrlResponsePlaintext);
        Assert.Contains("RP-Server-IPv4:", Encoding.ASCII.GetString(result.CtrlResponsePlaintext!));
    }

    [Fact]
    public async Task Handshake_ReportsTokenDesync_On80108d6c()
    {
        byte[] k16 = RandomNumberGenerator.GetBytes(16);
        await using var console = new MockV2Console(k16) { RejectInit = true };
        using var crypto = new HalyardV2SessionCrypto(TestSecrets.SyntheticControlSecrets());

        V2ControlHandshakeResult result = await HalyardV2ControlHandshake.RunAsync(
            console, crypto, Endpoint, "192.0.2.1:9295", HalyardConsolePlatform.Ps5,
            registrationKey: "6c9b1c31"u8.ToArray(), companion: new byte[16], k16: k16, deviceId: new byte[16],
            osMajor: 10, osMinor: 0, startBitrate: 0, streamingType: 1, connectionPath: 1,
            verifyKey: HalyardSessionToken.Mint(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.TokenDesync);
    }
}

/// <summary>A minimal console for the v2 control plane: it runs the console side of Path A over the control
/// channel so the client's handshake can be driven offline. Not wire-faithful beyond what the handshake reads.</summary>
internal sealed class MockV2Console(byte[] k16) : IHalyardControlChannel
{
    private static readonly byte[] N16 = TestSecrets.SyntheticControlSecrets().ContextKeys.SelectorOne.ToArray();
    private readonly byte[] _k16 = k16;
    private ECDiffieHellman? _consoleEcdh;
    private HalyardStreamKeySchedule.DirectionKeys? _sk9;  // client -> console
    private HalyardStreamKeySchedule.DirectionKeys? _sk8;  // console -> client

    public bool RejectInit { get; init; }
    public byte[]? ReceivedVerifyKey { get; private set; }

    public Task ConnectAsync(IPEndPoint endpoint, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<SessResponse> SendRequestAsync(SessRequest request, CancellationToken cancellationToken)
    {
        (var headers, byte[] aad) = Parse(request);
        string path = request.Path;
        SessResponse response =
            path.Contains("/sess/init2") ? HandleInit2(headers, aad)
            : path.Contains("/sess/init") ? HandleInit(headers)
            : path.Contains("/sess/ctrl") ? HandleCtrl(headers)
            : Respond(404);
        return Task.FromResult(response);
    }

    private SessResponse HandleInit(Dictionary<string, string> headers)
    {
        if (RejectInit)
            return Respond(403, "RP-Application-Reason: 80108d6c");

        byte[] clientPub = Convert.FromBase64String(headers[SessProtocol.HeaderPubKey]);
        var (ecdh, consolePub) = HalyardStreamKeySchedule.GenerateKeyPair(HalyardStreamCurve.NistP521);
        _consoleEcdh = ecdh;
        byte[] z = HalyardStreamKeySchedule.DeriveSharedSecret(ecdh, clientPub.AsSpan(0, 133));
        _sk9 = HalyardStreamKeySchedule.DeriveDirection(z, _k16, 0x09);
        _sk8 = HalyardStreamKeySchedule.DeriveDirection(z, _k16, 0x08);

        byte[] consoleHmac = HMACSHA256.HashData(_k16, consolePub.AsSpan(0, 133));
        return Respond(200,
            $"RP-Nonce: {Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))}",
            "RP-Version: 1.0",
            $"RP-Pubkey: {Convert.ToBase64String(consolePub)}",
            $"RP-Hmac: {Convert.ToBase64String(consoleHmac)}",
            "RP-SupportCmd: 141000",
            "RP-Feature: 1");
    }

    private SessResponse HandleInit2(Dictionary<string, string> headers, byte[] aad)
    {
        byte[] plaintext = Open(_sk9!.Value, aad, headers);
        string line = Encoding.ASCII.GetString(plaintext).TrimEnd('\r', '\n');
        ReceivedVerifyKey = Convert.FromBase64String(line["RP-VerifyKey: ".Length..]);
        return Respond(200, "RP-Version: 1.0", "RP-SupportCmd: 141000");
    }

    private SessResponse HandleCtrl(Dictionary<string, string> headers)
    {
        _ = Open(_sk9!.Value, [], headers); // verify the client's ctrl envelope opens (AAD empty)

        byte[] responseBlock = Encoding.ASCII.GetBytes(
            "RP-Prohibit: 0\r\nRP-Device: 0\r\nRP-Enterkey: X\r\nRP-Server-IPv4: 192.0.2.5\r\nRP-Vmode: 0\r\nRP-SupportCmd: 141000\r\n");
        var (ct, tag) = AesGcmCore.Encrypt(_sk8!.Value.AesKey, Iv(_sk8.Value), ReadOnlySpan<byte>.Empty, responseBlock);
        return Respond(200,
            $"RP-Data: {Convert.ToBase64String(ct)}",
            $"RP-Tag: {Convert.ToBase64String(tag)}");
    }

    private byte[] Open(in HalyardStreamKeySchedule.DirectionKeys sk, byte[] aad, Dictionary<string, string> headers)
    {
        byte[] ct = Convert.FromBase64String(headers[SessProtocol.HeaderData]);
        byte[] tag = Convert.FromBase64String(headers[SessProtocol.HeaderTag]);
        return AesGcmCore.Decrypt(sk.AesKey, Iv(sk), aad, ct, tag)
               ?? throw new InvalidOperationException("mock console: client envelope did not authenticate");
    }

    private static byte[] Iv(in HalyardStreamKeySchedule.DirectionKeys sk)
    {
        byte[] data = new byte[16 + sizeof(ulong)];
        sk.BaseIv.CopyTo(data, 0);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(16), 0UL); // counter 0 (fresh connection per message)
        return HMACSHA256.HashData(N16, data)[..16];
    }

    // Returns the request's headers and the init2 AAD (header lines minus RP-Data/RP-Tag, CRLF-joined + CRLF).
    private static (Dictionary<string, string> Headers, byte[] Aad) Parse(SessRequest request)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        var kept = new List<string>();
        string[] lines = Encoding.ASCII.GetString(request.Serialize()).Split("\r\n");
        for (int i = 1; i < lines.Length && lines[i].Length > 0; i++)
        {
            int c = lines[i].IndexOf(": ", StringComparison.Ordinal);
            if (c <= 0) continue;
            headers[lines[i][..c]] = lines[i][(c + 2)..];
            if (!lines[i].StartsWith("RP-Data: ", StringComparison.Ordinal) &&
                !lines[i].StartsWith("RP-Tag: ", StringComparison.Ordinal))
            {
                kept.Add(lines[i]);
            }
        }

        return (headers, Encoding.ASCII.GetBytes(string.Join("\r\n", kept) + "\r\n"));
    }

    private static SessResponse Respond(int status, params string[] headerLines)
    {
        var sb = new StringBuilder($"HTTP/1.1 {status} X\r\n");
        foreach (string h in headerLines)
            sb.Append(h).Append("\r\n");
        sb.Append("Content-Length: 0\r\n\r\n");
        Assert.True(SessResponse.TryParse(Encoding.ASCII.GetBytes(sb.ToString()), out SessResponse? r, out _));
        return r!;
    }

    public Task SendCtrlMessageAsync(HalyardCtrlMessage message, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<HalyardCtrlMessage?> ReadCtrlMessageAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public ValueTask DisposeAsync()
    {
        _consoleEcdh?.Dispose();
        return ValueTask.CompletedTask;
    }
}
