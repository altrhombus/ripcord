namespace Ripcord.Protocol.Halyard.Takion;

/// <summary>
/// Reassembles control messages from reliable DATA chunks. A large ControlMessage (e.g. SESSION_REQUEST's
/// ~1.5 KB launchSpec) is split across consecutive DATA chunks on the same channel, each carrying a payload
/// fragment; only the final chunk has the ending bit set. This buffers fragments per channel and yields the
/// concatenated message when the ending chunk arrives (spec §8; fragmentation confirmed on the wire).
///
/// In-order delivery (dropping/holding out-of-order sequence numbers) is the reliable-delivery layer's job;
/// this type assumes it is fed the accepted, in-order chunks for a channel.
/// </summary>
public sealed class TakionMessageReassembler
{
    private readonly Dictionary<ushort, List<byte>> _partial = [];

    /// <summary>
    /// Feed one in-order DATA chunk. Returns the complete message bytes when <paramref name="chunk"/> ends a
    /// message, otherwise null (the fragment is buffered).
    /// </summary>
    public byte[]? Accept(in TakionDataChunk.Parsed chunk)
    {
        // "First" is positional: a fragment is the first of a message iff nothing is buffered for its channel.
        // The first fragment's payload sits at offset 9, continuation fragments at offset 8.
        bool first = !_partial.ContainsKey(chunk.Channel);
        ReadOnlyMemory<byte> frag = first ? chunk.FirstPayload : chunk.ContinuationPayload;

        // Fast path: a self-contained message (first fragment that also ends the message).
        if (chunk.EndOfMessage && first)
        {
            return frag.ToArray();
        }

        if (!_partial.TryGetValue(chunk.Channel, out var buffer))
        {
            buffer = [];
            _partial[chunk.Channel] = buffer;
        }

        buffer.AddRange(frag.Span);

        if (!chunk.EndOfMessage)
        {
            return null; // more fragments to come
        }

        _partial.Remove(chunk.Channel);
        return buffer.ToArray();
    }

    /// <summary>Discard any partially-accumulated fragments (e.g. on reset).</summary>
    public void Reset() => _partial.Clear();
}
