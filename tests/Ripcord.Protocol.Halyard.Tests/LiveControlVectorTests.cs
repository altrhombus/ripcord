using System.Text.Json;
using Ripcord.Protocol.Halyard.Common.Crypto.V1;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// Validates the control-plane crypto against real captured ground truth (the KDF lookup tables extracted
/// from our own client binary, the context keys, and live-captured input→output triples). All of this is
/// extracted/interop material that stays in the gitignored dirty room, so these tests load a local fixture
/// and <see cref="Assert.SkipUnless"/> when it is absent — keeping CI green and committing no secrets.
///
/// <para>
/// Fixture: <c>docs/protocol/captures/control_crypto_vectors.json</c> (gitignored). Generated once from the
/// dirty-room reference material; schema in <see cref="Fixture"/>. When present, this upgrades the
/// control-plane crypto from "[V] in the Python reference" to "[V] in our C# implementation".
/// </para>
/// </summary>
public class LiveControlVectorTests
{
    private static readonly string FixturePath = LocateFixture();

    [SkippableFact]
    public void Kdf_ReproducesCapturedOutputs()
    {
        var fx = LoadOrSkip();
        var secrets = fx.BuildSecrets();
        var kdf = new HalyardControlKdf(secrets);

        Assert.NotEmpty(fx.KdfVectors!);
        foreach (var v in fx.KdfVectors!)
        {
            var (key, material) = kdf.Derive(Hex.Bytes(v.Nonce), Hex.Bytes(v.Companion));
            // The AES key is the companion-derived value; the IV material is the nonce-derived
            // material value. (v.Out1/v.Out2 are the raw derivation outputs in material/key order.)
            Assert.Equal(v.Key, Hex.String(key));
            Assert.Equal(v.Material, Hex.String(material));
        }
    }

    /// <summary>
    /// The end-to-end guard: from a real full session's nonce + companion, derive the control key and
    /// decrypt every captured <c>/sess/ctrl</c> field to its exact plaintext. This is the vector that
    /// exercises <see cref="HalyardControlKdf.Derive"/>'s key/material role assignment against real wire —
    /// the primitive-level <see cref="FieldCipher_ReproducesCapturedCiphertext"/> feeds key/material in
    /// directly and so cannot catch a swap in the derivation.
    /// </summary>
    [SkippableFact]
    public void ControlHandshake_DecryptsCapturedCtrlFields_EndToEnd()
    {
        var fx = LoadOrSkip();
        Skip.If(fx.CtrlHandshake is null, "Fixture has no ctrlHandshake section (regenerate to run).");
        var h = fx.CtrlHandshake!;

        var kdf = new HalyardControlKdf(fx.BuildSecrets());
        var (key, material) = kdf.Derive(Hex.Bytes(h.Nonce), Hex.Bytes(h.Companion));
        var crypto = new HalyardControlFieldCrypto(key, material, Hex.Bytes(h.ContextKey));

        Assert.NotEmpty(h.Fields!);
        foreach (var f in h.Fields!)
        {
            byte[] plain = crypto.DecryptFieldBase64(f.Counter, f.WireBase64);
            Assert.Equal(f.Plaintext, Hex.String(plain));
        }
    }

    [SkippableFact]
    public void FieldIv_ReproducesCapturedTriples()
    {
        var fx = LoadOrSkip();
        Assert.NotEmpty(fx.FieldIvVectors!);
        foreach (var v in fx.FieldIvVectors!)
        {
            var iv = HalyardFieldIv.Derive(Hex.Bytes(v.ContextKey), Hex.Bytes(v.Material), v.Counter);
            Assert.Equal(v.Iv, Hex.String(iv));
        }
    }

    [SkippableFact]
    public void FieldCipher_ReproducesCapturedCiphertext()
    {
        var fx = LoadOrSkip();
        Assert.NotEmpty(fx.FieldVectors!);
        foreach (var v in fx.FieldVectors!)
        {
            var crypto = new HalyardControlFieldCrypto(Hex.Bytes(v.Key), Hex.Bytes(v.Material), Hex.Bytes(v.ContextKey));
            var ct = crypto.EncryptField(v.Counter, Hex.Bytes(v.Plaintext));
            Assert.Equal(v.Ciphertext, Hex.String(ct));
            Assert.Equal(v.Plaintext, Hex.String(crypto.DecryptField(v.Counter, ct)));
        }
    }

