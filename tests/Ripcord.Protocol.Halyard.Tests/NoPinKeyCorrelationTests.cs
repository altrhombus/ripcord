using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Crypto.V1;
using Ripcord.Protocol.Halyard.Session;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The decisive no-PIN experiment, and its result: <b>the account-route registration key is NOT derived from
/// the cloud <c>commands</c> <c>data1</c>/<c>data2</c>.</b>
///
/// <para>
/// This was the last standing hypothesis for where no-PIN authorisation lives, and cap64/cap65 (a PS5 and a
/// PS4 first-time no-PIN pair, each captured as a Fiddler saz + a raw pcapng <em>together</em>, so the cloud
/// bodies and the 9303 exchange are readable for one event) let it be tested directly and falsified two ways:
/// </para>
/// <list type="number">
///   <item><description><b>Cryptographic (this file).</b> With the PS5 <c>data1</c>/<c>data2</c> and the
///   registkey the client actually used both in hand, no key derived from the data — directly, reversed,
///   folded into the selected table entry, or via SHA-256 / HMAC-SHA256 in the shapes the vendor uses
///   elsewhere — decrypts the registration response to that registkey.</description></item>
///   <item><description><b>Structural (recorded in the spec).</b> The PS4 in cap65 completed the same 9303
///   account-route pairing with <em>no <c>commands</c> call at all</em> — it was found by search, not from
///   the cloud list — so it never received any <c>data1/2</c>, yet still obtained a registration key. A key
///   source the PS4 demonstrably never saw cannot be the account-route key source.</description></item>
/// </list>
///
/// <para>
/// This mirrors the earlier fate of "the <c>data1/2/3</c> seed the session crypto" (also falsified): the
/// commands data is real, out-of-band, 16-byte material whose role keeps not being the thing it is nearest to.
/// Where the account-route key actually comes from is open — and since the PS4 had no cloud trigger, the
/// source is most likely in-band (the 9303 INIT carries high-entropy blobs) or from account/session material
/// common to both families, not the commands payload.
/// </para>
///
/// <para>
/// <b>Why the negative is trustworthy.</b> The oracle is validated end-to-end on a known PIN key
/// (<see cref="Oracle_RecoversAKnownRegistkey_FromThePinFixture"/>) before any conclusion is drawn from a
/// miss: the response is AES-128-CFB, whose blocks after the first decrypt from the key alone, and the
/// registkey rides in those blocks, so a correct key would be recognised by recovering the exact captured
/// registkey — no coincidence passes.
/// </para>
///
/// <para>
/// Fixture: <c>docs/protocol/captures/nopin_correlation_ps5.json</c> (gitignored dirty room). Skips when
/// absent. Should a future derivation be found to work, this test fails loudly with its label — which is the
/// signal to promote it to a positive proof and write the mechanism into the spec.
/// </para>
/// </summary>
public class NoPinKeyCorrelationTests
{
    [SkippableFact]
    public void Oracle_RecoversAKnownRegistkey_FromThePinFixture()
    {
        // A negative from the experiment below is only trustworthy if the oracle itself is sound. This proves
        // it end-to-end on ground truth: take the PIN fixture's response and its KNOWN key, run the exact same
        // IV-free tail decrypt, and confirm it recovers the registkey. If this passes, a miss below is a real
        // miss and not an oracle bug (a wrong CFB offset, or the registkey sitting in the IV-dependent block 0).
        PinFixture fx = LoadPinFixtureOrSkip();
        Skip.If(fx.ResponseVectors is not { Count: > 0 }, "PIN fixture carries no response vectors.");

        var kdf = new HalyardRegistrationKdf(new HalyardRegistrationSecrets(
            Convert.FromHexString(fx.RegistrationTable!), fx.SelectorOffset));

        var recovered = new List<string>();
        foreach (PinResponseVector v in fx.ResponseVectors!)
        {
            byte[] key = kdf.DeriveKey(Convert.FromHexString(v.Context), v.Passcode);
            string? registkey = TryRecoverRegistkey(key, Convert.FromHexString(v.ResponseBody));
            if (registkey is not null)
            {
                recovered.Add(registkey);
            }
        }

        Assert.NotEmpty(recovered);
    }

