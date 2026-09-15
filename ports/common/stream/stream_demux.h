/*
 * ripcord-3ds - splits the multiplexed A/V stream into video/audio/other by packet type, authenticates +
 * decrypts each media packet through the crypto seam below, and reassembles video fragments into whole
 * Annex-B frames (recovering lost source units via FEC when enough units survive). Ported from
 * HalyardStreamDemuxer.cs - see that file for the fuller rationale behind each piece; comments here cover
 * only where this port's fixed-buffer C differs from the reference's dynamically-growing C# arrays.
 *
 * Video reassembly keys on the frame index: units accumulate in a slot buffer (one stride-sized slot per
 * unit index) while the frame index is constant, and the previous frame is flushed when a new index
 * arrives (spec sec6.1). Audio packs one Opus source frame plus redundant loss-concealment copies of the
 * same 10ms per packet; only the first (source) unit is ever emitted - see stream_demux.c's ingest_audio.
 *
 * FEC_MAX_TOTAL_UNITS bounds units-per-frame (shared with fec_reed_solomon.h - a frame's FEC group can
 * never exceed what that module can recover anyway). STREAM_DEMUX_MAX_UNIT_STRIDE is this port's own cap
 * on a single unit's coded length: unlike the reference's List<byte>/Array.Resize, nothing in this port
 * allocates, so a frame whose declared geometry would exceed either cap is skipped defensively (see
 * allocate_frame in the .c) rather than risking a buffer overflow from console-controlled wire fields.
 * Both caps are sized generously above what a 3DS-appropriate stream resolution/bitrate needs - see the
 * .c file's allocate_frame for the exact reasoning - not derived from any protocol constant.
 */
#ifndef STREAM_DEMUX_H
#define STREAM_DEMUX_H

#include "stream_header.h"
#include "stream_packet_crypto.h"
#include "fec_reed_solomon.h"

#include <stdint.h>
#include <stddef.h>

#define STREAM_DEMUX_VIDEO_UNIT_PREFIX_LENGTH 2
#define STREAM_DEMUX_OPUS_CODEC 5

#define STREAM_DEMUX_MAX_UNIT_STRIDE 4096

/*
 * UNITS PER FRAME, WHICH IS NOT THE SAME NUMBER AS THE FEC GROUP SIZE, and conflating the two is what
 * kept 1080p from working at all.
 *
 * FEC_MAX_TOTAL_UNITS bounds what fec_reed_solomon.c can RECOVER - k+m against its fixed working
 * buffers. The slots a frame occupies is a different quantity that merely happens to have been smaller
 * on every stream this code had seen: 640x360 needs a handful and 1280x720 measured about 21, both
 * comfortably inside 64. A 1920x1080 frame needs well over a hundred, and the frame was then abandoned
 * before assembly began - silently, which is why 103,371 video packets became 32 frames and nobody
 * could see where they went.
 *
 * 512 is HalyardStreamDemuxer's own cap, which sizes its slots dynamically; this is the fixed-buffer
 * equivalent of the same number, and it costs a 2 MB slot buffer.
 *
 * NOT OVERRIDABLE, AND THAT IS THE POINT. b168 made it a per-port -D and the console hung before it
 * reached the network: this port's core-test objects are compiled from their own flag list rather than
 * from CFLAGS, so stream_demux.c saw 512 while the test translation units saw 64. One struct, two sizes,
 * one binary - writes past the end of an object the rest of the program believed was sixteen times
 * larger. A type whose size depends on a command-line define is a trap that is set once and sprung
 * somewhere else, so there is one number here and no way to vary it.
 */
#define STREAM_DEMUX_MAX_UNITS_PER_FRAME 512
#define STREAM_DEMUX_VIDEO_HEADER_CAPACITY 512
#define STREAM_DEMUX_ASSEMBLY_CAPACITY \
    (STREAM_DEMUX_MAX_UNITS_PER_FRAME * STREAM_DEMUX_MAX_UNIT_STRIDE \
     + STREAM_DEMUX_VIDEO_HEADER_CAPACITY)

/*
 * The crypto seam (same shape as IHalyardSessionCrypto's seam-and-stub pattern - see CLAUDE.md): verify +
 * decrypt one media packet in place. Must verify the packet's GMAC tag (AV: offset
 * STREAM_HEADER_TAG_OFFSET, key position NOT zeroed - confirmed byte-for-byte against a live console
 * capture, see HalyardPacketCrypto.ComputeTag) and, only on success, CTR-decrypt
 * packet[payload_offset..packet_length). Returns 1 on success (packet is now plaintext from
 * payload_offset on), 0 to drop it.
 */
typedef int (*stream_demux_open_packet_fn)(void *crypto_ctx, uint8_t *packet, size_t packet_length,
    uint32_t key_position, int payload_offset);

typedef struct {
    stream_demux_open_packet_fn open_packet;
    void *ctx;
} stream_demux_crypto;

/* Identity stub - matches PassthroughHalyardSessionCrypto: always succeeds, never touches the packet. Lets
 * the rest of this pipeline (framing, demux, FEC) run against captures/synthetic data before real stream
 * crypto exists (ECDH/session establishment is a separate, not-yet-started backlog item). */
