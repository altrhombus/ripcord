using Ripcord.Core.Sessions;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Streaming.Fec;

namespace Ripcord.Protocol.Halyard.Common.Streaming;

/// <summary>
/// Splits the multiplexed stream into video/audio/other by the packet type (low nibble of byte 0),
/// authenticates + decrypts each media packet through the crypto seam, and reassembles video fragments
/// into whole encoded frames. With <see cref="PassthroughHalyardSessionCrypto"/> the open step is identity
/// (payloads are already plaintext), so this runs end to end against captures/synthetic data; the real
/// crypto slots in unchanged.
///
/// Per-packet crypto is keyed by the header's key position and covers the whole packet (spec §5.5), so the
/// demuxer hands the crypto the full packet and reads the decrypted payload back from
/// <see cref="HalyardStreamHeader.PayloadOffset"/>.
///
/// Video reassembly keys on the frame index: fragments accumulate while the frame index is constant and
/// the previous frame is flushed when a new index arrives (spec §6.1).
/// </summary>
public sealed class HalyardStreamDemuxer(IHalyardSessionCrypto crypto)
{
    /// <summary>
    /// Each decrypted video unit payload begins with a 2-byte size-extension/padding field before the Annex-B
    /// NAL start code (<c>[2-byte prefix][00 00 00 01][slice]</c>). The prefix drives both the FEC unit size
    /// (it's added to the transmitted size to get the common padded unit size) and slice extraction (skipped so
    /// the emitted frame is clean Annex-B).
    /// </summary>
    private const int VideoUnitPrefixLength = 2;

    /// <summary>Sanity cap on units per frame (matches the reference implementation's UNIT_SLOTS_MAX).</summary>
    private const int MaxUnitsPerFrame = 512;

    private readonly IHalyardSessionCrypto _crypto = crypto;
    // Scratch for the assembled Annex-B frame. A plain array rather than List<byte> so it can be handed to
    // subscribers as a ReadOnlyMemory slice with no copy — List does not expose its backing store as Memory.
    // Reused across frames; see the lifetime contract on VideoFrameReady.
    private byte[] _assembly = new byte[64 * 1024];
    private int _assemblyLength;

    // Current-frame reassembly is slot-buffered by unit index (so out-of-order units land correctly and FEC can
    // reconstruct lost source units from the parity units). Each unit occupies one stride-sized slot; source
    // units are indices [0, _sourceExpected), parity units follow.
    private int _frameIndex = -1;
    private long _frameTimestampTicks;
    private int _sourceExpected;
    private int _fecExpected;
    private int _fecActual; // FEC units actually on the wire (ParityUnits); _fecExpected forces a min of 1 slot
    private int _sourceReceived;
    private int _fecReceived;

    // Accumulated wire packet stats for congestion feedback (received vs lost units across frames). Read+reset
    // via TakePacketStats from the congestion loop, so guarded with Interlocked.
    private long _statUnitsReceived;
    private long _statUnitsLost;
    // The frame's coded unit length — the number of bytes FEC actually codes over, shared by every unit. This
    // is the wire-bearing value of the two: it must equal what the console coded over or recovery reconstructs
    // garbage. Confirmed [C]/[W]: the console's length is the longest source unit rounded UP TO A MULTIPLE OF
    // 4, constant within a frame. Verified on the wire across 5,961 frames of cap47 (parity length constant
    // per frame, never exceeded by any source unit, and parity_len - max(source_len) only ever 0..3) and in
    // our binary, where FUN_101035e0 rejects any fragment length with (len & 3) != 0.
    private int _unitPaddedSize;

    // Slot spacing in _slotBuf. NOT a protocol value — the console lays its units out at a stride equal to the
    // coded length itself, with no 16-alignment anywhere. This is purely our own buffer layout, and any value
    // >= _unitPaddedSize is equally correct: all coding reads and writes [slot, slot + _unitPaddedSize), the
    // buffer is zero-cleared per frame, and the tail between the two is never read. Rounding to 16 buys
    // alignment for the copies; 4 (the console's own granularity) would be just as correct and no faster.
    // Recorded because this was long carried as a suspected live FEC bug on the theory that 16 was a mis-derived
    // wire constant — it is not a wire constant at all, and the value that IS one is _unitPaddedSize above.
    private int _unitStride;
    private byte[] _slotBuf = [];
    private bool[] _slotPresent = [];
    private int[] _slotDataSize = [];
    private bool _frameAllocated;

    // The SPS/PPS parameter sets from STREAM_INFO (not carried in the video stream); prepended ahead of every
    // emitted IDR so the decoder can (re)initialise.
    private byte[]? _videoHeader;

