namespace Ripcord.Protocol.Halyard.Common.Crypto.V1;

/// <summary>
/// The v1 control-session key derivation. From a 16-byte nonce (the console's per-connect RP-Nonce) and
/// the 16-byte companion (the stored per-pairing RP-Key), it produces two 16-byte outputs:
/// <list type="bullet">
///   <item><description><b>Key</b> — the AES-128 key used directly for the control field cipher and the
///   streaminfo cipher. Derived from the <em>companion</em>.</description></item>
///   <item><description><b>Material</b> — the 16-byte material fed into the per-field IV derivation
///   (<see cref="HalyardFieldIv"/>); it is not itself an IV. Derived from the <em>nonce</em>.</description></item>
/// </list>
///
/// <para>
/// The transform is two independent per-byte passes over static lookup tables selected by two nonce
/// bytes. The <em>structure</em> below is our own reimplementation, validated end-to-end against a full
/// live session (cap22: derive → decrypt every /sess/ctrl field to its plaintext); the table
/// <em>contents</em> are extracted interop constants supplied via <see cref="HalyardControlSecrets"/>
/// (never committed — see that type's remarks).
/// </para>
///
/// <para>
/// <b>Role note:</b> the AES key is the companion-derived value and the IV material is the nonce-derived
/// value — not the other way round. An earlier revision had these two swapped; it passed the
/// primitive-level field-cipher tests (which feed key/material in directly) but produced garbage on a
/// real session, where the key/material come from this derivation. The <see cref="LiveControlVectorTests"/>
/// end-to-end vector guards against regressing that.
/// </para>
///
/// The console negotiates among several KDF variants (selected by RP-KeyType); this implements the
/// variant our captured sessions use. Other variants slot in behind the same seam when needed.
/// </summary>
public sealed class HalyardControlKdf
{
    private const int KeyLength = 16;

    private readonly HalyardControlSecrets _secrets;

    public HalyardControlKdf(HalyardControlSecrets secrets)
        => _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));

    /// <summary>
    /// Derive <c>(out1, out2)</c> from the 16-byte <paramref name="nonce"/> and 16-byte
    /// <paramref name="companion"/>.
    /// </summary>
    public (byte[] Key, byte[] Material) Derive(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> companion)
    {
        if (nonce.Length != KeyLength)
            throw new ArgumentException("Nonce must be 16 bytes.", nameof(nonce));
        if (companion.Length != KeyLength)
            throw new ArgumentException("Companion must be 16 bytes.", nameof(companion));

        // Tables are selected by the top 5 bits of two nonce bytes (>>3 => index 0..31).
        ReadOnlySpan<byte> table1Entry = _secrets.KdfTable1Entry(nonce[7] >> 3);
        ReadOnlySpan<byte> table2Entry = _secrets.KdfTable2Entry(nonce[0] >> 3);

        var key = new byte[KeyLength];       // the AES key (companion-derived)
        var material = new byte[KeyLength];  // the IV material (nonce-derived)
        for (int i = 0; i < KeyLength; i++)
        {
            key[i] = (byte)(((companion[i] + 0x18 + i) & 0xFF) ^ table1Entry[i] ^ nonce[i]);
            material[i] = (byte)(((nonce[i] - i - 0x2d) & 0xFF) ^ table2Entry[i]);
        }

        return (key, material);
    }
}
