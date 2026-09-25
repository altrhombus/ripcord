/*
 * libripcord fuzzing - the two control planes: the binary /sess/ctrl channel over TCP, and the
 * protobuf-shaped Takion control messages.
 *
 * The /sess/ctrl parser is fed as a byte stream, the way TCP delivers it: it reports how much it
 * consumed, and whatever is left is offered to it again. The Takion control parsers each get the whole
 * input, because on the wire each of them is handed one reassembled message.
 */
#include "../../session/halyard_ctrl_message.h"
#include "../../takion/takion_control_proto.h"
#include "fuzz_input.h"

#include <stddef.h>
#include <stdint.h>

int LLVMFuzzerTestOneInput(const uint8_t *data, size_t size);

static void feed_ctrl_stream(const uint8_t *data, size_t size)
{
    while (size > 0) {
        unsigned type;
        const uint8_t *payload = NULL;
        size_t payload_length = 0;
        size_t consumed = halyard_ctrl_message_parse(data, size, &type, &payload, &payload_length);

        if (consumed == 0 || consumed > size)
            break;
        rc_fuzz_touch(payload, payload_length);
        data += consumed;
        size -= consumed;
    }
}

static void feed_takion_control(const uint8_t *data, size_t size)
{
    uint32_t type;
    uint32_t version;
    takion_session_reply reply;
    takion_stream_info info;
    const char *reason = NULL;
    size_t reason_length = 0;

    (void)takion_control_validate(data, size);
    (void)takion_control_peek_type(data, size, &type);
    (void)takion_control_parse_session_reply(data, size, &reply);
    if (takion_control_parse_disconnect(data, size, &reason, &reason_length))
        rc_fuzz_touch((const uint8_t *)reason, reason_length);
    if (takion_control_parse_stream_info(data, size, &info))
        rc_fuzz_touch(info.video_header, info.video_header_length);
    (void)takion_control_parse_protocol_version_ack(data, size, &version);
}

int LLVMFuzzerTestOneInput(const uint8_t *data, size_t size)
{
    feed_ctrl_stream(data, size);
    feed_takion_control(data, size);
    return 0;
}
