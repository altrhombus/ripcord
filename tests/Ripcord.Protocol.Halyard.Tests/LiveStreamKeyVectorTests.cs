using Ripcord.Protocol.Halyard.Common.Crypto.V1;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// Validates the stream key derivation against a real console's dumped key material (the P-521 ECDH shared
/// X, the handshakeKey, and the per-direction send/recv Crypt objects). These live in the gitignored dirty
/// room, so the test loads them if present and <see cref="Skip.IfNot"/> otherwise - CI stays green, no
/// secrets committed. When present it proves <see cref="HalyardStreamKeySchedule.DeriveDirection"/> is [V]
/// against hardware (the full stream key agreement, end to end).
///
/// Dirty-room inputs (any subfolder of docs/protocol/captures): <c>secret66.bin</c> (66-byte ECDH X),
/// <c>hkstruct.bin</c> (16-byte handshakeKey), <c>sendcrypt.bin</c>/<c>recvcrypt.bin</c> (0x60 Crypt objects,
/// AES key @0x30 / base IV @0x40).
/// </summary>
public class LiveStreamKeyVectorTests
{
    [SkippableFact]
    public void DeriveDirection_ReproducesDumpedConsoleKeys()
    {
        string? dir = LocateDumpDir();
        Skip.If(dir is null, "Stream-key dump material not present in the gitignored dirty room.");

        byte[] secret = File.ReadAllBytes(Path.Combine(dir!, "secret66.bin"));
        byte[] hkey = File.ReadAllBytes(Path.Combine(dir!, "hkstruct.bin"));
        byte[] send = File.ReadAllBytes(Path.Combine(dir!, "sendcrypt.bin"));
        byte[] recv = File.ReadAllBytes(Path.Combine(dir!, "recvcrypt.bin"));

        var c2s = HalyardStreamKeySchedule.DeriveDirection(secret, hkey, HalyardStreamKeySchedule.DirectionClientToServer);
        var s2c = HalyardStreamKeySchedule.DeriveDirection(secret, hkey, HalyardStreamKeySchedule.DirectionServerToClient);

        Assert.Equal(Hex.String(send.AsSpan(0x30, 16)), Hex.String(c2s.AesKey));
        Assert.Equal(Hex.String(send.AsSpan(0x40, 16)), Hex.String(c2s.BaseIv));
        Assert.Equal(Hex.String(recv.AsSpan(0x30, 16)), Hex.String(s2c.AesKey));
        Assert.Equal(Hex.String(recv.AsSpan(0x40, 16)), Hex.String(s2c.BaseIv));
    }

    private static string? LocateDumpDir()
    {
        // Explicit override first: RIPCORD_KEYDUMP_DIR may point either straight at the directory holding the
        // dumps or at their parent (captures/ holds one subfolder per capture session). It is validated by
        // CONTENT, not just existence: an env var aimed one level off used to return a directory with no
        // material in it, so the test hard-failed with FileNotFoundException instead of skipping.
        string? env = Environment.GetEnvironmentVariable("RIPCORD_KEYDUMP_DIR");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env))
        {
            return HasDumpMaterial(env) ? env : FindInSubdirectories(env);
        }

        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            string captures = Path.Combine(dir, "docs", "protocol", "captures");
            if (Directory.Exists(captures))
            {
                return FindInSubdirectories(captures);
            }
            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
    }

    private static string? FindInSubdirectories(string root)
    {
        foreach (var sub in Directory.EnumerateDirectories(root))
        {
            if (HasDumpMaterial(sub))
            {
                return sub;
            }
        }
        return null;
    }

    /// <summary>All four dumps present — the only definition of "this directory is usable" worth having.</summary>
    private static bool HasDumpMaterial(string dir) =>
        File.Exists(Path.Combine(dir, "secret66.bin")) &&
        File.Exists(Path.Combine(dir, "hkstruct.bin")) &&
        File.Exists(Path.Combine(dir, "sendcrypt.bin")) &&
        File.Exists(Path.Combine(dir, "recvcrypt.bin"));
}
