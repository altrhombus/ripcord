using Ripcord.Protocol.Halyard.Common.Crypto.V1;
using Ripcord.Protocol.Halyard.Session;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The interop constants bundled with the build must parse into usable secrets, so that a clone with no local
/// fixture can still pair and stream. These assert <em>shape</em>, never values — the point is that the bundle
/// is well-formed and wired to the right seams, and asserting bytes here would duplicate the live-vector tests
/// while making this file a second copy of the data.
///
/// <para>
/// All of these skip when the build omitted the bundle (<c>-p:BundleInteropConstants=false</c>), which is a
/// supported configuration rather than a failure.
/// </para>
/// </summary>
public class BundledInteropConstantsTests
{
    [SkippableFact]
    public void Control_BundleParsesIntoUsableSecrets()
    {
        Skip.IfNot(HalyardInteropConstants.IsBundled, "Build omitted the interop constants bundle.");

        var secrets = HalyardInteropConstants.Control();
        Assert.NotNull(secrets);

        // Both KDF tables must be the full 32 x 16-byte entry set the derivation indexes into.
        Assert.Equal(HalyardControlSecrets.KdfTableLength, secrets!.KdfTable1.Length);
        Assert.Equal(HalyardControlSecrets.KdfTableLength, secrets.KdfTable2.Length);

        // Every table entry must be addressable — a truncated bundle would throw here rather than at connect.
        for (int i = 0; i < HalyardControlSecrets.KdfTableEntryCount; i++)
        {
            Assert.Equal(HalyardControlSecrets.KdfTableEntrySize, secrets.KdfTable1Entry(i).Length);
            Assert.Equal(HalyardControlSecrets.KdfTableEntrySize, secrets.KdfTable2Entry(i).Length);
        }

        // All four context keys present and 16 bytes, for both selector paths.
        foreach (int codec in new[] { 0, 1 })
        {
            foreach (int version in new[] { 0, 1 })
            {
                Assert.Equal(16, secrets.ContextKeys.Select(codec, version).Length);
            }
        }
    }

    [SkippableFact]
    public void Control_BundleDrivesTheRealKdf()
    {
        Skip.IfNot(HalyardInteropConstants.IsBundled, "Build omitted the interop constants bundle.");

        // The bundle is only useful if it can actually key the derivation. Feed it fixed, meaningless inputs
        // and assert the KDF produces full-length outputs that differ from each other and from the inputs —
        // enough to prove the tables are wired in, without pinning any value.
        var kdf = new HalyardControlKdf(HalyardInteropConstants.Control()!);
        byte[] nonce = new byte[16];
        byte[] companion = new byte[16];
        for (int i = 0; i < 16; i++) { nonce[i] = (byte)(i + 1); companion[i] = (byte)(0x40 + i); }

        (byte[] key, byte[] material) = kdf.Derive(nonce, companion);

        Assert.Equal(16, key.Length);
        Assert.Equal(16, material.Length);
        Assert.NotEqual(key, material);
        Assert.NotEqual(companion, key);   // a table of zeros would fail this
        Assert.NotEqual(nonce, material);
    }

    [SkippableFact]
    public void Registration_BundleRoundTripsAPairingRequest()
    {
        Skip.IfNot(HalyardInteropConstants.IsBundled, "Build omitted the interop constants bundle.");

        var bundled = HalyardInteropConstants.Registration();
        Assert.NotNull(bundled);

        var (secrets, contextKey) = bundled!.Value;
        Assert.Equal(HalyardRegistrationSecrets.TableLength, secrets.Table.Length);
        Assert.Equal(16, contextKey.Length);
        Assert.True(secrets.SelectorOffset > 0);

        // The send path needs the wrap table; without it BuildRequest throws. This is the check that would
        // have caught the fixture-key rename silently disabling pairing.
        Assert.False(secrets.MaterialWrapTable.IsEmpty, "Bundle is missing the material wrap table.");

        // End-to-end through the console's own steps: build a request, then recover the field from the
        // transmitted context exactly as the console would. Proves the whole bundle is self-consistent.
        var cipher = new HalyardRegistrationCipher(new HalyardRegistrationKdf(secrets), contextKey);
        const string passcode = "12345678";
        byte[] plaintext = System.Text.Encoding.ASCII.GetBytes("Client-Type: test\r\n");

        var exchange = cipher.BuildRequest(passcode, plaintext);
        var context = exchange.RequestBody.AsSpan(0, HalyardRegistrationCipher.ContextLength).ToArray();
        var field = exchange.RequestBody.AsSpan(HalyardRegistrationCipher.ContextLength).ToArray();

        byte[] material = cipher.RecoverMaterial(context);
        byte[] recovered = cipher.DecryptWith(context, HalyardRegistrationKdf.ParsePasscode(passcode), material, field);

        Assert.Equal(plaintext, recovered);
    }

    [SkippableFact]
    public void Bundle_CarriesNoLiveVectorMaterial()
    {
        Skip.IfNot(HalyardInteropConstants.IsBundled, "Build omitted the interop constants bundle.");

        // A guard against the bundle ever regaining the personal half of the dirty-room fixtures. The
        // generic tables are shipped deliberately; anything tied to a specific console or account is not.
        using var stream = typeof(HalyardInteropConstants).Assembly
            .GetManifestResourceStream("Ripcord.Protocol.Halyard.Data.halyard-v1-constants.json");
        Assert.NotNull(stream);
        string json = new StreamReader(stream!).ReadToEnd();

        // Per-console / per-account material. The list is deliberately wider than the vector-file field names:
        // it also covers the identifiers a future regeneration could plausibly sweep in (registkey, session and
        // device ids, duid). Those four were named in ROADMAP as being enforced here when they were not — the
        // guard was narrower than the promise made for it, which is the failure mode this test exists to prevent.
        foreach (string forbidden in new[]
                 {
                     "companion", "nonce", "passcode", "responseBody", "plaintext", "ciphertext",
                     "ctrlHandshake", "kdfVectors", "fieldVectors", "keyVectors", "Np-Account", "RP-Did", "RP-Auth",
                     "registkey", "registrationKey", "sessionKey", "deviceId", "duid", "accountId", "handshakeKey",
                 })
        {
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
        }
    }
}
