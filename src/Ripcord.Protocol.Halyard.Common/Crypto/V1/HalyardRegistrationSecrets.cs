namespace Ripcord.Protocol.Halyard.Common.Crypto.V1;

/// <summary>
/// The extracted, on-the-wire interoperability constants the v1 registration key derivation needs but
/// which are deliberately NOT baked into this repository (clean-room discipline: same rationale as
/// <see cref="HalyardControlSecrets"/>). They are data the client reads from its own binary — a static
/// lookup table and the context-byte offset that selects into it — for one negotiated protocol variant,
/// supplied as configuration from the gitignored dirty room.
///
/// <para>
/// The registration key is <c>K = Table[ context[SelectorOffset] &amp; 0x1f ]</c> with the passcode folded
/// into the last 4 bytes (see <see cref="HalyardRegistrationKdf"/>). <see cref="Table"/> holds the 32
/// entries of 16 bytes each, already laid out contiguously (the dirty-room loader transposes the binary's
/// column-major storage into row order — the layout here is ours, not the binary's).
/// <see cref="SelectorOffset"/> is the context byte read to pick the entry (e.g. 0x18d for the
/// RP-Version 1.0 variant).
/// </para>
/// </summary>
public sealed class HalyardRegistrationSecrets
{
    public const int TableEntryCount = 32;
    public const int TableEntrySize = 16;
    public const int TableLength = TableEntryCount * TableEntrySize; // 512

    public HalyardRegistrationSecrets(
        ReadOnlyMemory<byte> table,
        int selectorOffset,
        ReadOnlyMemory<byte> materialWrapTable = default,
        ReadOnlyMemory<byte> ps4Table = default,
        ReadOnlyMemory<byte> ps4MaterialWrapTable = default)
    {
        if (table.Length != TableLength)
            throw new ArgumentException($"Registration table must be {TableLength} bytes.", nameof(table));
        if (selectorOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(selectorOffset));
        if (!materialWrapTable.IsEmpty && materialWrapTable.Length != TableLength)
            throw new ArgumentException($"Material wrap table must be {TableLength} bytes.", nameof(materialWrapTable));
        if (!ps4Table.IsEmpty && ps4Table.Length != TableLength)
            throw new ArgumentException($"PS4 registration table must be {TableLength} bytes.", nameof(ps4Table));
        if (!ps4MaterialWrapTable.IsEmpty && ps4MaterialWrapTable.Length != TableLength)
            throw new ArgumentException($"PS4 material wrap table must be {TableLength} bytes.", nameof(ps4MaterialWrapTable));

        Table = table;
        SelectorOffset = selectorOffset;
        MaterialWrapTable = materialWrapTable;
        Ps4Table = ps4Table;
        Ps4MaterialWrapTable = ps4MaterialWrapTable;
    }

    /// <summary>The 32 registration-key entries (16 bytes each), laid out contiguously.</summary>
    public ReadOnlyMemory<byte> Table { get; }

    /// <summary>
    /// The 32 wrap-table entries (16 bytes each) used to transform the per-pairing material into the
    /// wrapped form carried in the request context. Empty when only the response/decrypt path is exercised.
    /// </summary>
    public ReadOnlyMemory<byte> MaterialWrapTable { get; }

    /// <summary>The PS4 registration-key table (32×16), or empty when this build/fixture omits it.</summary>
    public ReadOnlyMemory<byte> Ps4Table { get; }

    /// <summary>The PS4 material-wrap table (32×16), or empty when omitted.</summary>
    public ReadOnlyMemory<byte> Ps4MaterialWrapTable { get; }

    /// <summary>Whether the PS4 registration tables are present (the PS4 family variant is usable).</summary>
    public bool HasPs4Tables => !Ps4Table.IsEmpty && !Ps4MaterialWrapTable.IsEmpty;

    /// <summary>The context byte offset whose low 5 bits select the key table entry.</summary>
    public int SelectorOffset { get; }

    /// <summary>The 16-byte key entry at <paramref name="index"/> (0..31).</summary>
    public ReadOnlySpan<byte> TableEntry(int index) => Entry(Table.Span, index);

    /// <summary>The 16-byte wrap-table entry at <paramref name="index"/> (0..31).</summary>
    public ReadOnlySpan<byte> WrapTableEntry(int index)
    {
        if (MaterialWrapTable.IsEmpty)
            throw new InvalidOperationException("Material wrap table not provided (needed for the outbound/send path).");
        return Entry(MaterialWrapTable.Span, index);
    }

    /// <summary>The 16-byte PS4 key entry at <paramref name="index"/> (0..31).</summary>
    public ReadOnlySpan<byte> Ps4TableEntry(int index)
    {
        if (Ps4Table.IsEmpty)
            throw new InvalidOperationException("PS4 registration table not provided (this build/fixture omits it).");
        return Entry(Ps4Table.Span, index);
    }

    /// <summary>The 16-byte PS4 wrap-table entry at <paramref name="index"/> (0..31).</summary>
    public ReadOnlySpan<byte> Ps4WrapTableEntry(int index)
    {
        if (Ps4MaterialWrapTable.IsEmpty)
            throw new InvalidOperationException("PS4 material wrap table not provided (needed for the PS4 outbound/send path).");
        return Entry(Ps4MaterialWrapTable.Span, index);
    }

    private static ReadOnlySpan<byte> Entry(ReadOnlySpan<byte> table, int index)
    {
        if ((uint)index >= TableEntryCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        return table.Slice(index * TableEntrySize, TableEntrySize);
    }
}
