using System.Text;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Discovery;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// Pins the LAN wake against the byte shape captured in cap49 (2026-08-03). These are not self-consistency
/// checks: the expected payload shape is transcribed from a real vendor-client WAKEUP that woke a
/// real console with its internet blocked, so a change that drifts from the wire fails here.
/// </summary>
public class WakeClientTests
{
    // Synthetic key: wire value 3161326233633464 -> ASCII "1a2b3c4d" -> 0x1a2b3c4d -> 439041101.
    private static HalyardPairingRecord RecordWithSyntheticKey()
    {
        byte[] registKey = Encoding.ASCII.GetBytes("1a2b3c4d"); // the raw ASCII the record stores
        return new HalyardPairingRecord(registKey, new byte[16], KeyType: 1);
    }

    [Fact]
    public void WakeCredential_IsTheRegistKeyAsADecimalInteger()
    {
        Assert.Equal("439041101", RecordWithSyntheticKey().WakeCredential());
    }

    [Fact]
    public void WakeCredential_HandlesTheFullUnsignedRange()
    {
        // 0xFFFFFFFF would overflow a signed int; the credential must be unsigned. Regression guard for the
        // parse choice, since a signed parse passes every low-valued key and only breaks on high ones.
        var record = new HalyardPairingRecord(Encoding.ASCII.GetBytes("ffffffff"), new byte[16], KeyType: 1);
        Assert.Equal("4294967295", record.WakeCredential());
    }

    [Fact]
    public void BuildWakePayload_MatchesTheCapturedBytesExactly()
    {
        // Shape transcribed verbatim from cap49, including the LF (not CRLF) line endings; the key is synthetic.
        const string expected =
            "WAKEUP * HTTP/1.1\n" +
            "client-type:vr\n" +
            "auth-type:R\n" +
            "model:w\n" +
            "app-type:r\n" +
            "user-credential:439041101\n" +
            "device-discovery-protocol-version:00030010\n";

        byte[] actual = HalyardWakeClient.BuildWakePayload("439041101");
        Assert.Equal(expected, Encoding.ASCII.GetString(actual));
    }

    [Fact]
    public void BuildWakePayload_UsesBareLineFeeds()
    {
        // The vendor's WAKEUP uses \n while its SRCH uses \r\n; guard against a well-meaning "consistency" fix.
        byte[] payload = HalyardWakeClient.BuildWakePayload("439041101");
        Assert.DoesNotContain((byte)'\r', payload);
        Assert.Contains((byte)'\n', payload);
    }
}
