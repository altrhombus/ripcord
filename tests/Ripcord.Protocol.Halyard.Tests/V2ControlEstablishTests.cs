using System.Security.Cryptography;
using System.Text;
using Ripcord.Protocol.Halyard.Common.Control;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Crypto.V1;
using Ripcord.Protocol.Halyard.Common.Crypto.V2;
using Ripcord.Protocol.Halyard.Session;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// Establishing the v2 control plane from a (synthetic) /sess/init response: the client verifies the console's
/// RP-Hmac over its RP-Pubkey under the shared K16, then ECDHs to the GCM keys. The test plays the console.
/// </summary>
public class V2ControlEstablishTests
{
    [Fact]
    public void TryEstablish_VerifiesConsoleHmac_AndEstablishes()
    {
        using var client = new HalyardV2SessionCrypto(TestSecrets.SyntheticControlSecrets());
        client.GenerateControlPublicKey();
        byte[] k16 = RandomNumberGenerator.GetBytes(16);

        byte[] consolePub = ConsolePublicKey();
        byte[] nonce = RandomNumberGenerator.GetBytes(16);
        byte[] consoleHmac = client.ComputeRpHmac(k16, consolePub); // the same formula the console applies

        SessResponse response = InitResponse(nonce, consolePub, consoleHmac);
        Assert.True(HalyardV2Control.TryEstablish(response, k16, client, out string? problem), problem);
        Assert.Null(problem);
        Assert.True(((IHalyardV2ControlCrypto)client).IsControlEstablished);
    }

    [Fact]
    public void TryEstablish_RejectsAWrongConsoleHmac()
    {
        using var client = new HalyardV2SessionCrypto(TestSecrets.SyntheticControlSecrets());
        client.GenerateControlPublicKey();
        byte[] k16 = RandomNumberGenerator.GetBytes(16);

        byte[] consolePub = ConsolePublicKey();
        // The console's RP-Hmac computed under a DIFFERENT K16 (a token desync, or a wrong bootstrap).
        byte[] wrongHmac = client.ComputeRpHmac(RandomNumberGenerator.GetBytes(16), consolePub);

        SessResponse response = InitResponse(RandomNumberGenerator.GetBytes(16), consolePub, wrongHmac);
        Assert.False(HalyardV2Control.TryEstablish(response, k16, client, out string? problem));
        Assert.NotNull(problem);
        Assert.False(((IHalyardV2ControlCrypto)client).IsControlEstablished);
    }

    [Fact]
    public void TryEstablish_ReportsAMissingHeader()
    {
        using var client = new HalyardV2SessionCrypto(TestSecrets.SyntheticControlSecrets());
        client.GenerateControlPublicKey();
        Assert.True(SessResponse.TryParse(
            Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n"), out SessResponse? bare, out _));
        Assert.False(HalyardV2Control.TryEstablish(bare!, new byte[16], client, out string? problem));
        Assert.Contains("RP-Nonce", problem);
    }

    private static byte[] ConsolePublicKey()
    {
        var (ecdh, pub) = HalyardStreamKeySchedule.GenerateKeyPair(HalyardStreamCurve.NistP521);
        ecdh.Dispose();
        return pub;
    }

    private static SessResponse InitResponse(byte[] nonce, byte[] pubkey, byte[] hmac)
    {
        string head = "HTTP/1.1 200 OK\r\n"
            + $"RP-Nonce: {Convert.ToBase64String(nonce)}\r\n"
            + $"RP-Pubkey: {Convert.ToBase64String(pubkey)}\r\n"
            + $"RP-Hmac: {Convert.ToBase64String(hmac)}\r\n"
            + "Content-Length: 0\r\n\r\n";
        Assert.True(SessResponse.TryParse(Encoding.ASCII.GetBytes(head), out SessResponse? response, out _));
        return response!;
    }
}