    /// <summary>
    /// Which codec the stream carries, inferred from the out-of-band parameter sets. Needed because "is this an
    /// IDR" is asked of the NAL header, and the two codecs encode that differently.
    /// </summary>
    private bool _videoIsHevc;

    /// <summary>Supply the parameter sets parsed from the console's STREAM_INFO (per-profile videoHeader).</summary>
    public void SetVideoHeader(ReadOnlySpan<byte> header)
    {
        if (!header.IsEmpty)
        {
            _videoHeader = header.ToArray();
            _videoIsHevc = LooksLikeHevcParameterSets(_videoHeader);
        }
    }

    /// <summary>
    /// Classify the out-of-band parameter sets as HEVC or H.264.
    ///
    /// <para>
    /// The parameter sets are the one place the two codecs cannot be confused. H.264 carries a 1-byte NAL header
    /// whose 5-bit type is 7 (SPS) or 8 (PPS); HEVC carries 2 bytes whose 6-bit type is 32 (VPS), 33 (SPS) or 34
    /// (PPS) — values H.264 cannot express. Slice headers are NOT safe to classify this way: a perfectly ordinary
    /// H.264 non-IDR slice byte of 0x21 reads as HEVC type 16, so testing both interpretations against a slice
    /// would produce false positives. Hence classify once, here, from the sets.
    /// </para>
    /// </summary>
    internal static bool LooksLikeHevcParameterSets(ReadOnlySpan<byte> header)
    {
        for (int i = 0; i + 2 < header.Length; i++)
        {
            if (header[i] != 0 || header[i + 1] != 0)
            {
                continue;
            }

            int payload;
            if (header[i + 2] == 0x01)
            {
                payload = i + 3;
            }
            else if (header[i + 2] == 0x00 && i + 3 < header.Length && header[i + 3] == 0x01)
            {
                payload = i + 4;
            }
            else
            {
                continue;
            }

            if (payload + 1 >= header.Length)
            {
                break;
            }

            byte b0 = header[payload];
            byte b1 = header[payload + 1];

            // A 6-bit type of 32/33/34 alone is NOT sufficient: H.264's very common 0x41 (nal_ref_idc 2,
            // non-IDR slice) reads as HEVC type 32. So validate the whole 2-byte HEVC header — forbidden_zero
            // clear, nuh_layer_id 0, and nuh_temporal_id_plus1 == 1, which for a parameter set means the second
            // byte is exactly 0x01. That rejects 0x41 0x9A while accepting real 0x40/0x42/0x44 0x01 sets.
            int hevcType = (b0 >> 1) & 0x3F;
            bool hevcHeaderValid = (b0 & 0x80) == 0 && (b0 & 0x01) == 0 && b1 == 0x01;
            if (hevcHeaderValid && hevcType is 32 or 33 or 34)
            {
                return true;
            }

            if ((b0 & 0x80) == 0 && (b0 & 0x1F) is 7 or 8)
            {
                return false;
            }

            i = payload;
        }

        return false;
    }

    /// <summary>
    /// Raised with each assembled Annex-B video frame.
    ///
    /// <para>
    /// <b>Lifetime:</b> <c>Payload</c> is a slice of a buffer this demuxer reuses for the NEXT frame. It is valid
    /// only for the duration of the callback — a subscriber that needs it afterwards must copy it. Publication is
    /// synchronous, and the one consumer (the decode pipeline) copies into a pooled buffer immediately, which is
    /// what makes this safe; a subscriber that retained the memory instead would see it overwritten mid-decode and
    /// produce corruption that looks like a network fault. Copy, or do not keep it.
    /// </para>
    /// </summary>
    public event Action<EncodedVideoFrame>? VideoFrameReady;
    public event Action<EncodedAudioFrame>? AudioFrameReady;

    /// <summary>
    /// Raised when video slices were lost — either a frame flushed incomplete or the frame index jumped
    /// forward. Carries the inclusive range of affected frame indices. A subscriber reports these to the
    /// console (CORRUPT_FRAME) and requests a fresh IDR so the picture recovers instead of accumulating
    /// reference-chain corruption. Frame indices are the console's 16-bit values.
    /// </summary>
    public event Action<int, int>? VideoLossDetected;

    /// <summary>Non-media packets (control/congestion/FEC) surfaced for feedback/diagnostics.</summary>
    public event Action<HalyardStreamHeader, ReadOnlyMemory<byte>>? ControlPacketReceived;

    /// <summary>Number of media packets that failed GMAC verification (diagnostics).</summary>
    public long AuthFailures { get; private set; }