    private static Fixture LoadOrSkip()
    {
        Skip.IfNot(File.Exists(FixturePath),
            $"Dirty-room fixture not present ({FixturePath}). Generate it from captures/ to run live validation.");
        var json = File.ReadAllText(FixturePath);
        return JsonSerializer.Deserialize<Fixture>(json, JsonOpts)
               ?? throw new InvalidOperationException("Fixture failed to deserialize.");
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static string LocateFixture()
    {
        // An explicit override first — the same RIPCORD_CONTROL_FIXTURE the production loader honours. Without
        // it these tests silently skip whenever the build output lands outside the repo tree, which reads as
        // "no live validation exists" rather than "it could not find the file".
        string? env = Environment.GetEnvironmentVariable("RIPCORD_CONTROL_FIXTURE");
        if (!string.IsNullOrEmpty(env) && File.Exists(env))
            return env;

        // Otherwise walk up from the test binary to the repo root, then into docs/protocol/captures.
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "docs", "protocol", "captures", "control_crypto_vectors.json");
            if (File.Exists(candidate))
                return candidate;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return Path.Combine(AppContext.BaseDirectory, "control_crypto_vectors.json"); // non-existent -> skip
    }

    // ---- fixture schema ----
    private sealed class Fixture
    {
        public string? KdfTable1 { get; set; }
        public string? KdfTable2 { get; set; }
        public ContextKeys? ContextKeys { get; set; }
        public List<KdfVector>? KdfVectors { get; set; }
        public List<FieldIvVector>? FieldIvVectors { get; set; }
        public List<FieldVector>? FieldVectors { get; set; }
        public CtrlHandshakeVector? CtrlHandshake { get; set; }

        public HalyardControlSecrets BuildSecrets()
        {
            var ck = ContextKeys ?? new ContextKeys();
            return new HalyardControlSecrets(
                Hex.Bytes(KdfTable1!),
                Hex.Bytes(KdfTable2!),
                new HalyardFieldContextKeys(
                    Hex.Bytes(ck.CodecInHigh ?? Zeros),
                    Hex.Bytes(ck.SelectorOne ?? Zeros),
                    Hex.Bytes(ck.SelectorZero ?? Zeros),
                    Hex.Bytes(ck.FallbackZero ?? Zeros)));
        }

        private const string Zeros = "00000000000000000000000000000000";
    }

    private sealed class ContextKeys
    {
        public string? CodecInHigh { get; set; }
        public string? SelectorOne { get; set; }
        public string? SelectorZero { get; set; }
        public string? FallbackZero { get; set; }
    }

    private sealed class KdfVector
    {
        public string Nonce { get; set; } = "";
        public string Companion { get; set; } = "";
        public string Out1 { get; set; } = "";      // raw derivation output 1 = the IV material
        public string Out2 { get; set; } = "";      // raw derivation output 2 = the AES key
        public string Key { get; set; } = "";        // = Out2: the AES key role
        public string Material { get; set; } = "";   // = Out1: the IV-material role
    }

    private sealed class CtrlHandshakeVector
    {
        public string Nonce { get; set; } = "";
        public string Companion { get; set; } = "";
        public string ContextKey { get; set; } = "";
        public List<CtrlField>? Fields { get; set; }
    }

    private sealed class CtrlField
    {
        public string Name { get; set; } = "";
        public ulong Counter { get; set; }
        public string WireBase64 { get; set; } = "";
        public string Plaintext { get; set; } = "";
    }

    private sealed class FieldIvVector
    {
        public string ContextKey { get; set; } = "";
        public string Material { get; set; } = "";
        public ulong Counter { get; set; }
        public string Iv { get; set; } = "";
    }

    private sealed class FieldVector
    {
        public string Key { get; set; } = "";
        public string Material { get; set; } = "";
        public string ContextKey { get; set; } = "";
        public ulong Counter { get; set; }
        public string Plaintext { get; set; } = "";
        public string Ciphertext { get; set; } = "";
    }
}
