#include "takion_reassembler.h"

#include <string.h>

void takion_reassembler_init(takion_reassembler *r)
{
    memset(r, 0, sizeof(*r));
}

int takion_reassembler_first(takion_reassembler *r, unsigned channel, const uint8_t *payload,
                             size_t payload_length, int ending,
                             const uint8_t **out_message, size_t *out_length)
{
    if (r->pending)
        return -1; /* a first fragment arrived before the previous message ended */
    if (payload_length > TAKION_REASSEMBLER_MAX_MESSAGE)
        return -1;

    if (ending) {
        /*
         * COPY, even though this message is complete and the payload is right there.
         *
         * `payload` points into the caller's receive buffer, and in takion_channel_poll that buffer is a
         * LOCAL, gone when poll returns. The multi-chunk path below returns r->buffer, which lives as
         * long as the channel; the copy gives a single-chunk message the same lifetime.
         *
         * Seen on the PS3: a 203-byte SESSION_REPLY verified its HMAC and then failed an on-curve check
         * (INVALID_KEY) a few calls later, because mbedtls's own frames had overwritten the point.
         */
        memcpy(r->buffer, payload, payload_length);
        r->length = payload_length;
        r->channel = channel;
        *out_message = r->buffer;
        *out_length = payload_length;
        return 1;
    }

    memcpy(r->buffer, payload, payload_length);
    r->length = payload_length;
    r->channel = channel;
    r->pending = 1;
    return 0;
}

int takion_reassembler_continue(takion_reassembler *r, const uint8_t *payload, size_t payload_length,
                                int ending, unsigned *out_channel,
                                const uint8_t **out_message, size_t *out_length)
{
    if (!r->pending)
        return -1;
    if (r->length + payload_length > TAKION_REASSEMBLER_MAX_MESSAGE) {
        r->pending = 0; /* the in-flight message is unrecoverable either way - don't wedge the slot */
        return -1;
    }

    memcpy(r->buffer + r->length, payload, payload_length);
    r->length += payload_length;

    if (!ending)
        return 0;

    *out_channel = r->channel;
    *out_message = r->buffer;
    *out_length = r->length;
    r->pending = 0;
    return 1;
}
