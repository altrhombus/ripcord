using System.Text.Json;
using System.Text.Json.Serialization;
using Ripcord.Protocol.Halyard.Common.Crypto.V1;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// Real captured packets whose keystream offset is an exact multiple of 45000: the only packets that tell
/// <c>(keystreamOffset - 1) / 45000</c> apart from the plain quotient. Each must verify under the key
/// <see cref="HalyardPacketCrypto"/> picks for it, and must not verify under the key of the window above.
///
/// <para>The plain quotient puts these packets one window too high: a client using it drops every received
/// packet that lands on a boundary and sends ones the console would refuse. It passes every other test,
/// because the two rules agree on every other position.</para>
///
/// <para>Fixture <c>docs/protocol/captures/gmac_boundary_vectors.json</c> is gitignored (live session keys):
/// v1 console-to-client from a 2026-10-03 session, v1 client-to-console from cap48, and both directions of
/// the v2 control plane from <c>rp_motion_cal2</c>. Skips when absent.</para>
/// </summary>
public class GmacWindowBoundaryVectorTests
{
    private static readonly string FixturePath = LocateFixture();

    [SkippableFact]
    public void BoundaryPackets_VerifyInTheWindowBelow_InBothPlanesAndDirections()
    {
        Skip.IfNot(File.Exists(FixturePath), $"Captures-folder fixture not present ({FixturePath}).");
        var fx = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(FixturePath), JsonOpts)!;
        Assert.NotEmpty(fx.Packets!);

        var covered = new HashSet<string>();
        foreach (var p in fx.Packets!)
        {
            Assert.Equal(0UL, p.KeystreamOffset % (ulong)HalyardPacketCrypto.RotationWindow);
            using var crypto = new HalyardPacketCrypto(Hex.Bytes(p.AesKey), Hex.Bytes(p.BaseIv));
            byte[] packet = Hex.Bytes(p.Packet);

            Assert.True(crypto.VerifyPacket(p.KeystreamOffset, packet, p.TagOffset),
                $"{p.Source} {p.Plane} {p.Direction} at keystreamOffset {p.KeystreamOffset}");

            // The plain quotient's window is one higher; its key is the one at keystreamOffset + 1.
            byte[] expected = crypto.ComputeTag(p.KeystreamOffset + 1, packet, p.TagOffset);
            Assert.False(expected.AsSpan().SequenceEqual(packet.AsSpan(p.TagOffset, HalyardPacketCrypto.TagLength)));

            covered.Add($"{p.Plane}/{p.Direction}");
        }

        Assert.Equal(["v1/c2s", "v1/s2c", "v2/c2s", "v2/s2c"], covered.Order());
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static string LocateFixture()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "docs", "protocol", "captures", "gmac_boundary_vectors.json");
            if (File.Exists(candidate))
                return candidate;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return Path.Combine(AppContext.BaseDirectory, "gmac_boundary_vectors.json"); // non-existent -> skip
    }

    private sealed class Fixture
    {
        public List<BoundaryPacket>? Packets { get; set; }
    }

    private sealed class BoundaryPacket
    {
        public string Source { get; set; } = "";
        public string Plane { get; set; } = "";
        public string Direction { get; set; } = "";
        public string AesKey { get; set; } = "";
        public string BaseIv { get; set; } = "";
        [JsonPropertyName("keyPos")] // the private fixture's key
        public ulong KeystreamOffset { get; set; }
        public int TagOffset { get; set; }
        public string Packet { get; set; } = "";
    }
}