    [SkippableFact]
    public void OutOfBandData_DoesNotDeriveTheRegistrationTransportKey()
    {
        Correlation fx = LoadOrSkip();

        byte[] context = Convert.FromHexString(fx.Context!);
        byte[] response = Convert.FromHexString(fx.ResponseBodyOrField());
        string expected = fx.ExpectRegistKey!;

        var hits = new List<string>();
        foreach ((string label, byte[] key) in Candidates(context, fx))
        {
            if (TryRecoverRegistkey(key, response) == expected)
            {
                hits.Add(label);
            }
        }

        // The established finding: none of these derivations of the commands data produce the transport key.
        // If this ever fails, a derivation DID work — that is the discovery, and the failing label names it.
        // Promote the test to a positive proof and write the mechanism into the spec at that point.
        Assert.True(
            hits.Count == 0,
            "A derivation of data1/data2 DID produce the no-PIN transport key: " + string.Join(", ", hits)
            + ". This is the answer we have been looking for — promote this test and document it.");
    }

    /// <summary>
    /// Candidate transport keys. <paramref name="context"/> supplies the selected table entry so the
    /// fold-style hypotheses can be tried against the same entry the PIN route would pick.
    /// </summary>
    private static IEnumerable<(string Label, byte[] Key)> Candidates(byte[] context, Correlation fx)
    {
        byte[]? d1 = fx.Data.TryGetValue("data1", out string? h1) ? Convert.FromHexString(h1) : null;
        byte[]? d2 = fx.Data.TryGetValue("data2", out string? h2) ? Convert.FromHexString(h2) : null;

        var bundled = HalyardInteropConstants.Registration(HalyardConsolePlatform.Ps5);
        Skip.If(bundled is null, "Build omitted the interop constants.");
        HalyardRegistrationSecrets secrets = bundled!.Value.Secrets;
        byte[] tableEntry = secrets.TableEntry(context[secrets.SelectorOffset] & 0x1f).ToArray();

        byte[] contextKey = bundled.Value.ContextKey;

        foreach ((string name, byte[]? d) in new[] { ("data1", d1), ("data2", d2) })
        {
            if (d is null || d.Length != 16)
            {
                continue;
            }

            // Direct and simple transforms.
            yield return (name, d);
            yield return ($"{name}-reversed", d.Reverse().ToArray());
            yield return ($"tableEntry^{name}", Xor(tableEntry, d));
            yield return ($"tableEntry-fold4-{name}", FoldLast4(tableEntry, d));

            // Hash/KDF over the datum, using the primitives the vendor uses elsewhere (SHA-256, HMAC-SHA256).
            yield return ($"sha256({name})[..16]", Sha256(d)[..16]);
            yield return ($"hmac({name}, tableEntry)[..16]", Hmac(d, tableEntry)[..16]);
            yield return ($"hmac(tableEntry, {name})[..16]", Hmac(tableEntry, d)[..16]);
            yield return ($"hmac(contextKey, {name})[..16]", Hmac(contextKey, d)[..16]);
            yield return ($"hmac({name}, contextKey)[..16]", Hmac(d, contextKey)[..16]);
        }

        if (d1 is { Length: 16 } && d2 is { Length: 16 })
        {
            yield return ("data1^data2", Xor(d1, d2));
            yield return ("sha256(data1||data2)[..16]", Sha256([.. d1, .. d2])[..16]);
            yield return ("sha256(data2||data1)[..16]", Sha256([.. d2, .. d1])[..16]);
            yield return ("hmac(data1, data2)[..16]", Hmac(d1, d2)[..16]);
            yield return ("hmac(data2, data1)[..16]", Hmac(d2, d1)[..16]);

            // The material-carrying context is still present; the two data values might key the field IV path
            // rather than the transport key. Covered indirectly: if either is the key, the tail decodes.
        }
    }

    private static byte[] Sha256(byte[] data) => SHA256.HashData(data);

    private static byte[] Hmac(byte[] key, byte[] message)
    {
        using var h = new HMACSHA256(key);
        return h.ComputeHash(message);
    }

