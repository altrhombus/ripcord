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
/// Per-packet crypto is keyed by the header's keystream offset and covers the whole packet (spec §5.5), so the
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

    /// <summary>
    /// Sanity cap on unit slots per frame (source + parity), from our own evidence: the console's Cauchy matrix
    /// builder forms every coefficient as the GF(2^8) inverse of <c>(m + j) XOR i</c> (spec §6.2, read off our
    /// own binary), and that lookup is an inverse only while both operands are below 256, so a frame the console
    /// can FEC-code has <c>k + m &lt;= 256</c>. The header's 11-bit total-units field could carry up to 2048
    /// <c>[C]</c>, but nothing in our captures comes near either bound: the largest frame seen is 130 units
    /// (spec §6.2). A frame over the cap is skipped; the console never checks this number.
    /// </summary>
    private const int MaxUnitsPerFrame = 256;

    private readonly IHalyardSessionCrypto _crypto = crypto;
    // Scratch for the assembled Annex-B frame. A plain array rather than List<byte> so it can be handed to
    // subscribers as a ReadOnlyMemory slice with no copy — List does not expose its backing store as Memory.
    // Reused across frames; see the lifetime contract on VideoFrameReady.
    private byte[] _assembly = new byte[64 * 1024];
    private int _assemblyLength;

    // The frame being reassembled, laid out as the console lays it out (spec §6.2): unit u at u * _codedLength
    // in _units, source units [0, _sourceCount) then parity units [_sourceCount, _sourceCount + _parityCount).
    // Units land by index, so arrival order doesn't matter and FEC sees every unit where it expects it.
    private int _frameIndex = -1;
    private long _frameTimestampTicks;
    private int _sourceCount;
    private int _parityCount; // as the header says; 0 means the frame has no parity and nothing to recover with
    private int _sourceArrived;
    private int _parityArrived;

    // Accumulated wire packet stats for the loss report (received vs lost units across frames). Read+reset
    // via TakePacketStats from the loss-report loop, so guarded with Interlocked.
    private long _statUnitsReceived;
    private long _statUnitsLost;
    // The frame's coded unit length: what FEC codes over, shared by every unit, and the spacing of units in
    // _units. Confirmed [C]/[W]: the longest source unit rounded up to a multiple of 4, constant within a frame
    // (cap47, 5,961 frames: parity length constant per frame, never exceeded by a source unit, parity_len -
    // max(source_len) only ever 0..3; FUN_101035e0 rejects any length with (len & 3) != 0).
    private int _codedLength;
    private byte[] _units = [];
    private bool[] _arrived = [];
    private int[] _arrivedLength = []; // each unit's payload length as it arrived (or as recovered)
    private bool _frameOpen;

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

    /// <summary>Non-media packets (control, loss-report, FEC) surfaced for diagnostics.</summary>
    public event Action<HalyardStreamHeader, ReadOnlyMemory<byte>>? ControlPacketReceived;

    /// <summary>Number of media packets that failed GMAC verification (diagnostics).</summary>
    public long AuthFailures { get; private set; }

    /// <summary>
    /// Atomically read and reset the accumulated wire unit stats (received, lost) since the last call — the
    /// input to the periodic loss report that lets the console's rate controller adapt.
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
            OpenFrame(header, dataSize, payload);
        }

        if (_frameOpen)
        {
            StoreUnit(header, dataSize, payload);
        }
    }

    /// <summary>Start a new frame from whichever of its units arrives first, which fixes the coded length.
    /// Leaves <see cref="_frameOpen"/> false on impossible geometry, so the frame is skipped.</summary>
    private void OpenFrame(in HalyardStreamHeader header, int dataSize, ReadOnlySpan<byte> payload)
    {
        _frameOpen = false;

        int source = header.SourceUnits;
        int parity = header.ParityUnits;
        int units = source + parity;
        if (source <= 0 || units > MaxUnitsPerFrame)
        {
            return;
        }

        // A source unit's 2-byte size-extension, added to its own length, gives the coded length; a parity unit
        // is the coded length already (spec §6.2). So either kind of unit, arriving first, gives the same answer.
        int codedLength = dataSize;
        if (header.UnitIndex < source && payload.Length >= VideoUnitPrefixLength)
        {
            codedLength += (payload[0] << 8) | payload[1];
        }

        if (codedLength <= 0)
        {
            return;
        }

        int bytes = units * codedLength;
        if (_units.Length < bytes)
        {
            _units = new byte[bytes];
        }
        else
        {
            Array.Clear(_units, 0, bytes);
        }

        if (_arrived.Length < units)
        {
            _arrived = new bool[units];
            _arrivedLength = new int[units];
        }
        else
        {
            Array.Clear(_arrived, 0, units);
            Array.Clear(_arrivedLength, 0, units);
        }

        _sourceCount = source;
        _parityCount = parity;
        _codedLength = codedLength;
        _sourceArrived = 0;
        _parityArrived = 0;
        _frameOpen = true;
    }

    /// <summary>Copy one decrypted unit to its place in the frame, by unit index.</summary>
    private void StoreUnit(in HalyardStreamHeader header, int dataSize, ReadOnlySpan<byte> payload)
    {
        int index = header.UnitIndex;
        if (index >= _sourceCount + _parityCount || _arrived[index] || dataSize > _codedLength)
        {
            return;
        }

        payload.CopyTo(_units.AsSpan(index * _codedLength));
        _arrived[index] = true;
        _arrivedLength[index] = dataSize;
        if (index < _sourceCount)
        {
            _sourceArrived++;
        }
        else
        {
            _parityArrived++;
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
        if (!_frameOpen || _sourceCount == 0)
        {
            _frameOpen = false;
            return;
        }

        // Record wire loss for the loss report before FEC: recovery doesn't change what the network dropped.
        long expectedUnits = _sourceCount + _parityCount;
        long receivedUnits = _sourceArrived + _parityArrived;
        Interlocked.Add(ref _statUnitsReceived, receivedUnits);
        Interlocked.Add(ref _statUnitsLost, Math.Max(0, expectedUnits - receivedUnits));

        // Recover missing source units from the parity units when enough units survived.
        int unitCount = _sourceCount + _parityCount;
        if (_sourceArrived < _sourceCount &&
            _sourceArrived + _parityArrived >= _sourceCount &&
            CauchyReedSolomon.RecoverSource(
                _units.AsSpan(0, unitCount * _codedLength), _codedLength, _sourceCount, _parityCount,
                _arrived.AsSpan(0, unitCount)))
        {
            // A recovered source unit's own size-extension, now restored, gives back its length.
            for (int i = 0; i < _sourceCount; i++)
            {
                if (_arrived[i])
                {
                    continue;
                }

                int offset = i * _codedLength;
                int extension = (_units[offset] << 8) | _units[offset + 1];
                if (extension < _codedLength)
                {
                    _arrivedLength[i] = _codedLength - extension;
                    _arrived[i] = true;
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
        for (int i = 0; i < _sourceCount; i++)
        {
            if (!_arrived[i])
            {
                incomplete = true;
                continue;
            }

            int size = _arrivedLength[i];
            if (size <= VideoUnitPrefixLength)
            {
                continue;
            }

            AppendToAssembly(_units.AsSpan(i * _codedLength + VideoUnitPrefixLength, size - VideoUnitPrefixLength));
        }

        _frameOpen = false;

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
        for (int i = 0; i < _sourceCount; i++)
        {
            if (!_arrived[i] || _arrivedLength[i] <= VideoUnitPrefixLength)
            {
                continue;
            }

            ReadOnlySpan<byte> slice = _units.AsSpan(
                i * _codedLength + VideoUnitPrefixLength, _arrivedLength[i] - VideoUnitPrefixLength);

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
        // source frame per packet. unit_size is derived as payload / total_units; the header's low 16 bits
        // are not decoded (spec §6). Feeding the whole payload to the decoder is the bug that made audio sound
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
        var layout = HalyardPacketLayout.Av(header.KeystreamOffset, header.PayloadOffset);
        if (_crypto.TryOpenPacket(opened, layout))
        {
            return true;
        }

        AuthFailures++;
        return false;
    }
}
