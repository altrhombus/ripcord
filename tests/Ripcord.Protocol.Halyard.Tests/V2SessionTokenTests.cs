using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Crypto.V2;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The v2 session-token roll: minting RP-VerifyKey, choosing this session's K16 (bootstrap K16_reg vs the stored
/// rolled token), and persisting the rolled token on the pairing record. The live commit-on-acceptance happens
/// in the session wiring; this pins the rules and the persistence.
/// </summary>
public class V2SessionTokenTests
{
    private static readonly byte[] K16Reg = Convert.FromHexString("2736d42c9327ccbb1322e21133dab093");

    [Fact]
    public void Mint_IsSixteenRandomBytes()
    {
        byte[] a = HalyardSessionToken.Mint();
        byte[] b = HalyardSessionToken.Mint();
        Assert.Equal(HalyardSessionToken.Length, a.Length);
        Assert.Equal(16, a.Length);
        Assert.NotEqual(Convert.ToHexString(a), Convert.ToHexString(b)); // vanishingly unlikely to collide
    }

    [Fact]
    public void ResolveK16_BootstrapsWhenNoTokenThenUsesTheRolledToken()
    {
        // First session after pairing: no token -> the bundled K16_reg.
        Assert.Equal(Convert.ToHexString(K16Reg),
            Convert.ToHexString(HalyardSessionToken.ResolveK16(ReadOnlySpan<byte>.Empty, K16Reg)));

        // Once a token is held, it is this session's K16, not the bootstrap.
        byte[] token = HalyardSessionToken.Mint();
        Assert.Equal(Convert.ToHexString(token),
            Convert.ToHexString(HalyardSessionToken.ResolveK16(token, K16Reg)));
    }

    [Fact]
    public void HasRolledToken_OnlyForAFull16Bytes()
    {
        Assert.False(HalyardSessionToken.HasRolledToken(ReadOnlySpan<byte>.Empty));
        Assert.False(HalyardSessionToken.HasRolledToken(new byte[15]));
        Assert.True(HalyardSessionToken.HasRolledToken(new byte[16]));
    }

    [Fact]
    public void ResolveK16_ThrowsWhenNeitherTokenNorBootstrapIsUsable()
        => Assert.Throws<InvalidOperationException>(
            () => HalyardSessionToken.ResolveK16(ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty));

    [Fact]
    public void PairingRecord_RoundTripsTheRolledToken()
    {
        byte[] token = HalyardSessionToken.Mint();
        var record = new HalyardPairingRecord(
            RegistrationKey: "abcd1234"u8.ToArray(),
            Companion: new byte[16],
            KeyType: 2) { RolledToken = token };

        Assert.True(HalyardPairingRecord.TryDeserialize(record.Serialize(), out var restored));
        Assert.Equal(Convert.ToHexString(token), Convert.ToHexString(restored!.RolledToken));
    }

    [Fact]
    public void PairingRecord_WithoutARolledToken_RoundTripsEmpty()
    {
        var record = new HalyardPairingRecord("abcd1234"u8.ToArray(), new byte[16], 2);
        Assert.Empty(record.RolledToken);

        Assert.True(HalyardPairingRecord.TryDeserialize(record.Serialize(), out var restored));
        Assert.Empty(restored!.RolledToken);
    }

    [Fact]
    public void RollForward_BootstrapThenCommitsTheMintedToken()
    {
        // Session 1: no token -> bootstrap K16_reg; mint a verify key; commit it after acceptance.
        var record = new HalyardPairingRecord("abcd1234"u8.ToArray(), new byte[16], 2);
        Assert.Equal(Convert.ToHexString(K16Reg),
            Convert.ToHexString(HalyardSessionToken.ResolveK16(record.RolledToken, K16Reg)));

        byte[] minted = HalyardSessionToken.Mint();
        HalyardPairingRecord rolled = record with { RolledToken = minted };

        // Session 2 (after persisting the rolled record): its K16 is the token minted in session 1.
        Assert.Equal(Convert.ToHexString(minted),
            Convert.ToHexString(HalyardSessionToken.ResolveK16(rolled.RolledToken, K16Reg)));
    }

    [Fact]
    public void PairingRecord_RoundTripsSupportsGcm()
    {
        var withGcm = new HalyardPairingRecord("abcd1234"u8.ToArray(), new byte[16], 2) { SupportsGcm = true };
        Assert.True(HalyardPairingRecord.TryDeserialize(withGcm.Serialize(), out var restored));
        Assert.True(restored!.SupportsGcm);

        var withoutGcm = new HalyardPairingRecord("abcd1234"u8.ToArray(), new byte[16], 2);
        Assert.False(withoutGcm.SupportsGcm);
        Assert.True(HalyardPairingRecord.TryDeserialize(withoutGcm.Serialize(), out var restored2));
        Assert.False(restored2!.SupportsGcm);
    }
}
