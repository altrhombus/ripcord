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

/*
 * CONGESTION FEEDBACK, which is a different packet at different offsets and MUST use this sealer.
 *
 * Not a convenience: the outgoing key position is a single advancing sequence shared by control DATA,
 * SACKs and congestion packets alike, so a congestion path with a counter of its own would repeat a
 * position that control had already spent - and a repeated position is a repeated GMAC nonce under one
 * key. That is a key-recovery bug, not an inefficiency, and it is the reason this lives on the sealer
 * rather than beside the code that builds the packet.
 *
 * The offsets differ from control's and from A/V's, and all three are [V] against captures on the .NET
 * side: control tag@5 key_pos@9 (2528 packets), congestion tag@7 key_pos@11 (474 packets), A/V tag@10
 * key_pos@14. The AAD rule here matches CONTROL - both the tag and the key-position field are zeroed -
 * which is not what A/V does.
 */
#define TAKION_CONGESTION_TAG_OFFSET    7u
#define TAKION_CONGESTION_KEYPOS_OFFSET 11u

#define TAKION_INPUT_KEYPOS_OFFSET      4u
#define TAKION_INPUT_TAG_OFFSET         8u

void takion_control_sealer_seal_congestion(takion_control_sealer *sealer,
                                           uint8_t *packet, size_t length);

/*
 * CONTROLLER INPUT, which is a third shape again and MUST use this sealer for the same reason
 * congestion does: one advancing key position for every outgoing sealed packet, so no GMAC nonce is
 * ever spent twice under one key.
 *
 * Its offsets are key position at 4 and tag at 8 - both different from control's and congestion's -
 * and its AAD rule matches A/V rather than control: only the TAG is zeroed, not the key-position
 * field. Three shapes, three rules, and nothing about any of them is inferable from the others.
 *
 * IT ALSO ENCRYPTS, which the other two do not. Spec 6.3 is explicit that input payloads are AES-CTR
 * encrypted, and the .NET side and ripcord-3ds both learned it the same expensive way: a plaintext
 * payload is faithfully DECRYPTED by the console into noise, so a button press arrives as something
 * else entirely, at an unpredictable moment, having passed authentication the whole way. Encrypting
 * here rather than at the call site means the tag is computed over the ciphertext that actually goes on
 * the wire - sealing first would authenticate a payload nobody will ever see.
 *
 * `payload_offset` is where the encrypted part begins (HALYARD_INPUT_HEADER_LENGTH for both packet
 * types).
 */
void takion_control_sealer_seal_input(takion_control_sealer *sealer,
                                      uint8_t *packet, size_t length, size_t payload_offset);

/* Wipes the key material. */
/*
 * Builds one congestion-feedback packet: 15 bytes, base type 5, carrying the A/V units received and lost
 * since the last one. Returns the length written, or 0 if the buffer is too small.
 *
 * LAYOUT AND ITS PROVENANCE, which differ in confidence and should not be blurred. Derived on the .NET
 * side from 474 congestion packets in one capture: type at 0; bytes 1-2 zero in every packet **[V]**;
 * received at 3 as a big-endian u16 **[V]**, observed between 2 and 351 per interval; lost at 5, also
 * u16 big-endian - its POSITION is confirmed but its MEANING is **[X]**, because that session had no
 * loss and every one of those 474 packets carried zero there. GMAC at 7 and key position at 11 are
 * written by the sealer, not here.
 *
 * VERSION CAVEAT, recorded because a future firmware will make it matter: this 15-byte form is what that
 * capture shows. An older capture shows a 23-byte variant of the same base type with a sequence at 1, a
 * 90 kHz timestamp at 3, GMAC at 15 and key position at 19. The size is protocol-version-specific rather
 * than universal. This port only negotiates the version the 15-byte capture used; if a console ever
 * rejects these, that variant is the first thing to look at.
 *
 * The counts SATURATE rather than wrap. A u16 cannot carry more than 65535 and an interval that busy is
 * a reporting problem, not a reason to tell the console a small number.
 */
#define TAKION_CONGESTION_PACKET_SIZE 15u

size_t takion_congestion_build(unsigned long received, unsigned long lost,
                               uint8_t *buf, size_t buf_size);

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
