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

    public HalyardRegistrationSecrets(ReadOnlyMemory<byte> table, int selectorOffset, ReadOnlyMemory<byte> materialWrapTable = default)
    {
        if (table.Length != TableLength)
            throw new ArgumentException($"Registration table must be {TableLength} bytes.", nameof(table));
        if (selectorOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(selectorOffset));
        if (!materialWrapTable.IsEmpty && materialWrapTable.Length != TableLength)
            throw new ArgumentException($"Material wrap table must be {TableLength} bytes.", nameof(materialWrapTable));

        Table = table;
        SelectorOffset = selectorOffset;
        MaterialWrapTable = materialWrapTable;
    }

    /// <summary>The 32 registration-key entries (16 bytes each), laid out contiguously.</summary>
    public ReadOnlyMemory<byte> Table { get; }

    /// <summary>
    /// The 32 wrap-table entries (16 bytes each) used to transform the per-pairing material into the
    /// wrapped form carried in the request context. Empty when only the response/decrypt path is exercised.
    /// </summary>
    public ReadOnlyMemory<byte> MaterialWrapTable { get; }

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

    private static ReadOnlySpan<byte> Entry(ReadOnlySpan<byte> table, int index)
    {
        if ((uint)index >= TableEntryCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        return table.Slice(index * TableEntrySize, TableEntrySize);
    }
}
