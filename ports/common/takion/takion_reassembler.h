/*
 * ripcord-3ds - Phase 5 Takion transport: DATA-chunk message reassembly.
 *
 * "First" vs "continuation" is not a wire bit (see takion_data_chunk.h) - a receiver decides from its
 * own state, and a continuation fragment carries no channel id at all. That second fact is why this
 * tracks a SINGLE in-flight message rather than the .NET reference's per-channel dictionary
 * (Ripcord.Protocol.Halyard.Takion.TakionMessageReassembler): DATA chunks arrive in one strict TSN
 * order, and a continuation with no channel field is only unambiguous if at most one message is being
 * fragmented at a time - which one in-flight slot models directly, and a dictionary would only add
 * capacity for a case the wire format itself cannot disambiguate.
 */
#ifndef TAKION_REASSEMBLER_H
#define TAKION_REASSEMBLER_H

#include <stddef.h>
#include <stdint.h>

#define TAKION_REASSEMBLER_MAX_MESSAGE 2048

typedef struct {
    int pending;
    unsigned channel;
    uint8_t buffer[TAKION_REASSEMBLER_MAX_MESSAGE];
    size_t length;
} takion_reassembler;

void takion_reassembler_init(takion_reassembler *r);

/*
 * Feeds a first-fragment payload for `channel`. If `ending` is set, the message is already complete:
 * returns 1 immediately and fills *out_message and *out_length (pointing at `payload` itself - no copy).
 * If not ending, buffers the payload (expecting takion_reassembler_continue() next) and returns 0.
 * Returns -1 if a message was already in flight (a first fragment should never arrive until the
 * previous one ended - a protocol violation, not a normal "incomplete" case) or if payload_length
 * exceeds TAKION_REASSEMBLER_MAX_MESSAGE.
 */
int takion_reassembler_first(takion_reassembler *r, unsigned channel, const uint8_t *payload,
                             size_t payload_length, int ending,
                             const uint8_t **out_message, size_t *out_length);

/*
 * Feeds a continuation-fragment payload, appending it to the message started by the most recent
 * takion_reassembler_first() call. If `ending` is set, returns 1, fills *out_channel/out_message/
 * out_length (pointing at the reassembler's own internal buffer - valid until the next call), and
 * clears the in-flight state. If not ending, appends and returns 0. Returns -1 if no message was in
 * flight, or the accumulated length would exceed TAKION_REASSEMBLER_MAX_MESSAGE.
 */
int takion_reassembler_continue(takion_reassembler *r, const uint8_t *payload, size_t payload_length,
                                int ending, unsigned *out_channel,
                                const uint8_t **out_message, size_t *out_length);

#endif /* TAKION_REASSEMBLER_H */
