using System.Text;
using System.Text.Json;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Crypto.V1;
using Ripcord.Protocol.Halyard.Session;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The account-based (no-PIN) registration route, validated against cap63 — an off-network capture of the
/// older vendor client pairing with a PS5 and a PS4 without ever showing a passcode.
///
/// <para>
/// <b>What these establish, and what they DON'T.</b> Two things are now settled from cap63, and one large
/// thing is settled as a negative:
/// </para>
/// <list type="bullet">
///   <item><description><b>The transport is the account (UDP 9303) route, and the request is unchanged.</b>
///   No TCP 9295 anywhere in the capture; the request body is the same 480-byte context + 107-byte field our
///   PIN-route builder already produces. So the send-side codec is reusable as-is.</description></item>
///   <item><description><b>The older client sends no <c>RP-Hmac</c>.</b> cap4 (a newer client) did, which had
///   made <c>RP-Hmac</c> the leading candidate for where no-PIN authorisation lives. For this protocol
///   version that candidate is simply absent — the request headers are exactly the PIN route's set.</description></item>
///   <item><description><b>[X] The body key is NOT any pinless entry from the bundled table.</b> This is the
///   negative, and the sweep below proves it rather than assuming it. So "the account route is the PIN route
///   with passcode 0" is FALSE, and where the response key comes from is genuinely open.</description></item>
/// </list>
///
/// <para>
/// <b>Why the negative is conclusive and not just a failed guess.</b> The response is AES-128-CFB, whose
/// blocks after the first decrypt from the key alone (the property <c>LiveRegistrationVectorTests</c> relies
/// on to recover credentials through a wrong IV). The registkey lives in those blocks. So a correct key would
/// be recognisable without knowing the IV — and the fixture records the <c>RP-Registkey</c> the client used
/// on the following <c>/sess/init</c> as an independent check on any hit. Sweeping all 32 table entries and
/// getting no parseable pairing record rules out the entire pinless-table-key family in one shot, the same
/// block-oracle method the RE log used to falsify the equivalent v2 hypothesis.
/// </para>
///
/// <para>
/// Fixture: <c>docs/protocol/captures/nopin_registration_vectors.json</c> (gitignored dirty room, generated
/// from cap63). Skips when absent, so CI and a clean checkout stay green.
/// </para>
/// </summary>
public class NoPinRegistrationVectorTests
{
    [SkippableTheory]
    [InlineData("ps5")]
    [InlineData("ps4")]
    public void NoPinResponse_IsNotDecryptableByAnyPinlessKeyFromTheBundledTable(string platform)
    {
        // The result is NEGATIVE, and the test is written to prove the negative rather than to hope for a
        // positive — which is why it sweeps rather than trying one key.
        //
        // The response is AES-128-CFB, so its first plaintext block is IV/material-dependent but every block
        // AFTER it decrypts with the KEY ALONE: P_i = C_i XOR AES-ECB(K, C_{i-1}). The pairing record's
        // RP-Registkey/RP-Key lines live in those later blocks (this is exactly why
        // LiveRegistrationVectorTests recovers them even with wrong material). So if any pinless key derived
        // from the 32-entry table decrypted this response, the tail would parse and carry the registkey the
        // client went on to use. We try all 32 entries with no passcode folded in. None do.
        //
        // This is the same block-oracle method the RE log used to falsify the v2 "reconnect -> passcode 0"
        // hypothesis, applied here to the older client's account route.
        Vector vector = LoadOrSkip(platform);
        HalyardRegistrationCipher cipher = CipherFor(platform);

        byte[] context = Convert.FromHexString(vector.Context);
        byte[] response = Convert.FromHexString(vector.ResponseBody);
        byte[] material = cipher.RecoverMaterial(context);

        byte[] contextKey = ContextKeyFor(platform);
        var recovered = new List<string>();
        foreach (byte[] key in PinlessTableKeys(platform))
        {
            byte[] plain = new HalyardControlFieldCrypto(key, material, contextKey)
                .DecryptField(HalyardRegistrationCipher.FieldCounter, response);
            if (HalyardRegistrationMessage.TryParsePairingRecord(plain, out HalyardPairingRecord? record)
                && record is not null)
            {
                recovered.Add(Encoding.ASCII.GetString(record.RegistrationKey).TrimEnd('\0'));
            }
        }

        Assert.True(
            recovered.Count == 0,
            "A pinless table key decrypted the account-route response — the account route may be the PIN route "
            + "minus the fold after all. Recovered registkey(s): " + string.Join(", ", recovered)
            + $"; the client actually used: {vector.ExpectRegistKey}. Revisit the finding.");
    }