    /// <summary>
    /// Atomically read and reset the accumulated wire unit stats (received, lost) since the last call — the
    /// input to the periodic congestion-feedback packet that lets the console's rate controller adapt.
    /// </summary>
    public (long Received, long Lost) TakePacketStats()
        => (Interlocked.Exchange(ref _statUnitsReceived, 0), Interlocked.Exchange(ref _statUnitsLost, 0));

    /// <summary>Feed one whole UDP payload from the stream port.</summary>
    public void Ingest(ReadOnlySpan<byte> packet)
    {
        if (!HalyardStreamHeader.TryParse(packet, out var header))
        {
            return;
        }

        switch (header.Type)
        {
            case HalyardStreamHeader.TypeVideo:
                IngestVideo(header, packet);
                break;
            case HalyardStreamHeader.TypeAudio:
                IngestAudio(header, packet);
                break;
            default:
                int copyFrom = Math.Min(header.PayloadOffset, packet.Length);
                ControlPacketReceived?.Invoke(header, packet[copyFrom..].ToArray());
                break;
        }
    }

    private void IngestVideo(in HalyardStreamHeader header, ReadOnlySpan<byte> packet)
    {
        if (packet.Length <= header.PayloadOffset)
        {
            return;
        }

        if (!TryOpenMedia(header, packet, out byte[] opened))
        {
            return;
        }

        int dataSize = opened.Length - header.PayloadOffset;
        if (dataSize <= 0)
        {
            return;
        }

        ReadOnlySpan<byte> payload = opened.AsSpan(header.PayloadOffset, dataSize);

        if (header.FrameIndex != _frameIndex)
        {
            // A stale unit from an already-finished (older) frame — drop it rather than restarting reassembly.
            int diff = (header.FrameIndex - _frameIndex) & 0xffff;
            if (_frameIndex >= 0 && diff >= 0x8000)
            {
                return;
            }

            if (_frameIndex >= 0)
            {
                CheckForFrameGap(header.FrameIndex);
                FlushVideoFrame();
            }

            _frameIndex = header.FrameIndex;
            _frameTimestampTicks = DateTime.UtcNow.Ticks;
            AllocateFrame(header, dataSize, payload);
        }

        if (_frameAllocated)
        {
            PlaceUnit(header, dataSize, payload);
        }
    }

    /// <summary>Size the slot buffer for a new frame from its first-arriving unit (which fixes the common
    /// padded unit size). Leaves <see cref="_frameAllocated"/> false on invalid geometry so the frame is skipped.</summary>
    private void AllocateFrame(in HalyardStreamHeader header, int dataSize, ReadOnlySpan<byte> payload)
    {
        _frameAllocated = false;

        int source = header.SourceUnits;
        int fec = Math.Max(1, header.ParityUnits);
        int slots = source + fec;
        if (source <= 0 || slots > MaxUnitsPerFrame)
        {
            return;
        }

        // Source units carry a 2-byte size-extension that is ADDED to the transmitted size to get the common
        // coded unit length all units share for FEC; parity units are already exactly that length. Both arrival
        // orders therefore land on the same value, which is why this can key off whichever unit comes first.
        int padded = dataSize;
        if (header.UnitIndex < source && payload.Length >= VideoUnitPrefixLength)
        {
            padded += (payload[0] << 8) | payload[1];
        }

        if (padded <= 0)
        {
            return;
        }

        int stride = (padded + 0xf) & ~0xf;
        int bufNeeded = slots * stride;
        if (_slotBuf.Length < bufNeeded)
        {
            _slotBuf = new byte[bufNeeded];
        }
        else
        {
            Array.Clear(_slotBuf, 0, bufNeeded);
        }

        if (_slotPresent.Length < slots)
        {
            _slotPresent = new bool[slots];
            _slotDataSize = new int[slots];
        }
        else
        {
            Array.Clear(_slotPresent, 0, slots);
            Array.Clear(_slotDataSize, 0, slots);
        }

        _sourceExpected = source;
        _fecExpected = fec;
        _fecActual = header.ParityUnits;
        _unitPaddedSize = padded;
        _unitStride = stride;
        _sourceReceived = 0;
        _fecReceived = 0;
        _frameAllocated = true;
    }

