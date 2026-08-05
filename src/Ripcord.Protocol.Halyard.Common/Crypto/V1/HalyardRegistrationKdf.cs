using System.Buffers.Binary;

namespace Ripcord.Protocol.Halyard.Common.Crypto.V1;

/// <summary>
/// The v1 registration (PIN-pairing) transport-key derivation.
///
/// <para>
/// Registration is a PIN-authenticated key exchange, not a static key derivation. The client puts a
/// freshly random context into the request body; a single 16-byte transport key <c>K</c> then protects
/// <em>both</em> the request field (Client-Type / Np-AccountId) and the response (the pairing record):
/// </para>
/// <code>
/// K = registrationTable[ context[selectorOffset] &amp; 0x1f ]   // one of 32 static 16-byte table entries
/// K[12..16] ^= big-endian-uint32(passcode)                     // the PIN folds into the last 4 bytes
/// </code>
/// <para>
/// The PIN fold is the whole authentication: the console recomputes <c>K</c> from the transmitted context
/// plus the passcode the user typed on-screen, so a client without the correct PIN derives the wrong key
/// and cannot decrypt the pairing record (registkey + companion). The table lookup itself is obfuscation,
/// not secrecy.
/// </para>
/// <para>
/// The <em>structure</em> here is our own reverse-engineering finding (validated end-to-end against live
/// captures — see LiveRegistrationVectorTests); the table <em>contents</em> and the selector offset are
/// protocol-variant-specific extracted interop constants supplied via <see cref="HalyardRegistrationSecrets"/>
/// (never committed — dirty room). The console negotiates the variant (by protocol id / RP-KeyType); this
/// carries one variant's <see cref="HalyardRegistrationSecrets"/> and others slot in behind the same seam.
/// </para>
/// </summary>
public sealed class HalyardRegistrationKdf
{
    /// <summary>The 16-byte transport-key length.</summary>
    public const int KeyLength = 16;

    private readonly HalyardRegistrationSecrets _secrets;
    private readonly bool _isPs4;

    /// <summary>
    /// <paramref name="versionSelector"/> picks the console-family variant (0 = PS4, anything else = the PS5
    /// variant). The two variants are the SAME mechanism (table lookup + PIN fold, plus the material wrap)
    /// and differ only in their key/wrap tables and the wrap bias — mirroring
    /// <see cref="HalyardControlKdf"/>. The PS4 variant additionally requires the PS4 tables to be present in
    /// <paramref name="secrets"/> (<see cref="HalyardRegistrationSecrets.HasPs4Tables"/>).
    /// </summary>
    public HalyardRegistrationKdf(HalyardRegistrationSecrets secrets, int versionSelector = 1)
    {
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _isPs4 = versionSelector == 0;
        if (_isPs4 && !_secrets.HasPs4Tables)
            throw new InvalidOperationException("PS4 registration tables are not loaded (this build/fixture omits them).");
    }

    /// <summary>
    /// Derive the 16-byte registration transport key from the transmitted request <paramref name="context"/>
    /// (the request body's leading random bytes; both peers hold it) and the 8-digit <paramref name="passcode"/>.
    /// </summary>
    public byte[] DeriveKey(ReadOnlySpan<byte> context, uint passcode)
    {
        int offset = _secrets.SelectorOffset;
        if (context.Length <= offset)
            throw new ArgumentException($"Context must be at least {offset + 1} bytes.", nameof(context));

        int index = context[offset] & 0x1f;
        byte[] key = (_isPs4 ? _secrets.Ps4TableEntry(index) : _secrets.TableEntry(index)).ToArray();

        Span<byte> fold = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(fold, passcode);
        for (int i = 0; i < fold.Length; i++)
            key[KeyLength - fold.Length + i] ^= fold[i];

        return key;
    }

    /// <summary>Parse an 8-digit passcode string to the <see cref="uint"/> folded into the key.</summary>
    public static uint ParsePasscode(string passcode)
    {
        ArgumentException.ThrowIfNullOrEmpty(passcode);
        if (!uint.TryParse(passcode, out uint value))
            throw new ArgumentException("Passcode must be a decimal number.", nameof(passcode));
        return value;
    }