stream_demux_crypto stream_demux_passthrough_crypto(void);

/* Wires an already-established stream_packet_crypto (see stream_packet_crypto.h) as the real crypto
 * implementation. `crypto` must outlive the stream_demux using it. */
stream_demux_crypto stream_demux_packet_crypto(stream_packet_crypto *crypto);

/* Callback sink. Any callback may be NULL to ignore that event. All pointers passed to a callback are
 * valid only for the duration of the call - a callback that needs the data afterwards must copy it
 * (matches VideoFrameReady's lifetime contract in the .NET reference). */
typedef struct {
    void *userdata;
    void (*video_frame_ready)(void *userdata, const uint8_t *data, size_t length, int is_keyframe);
    void (*audio_frame_ready)(void *userdata, const uint8_t *data, size_t length);
    /* Inclusive range of affected frame indices (the console's 16-bit values) - either a frame flushed
     * incomplete or the frame index jumped forward. A subscriber reports these to the console
     * (CORRUPT_FRAME) and requests a fresh IDR. */
    void (*video_loss_detected)(void *userdata, int first_frame_index, int last_frame_index);
    void (*control_packet_received)(void *userdata, const stream_header *header, const uint8_t *data,
        size_t length);
} stream_demux_sink;

/*
 * ~513 KB - by far the largest object in this port (whole-frame reassembly buffers x FEC unit slots).
 * NEVER DECLARE ONE AS A LOCAL, and do not casually put one per-session on a 3DS either: a .3dsx main
 * thread gets a 32 KB stack, so a local is an instant data abort in the prologue with nothing logged.
 * File scope, `static`, or the heap. See takion_reliable_channel.h - the same mistake, 10x smaller, is
 * what actually crashed on hardware; the 3DS build's -Wframe-larger-than=8192 now catches both.
 */
typedef struct {
    stream_demux_crypto crypto;
    stream_demux_sink sink;

    uint8_t video_header[STREAM_DEMUX_VIDEO_HEADER_CAPACITY]; /* SPS/PPS from STREAM_INFO */
    size_t video_header_length;
    int video_is_hevc;

    uint8_t assembly[STREAM_DEMUX_ASSEMBLY_CAPACITY];
    size_t assembly_length;
    int assembly_overflowed;

    int frame_index; /* -1 = no frame in progress */
    long frame_timestamp_ms;
    int frame_allocated;
    int source_expected;
    int fec_expected;
    int fec_actual;
    int source_received;
    int fec_received;
    int unit_padded_size;
    int unit_stride;
    uint8_t slot_buf[STREAM_DEMUX_MAX_UNITS_PER_FRAME * STREAM_DEMUX_MAX_UNIT_STRIDE];
    uint8_t slot_present[STREAM_DEMUX_MAX_UNITS_PER_FRAME];
    int slot_data_size[STREAM_DEMUX_MAX_UNITS_PER_FRAME];

    long stat_units_received;
    long stat_units_lost;
    /* Frames refused because they wanted more slots than STREAM_DEMUX_MAX_UNITS_PER_FRAME. Counted
     * because this used to be a bare `return` - see that constant's note. */
    long stat_frames_too_many_units;
    long auth_failures;
} stream_demux;

/* Zeroes *demux and installs the crypto seam + callback sink. */
/*
 * sizeof(stream_demux) as the LIBRARY was compiled, so a caller can check it against its own. There is
 * no way to catch a struct that two translation units disagree about at compile time in C, and b168
 * showed what it costs at run time: the console hung before it reached the network, with nothing in the
 * log to say why. One comparison at startup turns that into a message.
 */
size_t stream_demux_struct_size(void);

void stream_demux_init(stream_demux *demux, stream_demux_crypto crypto, stream_demux_sink sink);

/* Supplies the SPS/PPS parameter sets parsed from the console's STREAM_INFO, prepended ahead of every
 * emitted IDR (the sets arrive out-of-band, and a hardware decoder needs them to (re)initialise). Also
 * classifies the stream as HEVC vs H.264 from the sets themselves (see stream_demux.c's
 * looks_like_hevc_parameter_sets for why slice headers are not safe to classify this way). Truncates
 * silently to STREAM_DEMUX_VIDEO_HEADER_CAPACITY if longer (real parameter sets are tens of bytes).
 */
void stream_demux_set_video_header(stream_demux *demux, const uint8_t *header, size_t length);

/* Feed one whole UDP payload from the stream port. */
void stream_demux_ingest(stream_demux *demux, const uint8_t *packet, size_t packet_length);

/* Reads and resets the accumulated wire unit stats (received, lost) since the last call - the input to the
 * periodic congestion-feedback packet that lets the console's rate controller adapt. Not thread-safe (no
 * Interlocked here, unlike the .NET reference): callers on this single-threaded target must call this from
 * the same thread that calls stream_demux_ingest, or add their own locking. */
void stream_demux_take_packet_stats(stream_demux *demux, long *out_received, long *out_lost);

#endif /* STREAM_DEMUX_H */
