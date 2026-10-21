using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Ripcord.Core.Net.Crypto;
using Ripcord.Protocol.Halyard.Session;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// Byte-exact check of the whole v2 control envelope against real console <c>/sess/ctrl</c> ciphertext (sessions
/// 003/004): the captured RP-Data/RP-Tag decrypt only if the GCM key (sk_9[0:16]), the IV
/// (HMAC(N16, sk_9[16:32]‖be64(0))), the AAD (empty), and — the point of this test — <b>N16 = the bundled
/// selectorOne context key</b> are all correct. This is what the self-consistent round-trip in
/// <see cref="V2ControlEnvelopeTests"/> cannot prove, and it is the regression guard for the control-IV fix (N16
/// is the fixed bundle constant, not the per-session RP-Nonce). Fixture
/// <c>v2_control_key_vectors.json</c> is gitignored; skips when absent.
/// </summary>
public class V2ControlEnvelopeKatTests
{
    [SkippableFact]
    public void ControlEnvelope_DecryptsCapturedCtrl_UnderBundledN16()
    {
        Skip.IfNot(HalyardInteropConstants.IsBundled, "Build omitted the interop constants bundle.");
        Fixture fx = LoadOrSkip();
        Skip.If(fx.CtrlEnvelopes is not { Count: > 0 }, "Fixture carries no ctrl envelopes (regenerate it).");

        // N16, the IV HMAC key, is the bundled selectorOne context key — not the per-session RP-Nonce.
        byte[] n16 = HalyardInteropConstants.Control()!.ContextKeys.SelectorOne.ToArray();

        foreach (CtrlEnvelope e in fx.CtrlEnvelopes!)
        {
            byte[] sk = Convert.FromHexString(e.Sk);
            byte[] iv = DeriveIv(n16, sk.AsSpan(16, 16), e.Counter);
            byte[]? plaintext = AesGcmCore.Decrypt(
                sk.AsSpan(0, 16), iv, Convert.FromHexString(e.Aad),
                Convert.FromHexString(e.Ct), Convert.FromHexString(e.Tag));

            // Non-null ⇔ the GCM tag verified ⇔ key + IV + N16 + AAD are all correct for the real console bytes.
            Assert.NotNull(plaintext);
        }
    }

    private static byte[] DeriveIv(byte[] n16, ReadOnlySpan<byte> baseIv, ulong counter)
    {
        byte[] data = new byte[16 + sizeof(ulong)];
        baseIv.CopyTo(data);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(16), counter);
        return HMACSHA256.HashData(n16, data)[..16];
    }

    private static Fixture LoadOrSkip()
    {
        string path = LocateFixture();
        Skip.IfNot(File.Exists(path),
            $"Captures-folder fixture not present ({path}). Generate it with v2-validation/extract_v2_key_vectors.py.");
        return JsonSerializer.Deserialize<Fixture>(File.ReadAllText(path),
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException("Fixture failed to deserialize.");
    }

    private static string LocateFixture()
    {
        string? env = Environment.GetEnvironmentVariable("RIPCORD_V2_KEY_FIXTURE");
        if (!string.IsNullOrEmpty(env) && File.Exists(env))
            return env;

        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            string candidate = Path.Combine(dir, "docs", "protocol", "captures", "v2_control_key_vectors.json");
            if (File.Exists(candidate))
                return candidate;
            dir = Directory.GetParent(dir)?.FullName;
        }

        return Path.Combine(AppContext.BaseDirectory, "v2_control_key_vectors.json"); // non-existent -> skip
    }

    private sealed class Fixture
    {
        public List<CtrlEnvelope>? CtrlEnvelopes { get; set; }
    }

    private sealed class CtrlEnvelope
    {
        public string Session { get; set; } = "";
        public string Sk { get; set; } = "";
        public ulong Counter { get; set; }
        public string Aad { get; set; } = "";
        public string Ct { get; set; } = "";
        public string Tag { get; set; } = "";
    }
}
