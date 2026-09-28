using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Session;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// No control key, no session. /sess/init's RP-Nonce and the pairing's companion are the control key's
/// inputs; without either, the session used to carry on to an unauthenticated /sess/ctrl and would then have
/// sent the launchSpec, handshakeKey and all, in the clear. It now stops with a reason, as the C core and the
/// Rust engine do (engine comparisons, 2026-09-26).
/// </summary>
public class ControlKeyMaterialTests
{
    private static readonly byte[] Companion = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
    private static readonly string Nonce16 = Convert.ToBase64String(Enumerable.Range(0x40, 16).Select(i => (byte)i).ToArray());

    [Theory]
    [InlineData(HalyardConsolePlatform.Ps5, 1)]
    [InlineData(HalyardConsolePlatform.Ps4, 0)]
    public void AValidNonceAndCompanion_GiveKeyMaterial(HalyardConsolePlatform platform, int versionSelector)
    {
        HalyardControlKeyMaterial? material =
            HalyardStreamingSession.ControlKeyMaterial(Nonce16, Companion, platform, out string? problem);

        Assert.NotNull(material);
        Assert.Null(problem);
        Assert.Equal(Convert.FromBase64String(Nonce16), material.Value.Nonce.ToArray());
        Assert.Equal(versionSelector, material.Value.VersionSelector);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("AAECAwQFBgc=")]   // 8 bytes, not 16
    public void AMissingOrMalformedNonce_StopsTheSession(string? nonce)
    {
        Assert.Null(HalyardStreamingSession.ControlKeyMaterial(nonce, Companion, HalyardConsolePlatform.Ps5, out string? problem));
        Assert.Contains("RP-Nonce", problem);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public void AMissingCompanion_StopsTheSession(int length)
    {
        byte[]? companion = length == 0 ? null : new byte[length];
        Assert.Null(HalyardStreamingSession.ControlKeyMaterial(Nonce16, companion, HalyardConsolePlatform.Ps5, out string? problem));
        Assert.Contains("companion", problem);
    }
}
