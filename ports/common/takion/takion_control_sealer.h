/*
 * GMAC sealing for outgoing Takion CONTROL packets - DATA chunks and SACKs alike.
 *
 * takion_reliable_channel takes a takion_seal_fn rather than doing this itself, deliberately: that layer
 * is pure transport and knows nothing about the stream cipher. But every port that reaches this point
 * needs the identical callback, and ports/ripcord-3ds already grew one. Rather than have the PS3 grow a
 * second copy of a key-position counter and a pair of magic offsets, it lives here once.
 *
 * WHAT IT WRITES, and both halves are confirmed against captures on the .NET side
 * (src/Ripcord.Protocol.Halyard.Common/Crypto/V1/HalyardV1SessionCrypto.cs states the evidence in full):
 *
 *   - the 32-bit key position at offset 9, and the 32-bit GMAC tag at offset 5. **[V]** across 2528
 *     type-0 packets in one capture.
 *   - the AAD zeroes BOTH the tag and the key-position field. **[V]** by recomputing 727 authenticated
 *     packets offline against dumped keys: zeroing tag+key_pos reproduces the on-wire tag 727/727, while
 *     zeroing the tag alone matches only the two packets whose key_pos is 0 - where the two rules are
 *     byte-identical and so prove nothing.
 *
 * A/V packets zero only the tag and sit at different offsets; this is the control rule and is not it.
 *
 * SACKS ARE SEALED TOO. Forgetting that is a specific, documented failure - see
 * takion_channel_enable_sealing. Because the channel routes every outgoing control packet through one
 * callback, this gets it right by construction rather than by remembering.
 */
#ifndef TAKION_CONTROL_SEALER_H
#define TAKION_CONTROL_SEALER_H

#include "../stream/stream_packet_crypto.h"

#include <stddef.h>
#include <stdint.h>

/* Where the two sealed fields live in a control packet's header. */
#define TAKION_CONTROL_TAG_OFFSET    5u
#define TAKION_CONTROL_KEYPOS_OFFSET 9u

typedef struct {
    stream_packet_crypto crypto;

    /*
     * The send-direction key position, which is a BYTE COUNT and not a packet count: each packet
     * advances it by its own length rounded up to the cipher's 16-byte block. Control DATA and SACKs
     * share this one sequence, which is exactly why the counter belongs to the sealer and not to
     * whatever is building a particular message.
     */
    uint64_t key_pos;
    int enabled;
} takion_control_sealer;

/* Arms the sealer with the send-direction stream key and base IV. Nothing is sealed until this is called,
 * and that is correct: packets sent before key agreement are legitimately unauthenticated. */
void takion_control_sealer_init(takion_control_sealer *sealer,
                                const uint8_t aes_key[16], const uint8_t base_iv[16]);

/* Matches takion_seal_fn. Pass this and the sealer to takion_channel_enable_sealing. */
void takion_control_sealer_seal(void *ctx, uint8_t *packet, size_t length);

/* Wipes the key material. */
void takion_control_sealer_reset(takion_control_sealer *sealer);

#endif /* TAKION_CONTROL_SEALER_H */