    // ---- material transport (the wrapped material) ----
    //
    // The 16-byte per-pairing material is client-random but must reach the console; it travels in the
    // request context in *wrapped* form — a per-byte transform through a second table selected by
    // context[0]>>3, scattered to two fixed offsets. The console unwraps it to recover the material and
    // derive the field IV. Structure and offsets are our own RE: the table is at DAT_102f7ad1 in the vendor
    // binary, and the transform was validated end-to-end from cap22's wire alone (recover the material from
    // the context, derive K, decrypt the request field to its exact plaintext).

    /// <summary>Context byte whose top 5 bits select the wrap-table entry (<c>context[0] &gt;&gt; 3</c>).</summary>
    public const int MaterialSelectorOffset = 0;

    /// <summary>Context offset holding wrapped-material bytes [0..8).</summary>
    public const int WrappedOffsetLow = 0x191;

    /// <summary>Context offset holding wrapped-material bytes [8..16).</summary>
    public const int WrappedOffsetHigh = 0xc7;

    // The per-byte additive bias in the wrap transform, keyed by family. Both variants compute
    // w[i] = ((material[i] ^ table[i]) + bias + i) & 0xff; PS5 subtracts 0x2d (bias = -0x2d), PS4 adds 0x29.
    private const int Ps5WrapBiasAdd = -0x2d;
    private const int Ps4WrapBiasAdd = 0x29;
    private int WrapBiasAdd => _isPs4 ? Ps4WrapBiasAdd : Ps5WrapBiasAdd;
    private ReadOnlySpan<byte> WrapEntry(int index)
        => _isPs4 ? _secrets.Ps4WrapTableEntry(index) : _secrets.WrapTableEntry(index);

    /// <summary>
    /// Wrap the 16-byte <paramref name="material"/> for transmission, using the table entry selected by
    /// <paramref name="context"/>: <c>w[i] = ((material[i] ^ table[i]) + bias + i) &amp; 0xff</c> (PS5 bias
    /// -0x2d, PS4 bias +0x29).
    /// </summary>
    public byte[] WrapMaterial(ReadOnlySpan<byte> material, ReadOnlySpan<byte> context)
    {
        if (material.Length != KeyLength)
            throw new ArgumentException("Material must be 16 bytes.", nameof(material));
        ReadOnlySpan<byte> table = WrapEntry(context[MaterialSelectorOffset] >> 3);
        int bias = WrapBiasAdd;
        var wrapped = new byte[KeyLength];
        for (int i = 0; i < KeyLength; i++)
            wrapped[i] = (byte)((material[i] ^ table[i]) + bias + i);
        return wrapped;
    }

    /// <summary>Recover the material from its wrapped form (the console side): the inverse of
    /// <see cref="WrapMaterial"/>.</summary>
    public byte[] UnwrapMaterial(ReadOnlySpan<byte> wrapped, ReadOnlySpan<byte> context)
    {
        if (wrapped.Length != KeyLength)
            throw new ArgumentException("Wrapped material must be 16 bytes.", nameof(wrapped));
        ReadOnlySpan<byte> table = WrapEntry(context[MaterialSelectorOffset] >> 3);
        int bias = WrapBiasAdd;
        var material = new byte[KeyLength];
        for (int i = 0; i < KeyLength; i++)
            material[i] = (byte)(((wrapped[i] - i - bias) & 0xff) ^ table[i]);
        return material;
    }

    /// <summary>Scatter 16 bytes of wrapped material into the request context at the two fixed offsets.</summary>
    public static void ScatterWrapped(ReadOnlySpan<byte> wrapped, Span<byte> context)
    {
        wrapped[..8].CopyTo(context[WrappedOffsetLow..]);
        wrapped[8..].CopyTo(context[WrappedOffsetHigh..]);
    }

    /// <summary>Gather the 16 wrapped-material bytes back out of the request context (the console side).</summary>
    public static byte[] GatherWrapped(ReadOnlySpan<byte> context)
    {
        var wrapped = new byte[KeyLength];
        context.Slice(WrappedOffsetLow, 8).CopyTo(wrapped);
        context.Slice(WrappedOffsetHigh, 8).CopyTo(wrapped.AsSpan(8));
        return wrapped;
    }
}
