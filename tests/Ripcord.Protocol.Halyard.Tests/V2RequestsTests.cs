using System.Text;
using Ripcord.Protocol.Halyard.Common.Control;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Session;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>The v2 /sess/init request builder (spec §4): the v1 header set plus RP-Pubkey + RP-Hmac.</summary>
public class V2RequestsTests
{
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
        Assert.Equal(SessProtocol.DefaultSupportCmd, headers[SessProtocol.HeaderSupportCmd]);
        Assert.Equal("1", headers[SessProtocol.HeaderFeature]);
        Assert.Equal("1.0", headers["Rp-Version"]);
    }
}