    /// <summary>Copy one decrypted unit into its slot (indexed by unit index, so arrival order is irrelevant).</summary>
    private void PlaceUnit(in HalyardStreamHeader header, int dataSize, ReadOnlySpan<byte> payload)
    {
        int idx = header.UnitIndex;
        if (idx >= _sourceExpected + _fecExpected || _slotPresent[idx] || dataSize > _unitPaddedSize)
        {
            return;
        }

        payload.CopyTo(_slotBuf.AsSpan(idx * _unitStride));
        _slotPresent[idx] = true;
        _slotDataSize[idx] = dataSize;
        if (idx < _sourceExpected)
        {
            _sourceReceived++;
        }
        else
        {
            _fecReceived++;
        }
    }

    /// <summary>
    /// On a frame-index change, report whole frames that went missing between the frame we just finished and
    /// <paramref name="newFrameIndex"/> (forward jumps only; a backward index is an out-of-order straggler).
    /// Per-frame slice loss is reported from <see cref="FlushVideoFrame"/> after FEC has had its chance.
    /// </summary>
    private void CheckForFrameGap(int newFrameIndex)
    {
        int expected = (_frameIndex + 1) & 0xffff;
        int forwardGap = (newFrameIndex - expected) & 0xffff;
        if (forwardGap is > 0 and < 0x8000)
        {
            VideoLossDetected?.Invoke(expected, (newFrameIndex - 1) & 0xffff);
        }
    }

    private void FlushVideoFrame()
    {
        if (!_frameAllocated || _sourceExpected == 0)
        {
            _frameAllocated = false;
            return;
        }

        // Record wire loss for congestion feedback (before FEC — FEC recovery doesn't change what the network
        // actually dropped). _fecActual (not the min-1 slot count) so a frame with no parity isn't false loss.
        long expectedUnits = _sourceExpected + _fecActual;
        long receivedUnits = _sourceReceived + _fecReceived;
        Interlocked.Add(ref _statUnitsReceived, receivedUnits);
        Interlocked.Add(ref _statUnitsLost, Math.Max(0, expectedUnits - receivedUnits));

        // Recover missing source units from the parity units when enough total units survived.
        if (_sourceReceived < _sourceExpected &&
            _sourceReceived + _fecReceived >= _sourceExpected &&
            CauchyReedSolomon.Decode(_slotBuf, _unitPaddedSize, _unitStride, _sourceExpected, _fecExpected, _slotPresent))
        {
            // Restore the reconstructed source units' data sizes from their now-recovered padding field.
            for (int i = 0; i < _sourceExpected; i++)
            {
                if (_slotPresent[i])
                {
                    continue;
                }

                int off = i * _unitStride;
                int padding = (_slotBuf[off] << 8) | _slotBuf[off + 1];
                if (padding < _unitPaddedSize)
                {
                    _slotDataSize[i] = _unitPaddedSize - padding;
                    _slotPresent[i] = true;
                }
            }
        }

        // A frame is a keyframe iff its slices are IDR NAL units (type 5). This drives both the IsKeyFrame flag
        // and the SPS/PPS re-send below — more reliable than guessing from the slice count.
        bool isKey = FirstSourceSliceIsIdr();

        // Assemble clean Annex-B: prepend SPS/PPS ahead of every IDR (the sets arrive out-of-band in
        // STREAM_INFO, and a hardware decoder needs them to (re)initialise — resending on each IDR lets the
        // picture resync cleanly after loss). Then append each source unit's slice (payload past the prefix).
        _assemblyLength = 0;
        if (isKey && _videoHeader is not null)
        {
            AppendToAssembly(_videoHeader);
        }

        bool incomplete = false;
        for (int i = 0; i < _sourceExpected; i++)
        {
            if (!_slotPresent[i])
            {
                incomplete = true;
                continue;
            }

            int size = _slotDataSize[i];
            if (size <= VideoUnitPrefixLength)
            {
                continue;
            }

            AppendToAssembly(_slotBuf.AsSpan(i * _unitStride + VideoUnitPrefixLength, size - VideoUnitPrefixLength));
        }

        _frameAllocated = false;

        // Slices lost that FEC could not recover — ask the console for a fresh IDR so the picture recovers fast.
        if (incomplete)
        {
            VideoLossDetected?.Invoke(_frameIndex, _frameIndex);
        }

        if (_assemblyLength == 0)
        {
            return;
        }

        // A slice of the reused buffer, not a copy. Valid only for the duration of this call — see the contract on
        // VideoFrameReady. This removed one allocation AND one full copy of every frame: the only consumer already
        // copies into its own pooled buffer, so the array handed out here was pure churn (~2.5 MB/s at 1080p60).
        VideoFrameReady?.Invoke(
            new EncodedVideoFrame(
                new ReadOnlyMemory<byte>(_assembly, 0, _assemblyLength), _frameTimestampTicks, isKey));
    }

