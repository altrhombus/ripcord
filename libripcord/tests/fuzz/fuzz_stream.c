/*
 * libripcord fuzzing - the A/V path: stream headers, then the demuxer's frame assembly and FEC
 * recovery.
 *
 * The demuxer runs with the passthrough crypto seam, which accepts every packet. That is deliberately
 * stronger than the wire: a real packet has to pass GMAC before anything beyond its header is trusted,
 * so this also reaches the code an authenticated but hostile or buggy console could drive, including
 * the frame geometry a header declares and the FEC reconstruction it triggers.
 *
 * The first record is the video header STREAM_INFO would supply (SPS/PPS). Every later record is one
 * datagram.
 */
#include "../../stream/stream_header.h"
#include "../../stream/stream_demux.h"
#include "fuzz_input.h"

#include <stddef.h>
#include <stdint.h>

int LLVMFuzzerTestOneInput(const uint8_t *data, size_t size);

/* About 2 MB of slot buffer, so static rather than on the stack. */
static stream_demux s_demux;

static void on_video(void *userdata, const uint8_t *frame, size_t length, int is_keyframe)
{
    (void)userdata;
    (void)is_keyframe;
    rc_fuzz_touch(frame, length);
}

static void on_audio(void *userdata, const uint8_t *frame, size_t length)
{
    (void)userdata;
    rc_fuzz_touch(frame, length);
}

static void on_loss(void *userdata, int first_frame_index, int last_frame_index)
{
    (void)userdata;
    (void)first_frame_index;
    (void)last_frame_index;
}

static void on_control(void *userdata, const stream_header *header, const uint8_t *packet, size_t length)
{
    (void)userdata;
    (void)header;
    rc_fuzz_touch(packet, length);
}

int LLVMFuzzerTestOneInput(const uint8_t *data, size_t size)
{
    rc_fuzz_input in;
    const uint8_t *record;
    size_t record_length;
    stream_demux_sink sink;

    sink.userdata = NULL;
    sink.video_frame_ready = on_video;
    sink.audio_frame_ready = on_audio;
    sink.video_loss_detected = on_loss;
    sink.control_packet_received = on_control;
    stream_demux_init(&s_demux, stream_demux_passthrough_crypto(), sink);

    rc_fuzz_input_init(&in, data, size);
    if (rc_fuzz_next(&in, &record, &record_length))
        stream_demux_set_video_header(&s_demux, record, record_length);

    while (rc_fuzz_next(&in, &record, &record_length)) {
        stream_header header;

        (void)stream_header_parse(record, record_length, &header);
        stream_demux_ingest(&s_demux, record, record_length);
    }
    return 0;
}
