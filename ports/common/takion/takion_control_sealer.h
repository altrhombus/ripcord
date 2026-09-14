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

/*
 * THE RECEIVE HALF.
 *
 * Sealing without verifying is half a mechanism: GMAC authenticates without encrypting, so a control
 * message from anyone at all parses perfectly well, and a client that only seals is protected in one
 * direction while accepting whatever arrives in the other. That is the gap this closes.
 *
 * The key position is taken FROM THE PACKET rather than from a local counter. A receiver has no business
 * predicting where the sender is in its own byte sequence - it is told, and the tag is what makes being
 * told safe. A forged position simply produces a tag that does not verify.
 */
typedef struct {
    stream_packet_crypto crypto;
    int enabled;

    /*
     * Counted rather than only rejected, because the useful question on a new platform is not "did one
     * fail" but "what proportion". A handful of failures among thousands is a different finding from
     * everything failing, and only the second means the key schedule is wrong.
     */
    unsigned long checked;
    unsigned long failed;
} takion_control_verifier;

/* Arms the verifier with the RECEIVE-direction stream key and base IV. */
void takion_control_verifier_init(takion_control_verifier *verifier,
                                  const uint8_t aes_key[16], const uint8_t base_iv[16]);

/* Matches takion_verify_fn. Returns 1 if the packet's tag is good, 0 if it is not. A packet too short to
 * carry the fields is reported as failing - it cannot be authenticated, and treating "unauthenticatable"
 * as "fine" is how the check gets bypassed. */
int takion_control_verifier_check(void *ctx, const uint8_t *packet, size_t length);

void takion_control_verifier_reset(takion_control_verifier *verifier);

#endif /* TAKION_CONTROL_SEALER_H */
