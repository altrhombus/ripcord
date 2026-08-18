using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Crypto.V1;
using Ripcord.Protocol.Halyard.Session;
using Xunit;
using Xunit.Abstractions;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The companion-keyed sweep for the no-PIN registration transport key.
///
/// <para>
/// The remaining hypothesis after table entries and cloud <c>data1/2</c> were both ruled out: on the account
/// route the passcode fold is replaced by the <b>companion</b> (<c>RP-Key</c>, the stable per-console secret),
/// so <c>K = f(companion, context, …)</c>. We hold the companion and the context, and — because the response
/// is AES-128-CFB and its plaintext (the registkey) is known — any candidate <c>K</c> is verified instantly and
/// with certainty by the IV-free block oracle. This is a function search, not a cryptanalytic attack.
/// </para>
///
/// <para>
/// Reports every candidate that recovers the captured registkey. If it finds one, the no-PIN key schedule is
/// solved; if it finds none, <c>K</c> is not a simple function of the companion + the material we hold, and the
/// next step is a fresh instrumented capture. Skips when the dirty-room fixture is absent.
/// </para>
/// </summary>
public class NoPinCompanionKeySweepTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [SkippableFact]
    public void SweepCompanionDerivations_AgainstTheCapturedResponse()
    {
        Fixture fx = LoadOrSkip();

        byte[] context = Convert.FromHexString(fx.Context!);
        byte[] response = Convert.FromHexString(fx.ResponseBody!);
        byte[] companion = Convert.FromHexString(fx.Companion!);
        byte[] companion2 = fx.Companion2 is null ? [] : Convert.FromHexString(fx.Companion2);
        byte[] registkey = Convert.FromHexString(fx.ExpectRegistKey!);
        byte[] accountId = Encoding.ASCII.GetBytes(fx.AccountId ?? string.Empty);
        string expected = Encoding.ASCII.GetString(registkey).TrimEnd('\0');

        // The bundled PS5 registration constants: the table (for the selected entry + fold hypotheses), the
        // wrap table (to recover the transmitted material), and the field context key.
        var bundled = HalyardInteropConstants.Registration(HalyardConsolePlatform.Ps5);
        Skip.If(bundled is null, "Build omitted the interop constants.");
        HalyardRegistrationSecrets secrets = bundled!.Value.Secrets;
        byte[] tableEntry = secrets.TableEntry(context[secrets.SelectorOffset] & 0x1f).ToArray();

        // The material the client transmitted inside the context — the field IV depends on it, and K might too.
        byte[] material = new HalyardRegistrationCipher(
            new HalyardRegistrationKdf(secrets, bundled.Value.VersionSelector), bundled.Value.ContextKey)
            .RecoverMaterial(context);

        var inputs = new Dictionary<string, byte[]>
        {
            ["context"] = context,
            ["contextHead16"] = context[..16],
            ["material"] = material,
            ["registkey"] = registkey,
            ["accountId"] = accountId,
            ["tableEntry"] = tableEntry,
            ["empty"] = [],
        };
        var secretsKeys = new Dictionary<string, byte[]>
        {
            ["companion"] = companion,
            ["companion2"] = companion2.Length == 16 ? companion2 : companion,
        };

        var hits = new List<string>();
        void Try(string label, byte[] key)
        {
            if (key.Length != 16)
            {
                return;
            }

            // Hit criterion is "the tail PARSES as a pairing record", not a string match — a wrong key yields
            // garbage that never parses, and this avoids a false-negative from registkey representation
            // (raw-hex vs ASCII). The recovered registkey is reported so a hit can be eyeballed against the
            // expected value.
            string? recovered = TryRecoverRegistkey(key, response);
            if (recovered is not null)
            {
                hits.Add($"{label}  (registkey={recovered}, expected={expected})");
            }
        }

        foreach ((string sn, byte[] sk) in secretsKeys)
        {
            // Direct and byte-level transforms of the companion.
            Try($"{sn}", sk);
            Try($"{sn}-reversed", (byte[])[.. ((byte[])sk.Clone()).Reverse()]);
            Try($"tableEntry^{sn}", Xor(tableEntry, sk));
            Try($"{sn}^tableEntry", Xor(sk, tableEntry));

            // Table entry with the companion folded into the last 4 bytes, mirroring the PIN passcode fold.
            Try($"tableEntry+fold4({sn})", FoldLast4(tableEntry, sk));

            foreach ((string mn, byte[] mv) in inputs)
            {
                Try($"hmac256({sn},{mn})", Hmac(sk, mv, HashAlgorithmName.SHA256)[..16]);
                Try($"hmac256({mn},{sn})", Hmac(mv, sk, HashAlgorithmName.SHA256)[..16]);
                Try($"hmac1({sn},{mn})", Hmac(sk, mv, HashAlgorithmName.SHA1)[..16]);
                Try($"hmac1({mn},{sn})", Hmac(mv, sk, HashAlgorithmName.SHA1)[..16]);
                Try($"sha256({sn}+{mn})", SHA256.HashData([.. sk, .. mv])[..16]);
                Try($"sha256({mn}+{sn})", SHA256.HashData([.. mv, .. sk])[..16]);
                Try($"aesEcb({sn},{mn}[0:16])", AesEcbBlock(sk, mv));
                Try($"cmac({sn},{mn})", AesCmac(sk, mv));
            }
        }

        foreach (string hit in hits)
        {
            _output.WriteLine($"HIT: {hit}");
        }

        // The established finding: K is NOT a single-stage function of the companion + the material we hold
        // (tried direct, reversed, table-XOR, table-fold, HMAC-SHA1/256, SHA-256, AES-ECB, and AES-CMAC over
        // context / material / registkey / accountId / tableEntry). Combined with the earlier table-entry and
        // data1/2 negatives, the no-PIN transport key derives from something not in the material we captured —
        // most likely a per-session secret PSN brokers to both sides (data1/2/3 already falsified) or an
        // on-device derivation. If this ever fails, a derivation DID work — that is the discovery; the winning
        // label is printed above.
        Assert.True(
            hits.Count == 0,
            "A companion-derived key DID recover the registkey: " + string.Join("; ", hits)
            + ". The no-PIN key schedule is solved — promote this and document it.");
    }

    // ---- oracle + primitives -------------------------------------------------------------------

    /// <summary>IV-free CFB tail decrypt with <paramref name="key"/>; the registkey if the tail parses.</summary>
    private static string? TryRecoverRegistkey(byte[] key, byte[] ciphertext)
    {
        int blocks = ciphertext.Length / 16;
        if (blocks < 2)
        {
            return null;
        }

        byte[] plain = (byte[])ciphertext.Clone();
        using Aes aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Key = key;
        for (int i = 1; i < blocks; i++)
        {
            byte[] ks = aes.EncryptEcb(ciphertext.AsSpan((i - 1) * 16, 16), PaddingMode.None);
            for (int j = 0; j < 16; j++)
            {
                plain[i * 16 + j] = (byte)(ciphertext[i * 16 + j] ^ ks[j]);
            }
        }

        return HalyardRegistrationMessage.TryParsePairingRecord(plain, out HalyardPairingRecord? record) && record is not null
            ? Encoding.ASCII.GetString(record.RegistrationKey).TrimEnd('\0')
            : null;
    }

    private static byte[] Hmac(byte[] key, byte[] message, HashAlgorithmName alg)
    {
        using var h = IncrementalHash.CreateHMAC(alg, key);
        h.AppendData(message);
        return h.GetHashAndReset();
    }

    private static byte[] AesEcbBlock(byte[] key, byte[] data)
    {
        if (key.Length != 16)
        {
            return [];
        }

        byte[] block = new byte[16];
        data.AsSpan(0, Math.Min(16, data.Length)).CopyTo(block);
        using Aes aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Key = key;
        return aes.EncryptEcb(block, PaddingMode.None);
    }

    /// <summary>AES-CMAC (RFC 4493) over <paramref name="message"/> — a 16-byte MAC candidate for K.</summary>
    private static byte[] AesCmac(byte[] key, byte[] message)
    {
        if (key.Length != 16)
        {
            return [];
        }

        using Aes aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Key = key;

        byte[] l = aes.EncryptEcb(new byte[16], PaddingMode.None);
        byte[] k1 = LeftShiftXorRb(l);
        byte[] k2 = LeftShiftXorRb(k1);

        int n = Math.Max(1, (message.Length + 15) / 16);
        bool complete = message.Length > 0 && message.Length % 16 == 0;

        byte[] last = new byte[16];
        int lastStart = (n - 1) * 16;
        if (complete)
        {
            message.AsSpan(lastStart, 16).CopyTo(last);
            last = Xor(last, k1);
        }
        else
        {
            int rem = message.Length - lastStart;
            message.AsSpan(lastStart, rem).CopyTo(last);
            last[rem] = 0x80;
            last = Xor(last, k2);
        }

        byte[] x = new byte[16];
        for (int i = 0; i < n - 1; i++)
        {
            x = aes.EncryptEcb(Xor(x, message.AsSpan(i * 16, 16).ToArray()), PaddingMode.None);
        }

        return aes.EncryptEcb(Xor(x, last), PaddingMode.None);
    }

    private static byte[] LeftShiftXorRb(byte[] input)
    {
        byte[] output = new byte[16];
        int carry = 0;
        for (int i = 15; i >= 0; i--)
        {
            int b = (input[i] << 1) | carry;
            output[i] = (byte)(b & 0xFF);
            carry = (b >> 8) & 1;
        }

        if ((input[0] & 0x80) != 0)
        {
            output[15] ^= 0x87; // Rb for the 128-bit block
        }

        return output;
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

    private static Fixture LoadOrSkip()
    {
        string path = Locate();
        Skip.IfNot(File.Exists(path), $"Correlation fixture not present ({path}).");
        Fixture? fx = JsonSerializer.Deserialize<Fixture>(
            File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Skip.If(fx?.Context is null || fx.ResponseBody is null || fx.Companion is null || fx.ExpectRegistKey is null,
            "Fixture is missing context/responseBody/companion/registkey.");
        return fx!;
    }

    private static string Locate()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            string candidate = Path.Combine(dir, "docs", "protocol", "captures", "nopin_correlation_ps5.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        return Path.Combine(AppContext.BaseDirectory, "nopin_correlation_ps5.json");
    }

    private sealed class Fixture
    {
        public string? Context { get; set; }

        public string? ResponseBody { get; set; }

        public string? Companion { get; set; }

        public string? Companion2 { get; set; }

        public string? ExpectRegistKey { get; set; }

        public string? AccountId { get; set; }
    }
}
