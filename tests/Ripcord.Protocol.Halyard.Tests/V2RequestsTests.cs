using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Ripcord.Core.Net.Crypto;
using Ripcord.Protocol.Halyard.Common.Control;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Crypto.V1;
using Ripcord.Protocol.Halyard.Common.Crypto.V2;
using Ripcord.Protocol.Halyard.Session;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The v2 control-plane HTTP request builders (spec §4): the /sess/init header set, and /sess/init2 (the
/// GCM-sealed RP-VerifyKey). init2 is round-tripped against a synthetic console that strips RP-Data/RP-Tag,
/// recomputes the AAD from the bytes we sent, and opens the envelope.
/// </summary>
public class V2RequestsTests
{
    private static readonly byte[] N16 = TestSecrets.SyntheticControlSecrets().ContextKeys.SelectorOne.ToArray();

    [Fact]
    public void V2InitRequest_CarriesPubkeyHmacSupportCmdAndFeature()
    {
        byte[] pubkey = new byte[133];
        pubkey[0] = 0x04;
        byte[] hmac = new byte[32];
        for (int i = 0; i < 32; i++) hmac[i] = (byte)i;

        SessRequest request = HalyardV2Requests.BuildInitRequest(
            HalyardConsolePlatform.Ps5, "192.0.2.1:9295", registrationKey: null, pubkey, hmac);

        Assert.Contains("/sess/init", request.Path);
        Assert.Equal("HTTP/1.2", request.HttpVersion); // v2 init is HTTP/1.2 (cap 004)
        var headers = HeadersOf(request);
        Assert.Equal(Convert.ToBase64String(pubkey), headers[SessProtocol.HeaderPubKey]);
        Assert.Equal(Convert.ToBase64String(hmac), headers[SessProtocol.HeaderHmac]);
        Assert.Equal(SessProtocol.V2SupportCmd, headers[SessProtocol.HeaderSupportCmd]); // 120000, not the v1 value
        Assert.Equal("1", headers[SessProtocol.HeaderFeature]);
        Assert.Equal("1.0", headers["Rp-Version"]);
    }

    [Fact]
    public void V2Init2Request_SealsTheVerifyKey_AndTheConsoleOpensIt()
    {
        using var client = new HalyardV2SessionCrypto(TestSecrets.SyntheticControlSecrets());
        byte[] clientPub = client.GenerateControlPublicKey();
        var (consoleEcdh, consolePub) = HalyardStreamKeySchedule.GenerateKeyPair(HalyardStreamCurve.NistP521);
        byte[] k16 = RandomNumberGenerator.GetBytes(16);
        Assert.True(client.EstablishControl(consolePub, k16));
        byte[] z = HalyardStreamKeySchedule.DeriveSharedSecret(consoleEcdh, clientPub);
        var skC2S = HalyardStreamKeySchedule.DeriveDirection(z, k16, 0x09); // purpose 9 = client->console
        consoleEcdh.Dispose();

        byte[] verifyKey = HalyardSessionToken.Mint();
        SessRequest init2 = HalyardV2Requests.BuildInit2Request(client, verifyKey, HalyardConsolePlatform.Ps5, "192.0.2.1:9295");

        Assert.Contains("/sess/init2", init2.Path);
        Assert.Equal("HTTP/1.2", init2.HttpVersion);

        // Console side: strip RP-Data/RP-Tag, rebuild the AAD from what we sent, open at purpose 9 / counter 0.
        (byte[] aad, byte[] ct, byte[] tag) = ConsoleView(init2);
        byte[] iv = Iv(skC2S, 0);
        byte[]? plaintext = AesGcmCore.Decrypt(skC2S.AesKey, iv, aad, ct, tag);

        Assert.NotNull(plaintext);
        Assert.Equal($"RP-VerifyKey: {Convert.ToBase64String(verifyKey)}\r\n", Encoding.ASCII.GetString(plaintext!));
    }

    [Fact]
    public void V2CtrlRequest_SealsTheFieldBlock_AndTheConsoleOpensIt()
    {
        using var client = new HalyardV2SessionCrypto(TestSecrets.SyntheticControlSecrets());
        byte[] clientPub = client.GenerateControlPublicKey();
        var (consoleEcdh, consolePub) = HalyardStreamKeySchedule.GenerateKeyPair(HalyardStreamCurve.NistP521);
        byte[] k16 = RandomNumberGenerator.GetBytes(16);
        Assert.True(client.EstablishControl(consolePub, k16));
        byte[] z = HalyardStreamKeySchedule.DeriveSharedSecret(consoleEcdh, clientPub);
        var skC2S = HalyardStreamKeySchedule.DeriveDirection(z, k16, 0x09);
        consoleEcdh.Dispose();

        SessRequest ctrl = HalyardV2Requests.BuildCtrlRequest(
            client, HalyardConsolePlatform.Ps5, "192.0.2.1:9295",
            registrationKey: "6c9b1c31"u8.ToArray(), deviceId: new byte[16], osMajor: 10, osMinor: 0,
            startBitrate: 0, streamingType: 1, connectionPath: 1);

        Assert.Contains("/sess/ctrl", ctrl.Path);
        Assert.Equal("HTTP/1.1", ctrl.HttpVersion);

        // ctrl AAD is empty; open the whole block envelope at purpose 9 / counter 0.
        var h = HeadersOf(ctrl);
        byte[] ct = Convert.FromBase64String(h[SessProtocol.HeaderData]);
        byte[] tag = Convert.FromBase64String(h[SessProtocol.HeaderTag]);
        byte[]? plaintext = AesGcmCore.Decrypt(skC2S.AesKey, Iv(skC2S, 0), ReadOnlySpan<byte>.Empty, ct, tag);

        Assert.NotNull(plaintext);
        string block = Encoding.ASCII.GetString(plaintext!);
        Assert.Contains("RP-Auth: ", block);
        Assert.Contains("RP-SupportCmd: 120000\r\n", block);     // the v2 capability value
        Assert.Contains("RP-PadProcNo: 3\r\n", block);           // v2 ctrl uses 3 (v1 used 2)
        Assert.Contains("RP-ReqMoreInfo: 4097\r\n", block);
    }

    // What the console receives: AAD = the header lines minus RP-Data/RP-Tag (CRLF-joined + CRLF), and the two
    // base64 values. Mirrors how a console would read our request off the wire.
    private static (byte[] Aad, byte[] Ct, byte[] Tag) ConsoleView(SessRequest request)
    {
        string[] lines = Encoding.ASCII.GetString(request.Serialize()).Split("\r\n");
        var kept = new List<string>();
        string? data = null, tag = null;
        for (int i = 1; i < lines.Length; i++) // skip the request line
        {
            string line = lines[i];
            if (line.Length == 0) break; // blank line ends the headers
            if (line.StartsWith("RP-Data: ", StringComparison.Ordinal)) { data = line["RP-Data: ".Length..]; continue; }
            if (line.StartsWith("RP-Tag: ", StringComparison.Ordinal)) { tag = line["RP-Tag: ".Length..]; continue; }
            kept.Add(line);
        }

        byte[] aad = Encoding.ASCII.GetBytes(string.Join("\r\n", kept) + "\r\n");
        return (aad, Convert.FromBase64String(data!), Convert.FromBase64String(tag!));
    }

    private static byte[] Iv(in HalyardStreamKeySchedule.DirectionKeys sk, ulong counter)
    {
        byte[] data = new byte[16 + sizeof(ulong)];
        sk.BaseIv.CopyTo(data, 0);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(16), counter);
        return HMACSHA256.HashData(N16, data)[..16];
    }

    private static Dictionary<string, string> HeadersOf(SessRequest request)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in Encoding.ASCII.GetString(request.Serialize()).Split("\r\n"))
        {
            int i = line.IndexOf(": ", StringComparison.Ordinal);
            if (i > 0)
                map[line[..i]] = line[(i + 2)..];
        }

        return map;
    }
}