    /// <summary>Append to the assembly buffer, growing it if needed.</summary>
    private void AppendToAssembly(ReadOnlySpan<byte> data)
    {
        int required = _assemblyLength + data.Length;
        if (required > _assembly.Length)
        {
            // Double until it fits. Frames are bounded by the console's own packetisation, so this settles after
            // the first few frames and never grows again.
            int capacity = _assembly.Length;
            while (capacity < required)
            {
                capacity *= 2;
            }

            Array.Resize(ref _assembly, capacity);
        }

        data.CopyTo(_assembly.AsSpan(_assemblyLength));
        _assemblyLength += data.Length;
    }

    /// <summary>
    /// Whether the first present source unit's slice is an IDR (H.264 NAL type 5). Scans past the 2-byte unit
    /// prefix and the Annex-B start code (3- or 4-byte) to the NAL header byte.
    /// </summary>
    private bool FirstSourceSliceIsIdr()
    {
        for (int i = 0; i < _sourceExpected; i++)
        {
            if (!_slotPresent[i] || _slotDataSize[i] <= VideoUnitPrefixLength)
            {
                continue;
            }

            ReadOnlySpan<byte> slice = _slotBuf.AsSpan(
                i * _unitStride + VideoUnitPrefixLength, _slotDataSize[i] - VideoUnitPrefixLength);

            int p = 0;
            while (p < slice.Length && slice[p] == 0)
            {
                p++;
            }

            if (p < slice.Length && slice[p] == 0x01 && p + 1 < slice.Length)
            {
                byte nal = slice[p + 1];

                // HEVC: nal_unit_type is the 6 bits after the forbidden_zero bit, and every IRAP picture — the
                // ones a decoder can start from — is 16..21 (BLA_W_LP..CRA_NUT, including IDR_W_RADL 19 and
                // IDR_N_LP 20). Testing H.264's type 5 against an HEVC stream matches essentially never, which
                // meant the parameter sets below were never prepended and an HEVC decoder was handed slices it
                // could not initialise from — it accepted every packet and emitted nothing, forever.
                if (_videoIsHevc)
                {
                    int hevcType = (nal >> 1) & 0x3F;
                    return hevcType is >= 16 and <= 21;
                }

                return (nal & 0x1f) == 5; // 5 = coded slice of an IDR picture
            }

            return false; // no parseable start code — treat as non-key
        }

        return false;
    }

    /// <summary>Audio codec id for Opus (the only audio codec the console uses); other codecs are ignored.</summary>
    private const byte OpusCodec = 5;

    private void IngestAudio(in HalyardStreamHeader header, ReadOnlySpan<byte> packet)
    {
        if (packet.Length <= header.PayloadOffset || header.Codec != OpusCodec)
        {
            return;
        }

        if (!TryOpenMedia(header, packet, out byte[] opened))
        {
            return;
        }

        // The audio payload packs TotalUnits equal-size units back to back. The first unit is the source
        // Opus frame; the remaining units are redundant copies of the same 10 ms for loss concealment (each
        // decodes to the same audio). The frame index advances by one per packet, so there is exactly one
        // source frame per packet. unit_size is derived as payload / total_units — the header's low-16 unit-size
        // field is packed differently on this v12 firmware and reads as 32 (which would imply a 96-byte payload,
        // not the observed 240). Feeding the whole payload to the decoder is the bug that made audio sound
        // "underwater": Opus/CELT sizes its per-band bit budget from the packet length, so the two extra units
        // are decoded as high-frequency coefficients, corrupting every band above ~5 kHz. Emitting just the
        // first unit restores clean audio. (Using the redundant units to conceal packet loss is a later
        // refinement.)
        int payloadLength = opened.Length - header.PayloadOffset;
        int units = Math.Max(1, header.TotalUnits);
        int unitSize = payloadLength / units;
        if (unitSize <= 0)
        {
            return;
        }

        AudioFrameReady?.Invoke(new EncodedAudioFrame(
            opened.AsMemory(header.PayloadOffset, unitSize), DateTime.UtcNow.Ticks));
    }

    /// <summary>Copy the packet, verify + decrypt it in place, and hand back the mutable buffer.</summary>
    private bool TryOpenMedia(in HalyardStreamHeader header, ReadOnlySpan<byte> packet, out byte[] opened)
    {
        opened = packet.ToArray();
        var layout = HalyardPacketLayout.Av(header.KeyPosition, header.PayloadOffset);
        if (_crypto.TryOpenPacket(opened, layout))
        {
            return true;
        }

        AuthFailures++;
        return false;
    }
}