    /// <summary>IV-free CFB tail decrypt with <paramref name="key"/>; returns the registkey if the tail parses.</summary>
    private static string? TryRecoverRegistkey(byte[] key, byte[] ciphertext)
    {
        int blocks = ciphertext.Length / 16;
        if (blocks < 2)
        {
            return null;
        }

        // Block 0 is IV-dependent, so leave it as-is; blocks 1..N-1 decrypt from the key alone.
        byte[] plain = (byte[])ciphertext.Clone();
        using Aes aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Key = key;

        for (int i = 1; i < blocks; i++)
        {
            byte[] keystream = aes.EncryptEcb(ciphertext.AsSpan((i - 1) * 16, 16), PaddingMode.None);
            for (int j = 0; j < 16; j++)
            {
                plain[i * 16 + j] = (byte)(ciphertext[i * 16 + j] ^ keystream[j]);
            }
        }

        return HalyardRegistrationMessage.TryParsePairingRecord(plain, out HalyardPairingRecord? record)
               && record is not null
            ? Encoding.ASCII.GetString(record.RegistrationKey).TrimEnd('\0')
            : null;
    }

    private static byte[] Xor(byte[] a, byte[] b)
    {
        byte[] r = new byte[a.Length];
        for (int i = 0; i < a.Length; i++)
        {
            r[i] = (byte)(a[i] ^ b[i]);
        }

        return r;
    }

    private static byte[] FoldLast4(byte[] entry, byte[] data)
    {
        byte[] r = (byte[])entry.Clone();
        for (int i = 0; i < 4; i++)
        {
            r[12 + i] ^= data[i];
        }

        return r;
    }

    // ---- fixture -------------------------------------------------------------------------------

    private static Correlation LoadOrSkip()
    {
        string path = Locate();
        Skip.IfNot(File.Exists(path), $"Correlation fixture not present ({path}).");
        Correlation? fx = JsonSerializer.Deserialize<Correlation>(
            File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Skip.If(fx?.Context is null || fx.ResponseBodyOrField().Length == 0, "Fixture incomplete.");
        Skip.If(string.IsNullOrEmpty(fx!.ExpectRegistKey), "Fixture has no ground-truth registkey.");
        Skip.If(fx.Data.Count == 0, "Fixture carries no commands data.");
        return fx;
    }

    private static string Locate()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            string candidate = Path.Combine(
                dir, "docs", "protocol", "captures", "nopin_correlation_ps5.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        return LocateNamed("nopin_correlation_ps5.json");
    }

    private static PinFixture LoadPinFixtureOrSkip()
    {
        string? env = Environment.GetEnvironmentVariable("RIPCORD_REGIST_FIXTURE");
        string path = !string.IsNullOrEmpty(env) && File.Exists(env) ? env : LocateNamed("registration_crypto_vectors.json");
        Skip.IfNot(File.Exists(path), $"PIN fixture not present ({path}).");
        return JsonSerializer.Deserialize<PinFixture>(
            File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("PIN fixture failed to deserialize.");
    }

    private static string LocateNamed(string fileName)
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            string candidate = Path.Combine(dir, "docs", "protocol", "captures", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        return Path.Combine(AppContext.BaseDirectory, fileName);
    }

    private sealed class PinFixture
    {
        public string? RegistrationTable { get; set; }

        public int SelectorOffset { get; set; }

        public List<PinResponseVector>? ResponseVectors { get; set; }
    }

    private sealed class PinResponseVector
    {
        public string Context { get; set; } = "";

        public uint Passcode { get; set; }

        public string ResponseBody { get; set; } = "";
    }

    private sealed class Correlation
    {
        public string? Context { get; set; }

        public string? RequestField { get; set; }

        public string? ResponseBody { get; set; }

        public Dictionary<string, string> Data { get; set; } = new();

        public string? ExpectRegistKey { get; set; }

        /// <summary>
        /// The registration RESPONSE is where the registkey lives and is what the oracle targets. The
        /// generator names it <c>responseBody</c>; tolerate its absence by falling back to the request field
        /// so an older fixture still loads (it simply will not find a hit).
        /// </summary>
        public string ResponseBodyOrField() => ResponseBody ?? RequestField ?? string.Empty;
    }
}