    [SkippableTheory]
    [InlineData("ps5")]
    [InlineData("ps4")]
    public void NoPinRequest_HasTheSameBodyLayoutAsThePinRoute(string platform)
    {
        // 480-byte context + 107-byte field is what our PIN-route builder produces. Establishing that the
        // account route is identical here is what makes the send side reusable rather than a second codec.
        Vector vector = LoadOrSkip(platform);

        Assert.Equal(HalyardRegistrationCipher.ContextLength, Convert.FromHexString(vector.Context).Length);
        Assert.Equal(107, Convert.FromHexString(vector.RequestField).Length);
    }

    // ---- crypto plumbing -----------------------------------------------------------------------

    private static (HalyardRegistrationSecrets Secrets, byte[] ContextKey, int VersionSelector) BundledOrSkip(
        string platform)
    {
        HalyardConsolePlatform family = platform == "ps4"
            ? HalyardConsolePlatform.Ps4
            : HalyardConsolePlatform.Ps5;

        var bundled = HalyardInteropConstants.Registration(family);
        Skip.If(bundled is null, "Build omitted the interop constants (-p:BundleInteropConstants=false).");
        return bundled!.Value;
    }

    private static HalyardRegistrationCipher CipherFor(string platform)
    {
        var b = BundledOrSkip(platform);
        return new HalyardRegistrationCipher(new HalyardRegistrationKdf(b.Secrets, b.VersionSelector), b.ContextKey);
    }

    private static byte[] ContextKeyFor(string platform) => BundledOrSkip(platform).ContextKey;

    /// <summary>
    /// Every candidate transport key the account route could be using if it were "the PIN route without the
    /// fold": all 32 raw table entries, with nothing folded into the last four bytes. Passcode 0 would select
    /// exactly one of these (via <c>context[selectorOffset] &amp; 0x1f</c>); sweeping all 32 also covers the
    /// possibility that the account route selects its entry by some other rule.
    /// </summary>
    private static IEnumerable<byte[]> PinlessTableKeys(string platform)
    {
        var b = BundledOrSkip(platform);
        bool ps4 = platform == "ps4";

        for (int i = 0; i < HalyardRegistrationSecrets.TableEntryCount; i++)
        {
            yield return (ps4 ? b.Secrets.Ps4TableEntry(i) : b.Secrets.TableEntry(i)).ToArray();
        }
    }

    // ---- fixture -------------------------------------------------------------------------------

    private static Vector LoadOrSkip(string platform)
    {
        string path = Locate();
        Skip.IfNot(File.Exists(path),
            $"Dirty-room fixture not present ({path}). Generate it from cap63 to enable these.");

        Fixture? fx = JsonSerializer.Deserialize<Fixture>(
            File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Vector? vector = fx?.Vectors?.Find(v =>
            string.Equals(v.Platform, platform, StringComparison.OrdinalIgnoreCase));
        Skip.If(vector is null, $"Fixture carries no {platform} vector.");
        Skip.If(string.IsNullOrEmpty(vector!.ExpectRegistKey),
            $"The {platform} vector has no ground-truth registkey, so it cannot prove anything.");

        return vector;
    }

    private static string Locate()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            string candidate = Path.Combine(
                dir, "docs", "protocol", "captures", "nopin_registration_vectors.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        return Path.Combine(AppContext.BaseDirectory, "nopin_registration_vectors.json");
    }

    private sealed class Fixture
    {
        public string? Source { get; set; }

        public List<Vector>? Vectors { get; set; }
    }

    private sealed class Vector
    {
        public string Platform { get; set; } = "";

        public string Context { get; set; } = "";

        public string RequestField { get; set; } = "";

        public string ResponseBody { get; set; } = "";

        public string? ExpectRegistKey { get; set; }
    }
}
