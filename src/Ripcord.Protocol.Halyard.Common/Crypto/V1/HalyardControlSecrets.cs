namespace Ripcord.Protocol.Halyard.Common.Crypto.V1;

/// <summary>
/// The extracted, on-the-wire interoperability constants the v1 control-plane crypto needs but which are
/// deliberately NOT baked into this repository (clean-room discipline: see docs/protocol/README.md and
/// IMPLEMENTATION.md "Two inputs that live outside these committed docs"). They are values the console
/// itself computes against, so they cannot be changed without breaking interoperability, but they are
/// data the client reads from its own binary — not authored code — and so are supplied as configuration
/// from the gitignored dirty room rather than committed here.
///
/// <para>
/// Contents:
/// <list type="bullet">
///   <item><description><see cref="KdfTable1"/>/<see cref="KdfTable2"/>: the two session-KDF lookup
///   tables (32 entries of 16 bytes each) indexed by nonce bytes — see <see cref="HalyardControlKdf"/>.</description></item>
///   <item><description><see cref="ContextKeys"/>: the four per-field-IV context keys and their
///   selection rule — see <see cref="HalyardFieldContextKeys"/>.</description></item>
/// </list>
/// </para>
/// A loader that populates these from the dirty-room fixture lives outside the committed tree.
/// </summary>
public sealed class HalyardControlSecrets
{
    public const int KdfTableEntryCount = 32;
    public const int KdfTableEntrySize = 16;
    public const int KdfTableLength = KdfTableEntryCount * KdfTableEntrySize; // 512

    public HalyardControlSecrets(
        ReadOnlyMemory<byte> kdfTable1,
        ReadOnlyMemory<byte> kdfTable2,
        HalyardFieldContextKeys contextKeys)
    {
        if (kdfTable1.Length != KdfTableLength)
            throw new ArgumentException($"KDF table 1 must be {KdfTableLength} bytes.", nameof(kdfTable1));
        if (kdfTable2.Length != KdfTableLength)
            throw new ArgumentException($"KDF table 2 must be {KdfTableLength} bytes.", nameof(kdfTable2));

        KdfTable1 = kdfTable1;
        KdfTable2 = kdfTable2;
        ContextKeys = contextKeys ?? throw new ArgumentNullException(nameof(contextKeys));
    }

    /// <summary>Table selected by <c>nonce[7] &gt;&gt; 3</c> (32 entries of 16 bytes).</summary>
    public ReadOnlyMemory<byte> KdfTable1 { get; }

    /// <summary>Table selected by <c>nonce[0] &gt;&gt; 3</c> (32 entries of 16 bytes).</summary>
    public ReadOnlyMemory<byte> KdfTable2 { get; }

    public HalyardFieldContextKeys ContextKeys { get; }

    /// <summary>The 16-byte entry at <paramref name="index"/> (0..31) of the given table.</summary>
    public ReadOnlySpan<byte> KdfTable1Entry(int index) => Entry(KdfTable1.Span, index);

    public ReadOnlySpan<byte> KdfTable2Entry(int index) => Entry(KdfTable2.Span, index);

    private static ReadOnlySpan<byte> Entry(ReadOnlySpan<byte> table, int index)
    {
        if ((uint)index >= KdfTableEntryCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        return table.Slice(index * KdfTableEntrySize, KdfTableEntrySize);
    }
}

/// <summary>
/// The four 16-byte context keys used to derive per-field IVs, plus the selection rule. The key
/// <em>values</em> are extracted constants (supplied via configuration); the selection rule is the
/// protocol's own logic and lives in code.
/// </summary>
public sealed class HalyardFieldContextKeys
{
    public HalyardFieldContextKeys(
        ReadOnlyMemory<byte> codecInHigh,
        ReadOnlyMemory<byte> selectorOne,
        ReadOnlyMemory<byte> selectorZero,
        ReadOnlyMemory<byte> fallbackZero)
    {
        CodecInHigh = Require(codecInHigh, nameof(codecInHigh));
        SelectorOne = Require(selectorOne, nameof(selectorOne));
        SelectorZero = Require(selectorZero, nameof(selectorZero));
        FallbackZero = Require(fallbackZero, nameof(fallbackZero));
    }

    /// <summary>Chosen when the codec/type selector is in its high band; wins over the selector below.</summary>
    public ReadOnlyMemory<byte> CodecInHigh { get; }

    /// <summary>Chosen when the version selector resolves to 1 (the variant our captures use).</summary>
    public ReadOnlyMemory<byte> SelectorOne { get; }

    /// <summary>Chosen when the version selector resolves to 0.</summary>
    public ReadOnlyMemory<byte> SelectorZero { get; }

    /// <summary>The all-zero fallback for any other selector combination.</summary>
    public ReadOnlyMemory<byte> FallbackZero { get; }

    /// <summary>
    /// Select the context key from the two negotiated selectors. <paramref name="codecSelector"/> is a
    /// codec/type enum; <paramref name="versionSelector"/> is the protocol/version discriminator that also
    /// picks the KDF variant. The high-band codec case is checked first and wins regardless of the version.
    /// </summary>
    public ReadOnlyMemory<byte> Select(int codecSelector, int versionSelector) => codecSelector switch
    {
        8 or 9 => CodecInHigh,
        _ => versionSelector switch
        {
            1 => SelectorOne,
            0 => SelectorZero,
            _ => FallbackZero,
        },
    };

    private static ReadOnlyMemory<byte> Require(ReadOnlyMemory<byte> value, string name)
        => value.Length == 16 ? value : throw new ArgumentException("Context key must be 16 bytes.", name);
}
