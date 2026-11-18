/*
 * GMAC sealing for outgoing Takion CONTROL packets - DATA chunks and SACKs alike.
 *
 * takion_reliable_channel takes a takion_seal_fn rather than doing this itself: that layer is pure
 * transport and knows nothing about the stream cipher. Every port needs the identical callback, so it
 * lives here once.
 *
 * WHAT IT WRITES, and both halves are confirmed against captures on the .NET side
 * (src/Ripcord.Protocol.Halyard.Common/Crypto/V1/HalyardV1SessionCrypto.cs states the evidence in full):
 *
 *   - the 32-bit keystream offset at offset 9, and the 32-bit GMAC tag at offset 5. **[V]** across 2528
 *     type-0 packets in one capture.
 *   - the AAD zeroes BOTH the tag and the keystream-offset field. **[V]** by recomputing 727 authenticated
 *     packets offline against dumped keys: zeroing tag+keystream_offset reproduces the on-wire tag 727/727, while
 *     zeroing the tag alone matches only the two packets whose keystream_offset is 0 - where the two rules are
 *     byte-identical and so prove nothing.
 *
 * A/V packets zero only the tag and sit at different offsets; this is the control rule and is not it.
 *
 * SACKS ARE SEALED TOO (see takion_channel_enable_sealing): the channel routes every outgoing control
 * packet through this one callback.
 */
#ifndef TAKION_CONTROL_SEALER_H
#define TAKION_CONTROL_SEALER_H

#include "../stream/stream_packet_crypto.h"

#include <stddef.h>
#include <stdint.h>

/* Where the two sealed fields live in a control packet's header. */
#define TAKION_CONTROL_TAG_OFFSET    5u
#define TAKION_CONTROL_KEYSTREAM_OFFSET_AT 9u

typedef struct {
    stream_packet_crypto crypto;

    /*
     * The send-direction keystream offset, which is a BYTE COUNT and not a packet count: each packet
     * advances it by its own length rounded up to the cipher's 16-byte block. Control DATA and SACKs
     * share this one sequence, which is exactly why the counter belongs to the sealer and not to
     * whatever is building a particular message.
     */
    uint64_t keystream_offset;
    int enabled;
} takion_control_sealer;

/* Arms the sealer with the send-direction stream key and base IV. Nothing is sealed until this is called,
 * and that is correct: packets sent before key agreement are legitimately unauthenticated. */
void takion_control_sealer_init(takion_control_sealer *sealer,
                                const uint8_t aes_key[16], const uint8_t base_iv[16]);

/* Matches takion_seal_fn. Pass this and the sealer to takion_channel_enable_sealing. */
void takion_control_sealer_seal(void *ctx, uint8_t *packet, size_t length);

/*
 * LOSS REPORTS, which is a different packet at different offsets and MUST use this sealer.
 *
 * The outgoing keystream offset is a single advancing sequence shared by control DATA, SACKs and loss
 * reports alike. A loss-report path with a counter of its own would repeat a position control had
 * already spent - a repeated GMAC nonce under one key, which is a key-recovery bug.
 *
 * The offsets differ from control's and from A/V's, and all three are [V] against captures on the .NET
 * side: control tag@5 keystream_offset@9 (2528 packets), loss-report tag@7 keystream_offset@11 (474 packets), A/V tag@10
 * keystream_offset@14. The AAD rule here matches CONTROL - both the tag and the keystream-offset field are zeroed -
 * which is not what A/V does.
 */
#define TAKION_LOSS_REPORT_TAG_OFFSET    7u
#define TAKION_LOSS_REPORT_KEYSTREAM_OFFSET_AT 11u

#define TAKION_INPUT_KEYSTREAM_OFFSET_AT      4u
#define TAKION_INPUT_TAG_OFFSET         8u

void takion_control_sealer_seal_loss_report(takion_control_sealer *sealer,
                                           uint8_t *packet, size_t length);

/*
 * CONTROLLER INPUT, which is a third shape again and MUST use this sealer for the same reason
 * a loss report does: one advancing keystream offset for every outgoing sealed packet, so no GMAC nonce is
 * ever spent twice under one key.
 *
 * Its offsets are keystream offset at 4 and tag at 8 - both different from control's and the loss report's -
 * and its AAD rule matches A/V rather than control: only the TAG is zeroed, not the keystream-offset
 * field.
 *
 * IT ALSO ENCRYPTS, which the other two do not. Spec 6.3 is explicit that input payloads are AES-CTR
 * encrypted: the console decrypts a plaintext payload into noise, which still passes authentication.
 * The payload is encrypted first, so the tag is computed over the ciphertext that goes on the wire.
 *
 * `payload_offset` is where the encrypted part begins (HALYARD_INPUT_HEADER_LENGTH for both packet
 * types).
 */
void takion_control_sealer_seal_input(takion_control_sealer *sealer,
                                      uint8_t *packet, size_t length, size_t payload_offset);

/* Wipes the key material. */
/*
 * Builds one loss report: 15 bytes, base type 5, carrying the A/V units received and lost
 * since the last one. Returns the length written, or 0 if the buffer is too small.
 *
 * LAYOUT, and the confidence of each part. Derived on the .NET
 * side from 474 loss reports in one capture: type at 0; bytes 1-2 zero in every packet **[V]**;
 * received at 3 as a big-endian u16 **[V]**, observed between 2 and 351 per interval; lost at 5, also
 * u16 big-endian - its POSITION is confirmed but its MEANING is **[X]**, because that session had no
 * loss and every one of those 474 packets carried zero there. GMAC at 7 and keystream offset at 11 are
 * written by the sealer, not here.
 *
 * VERSION CAVEAT: this 15-byte form is what that
 * capture shows. An older capture shows a 23-byte variant of the same base type with a sequence at 1, a
 * 90 kHz timestamp at 3, GMAC at 15 and keystream offset at 19. The size is protocol-version-specific rather
 * than universal. This port only negotiates the version the 15-byte capture used; if a console ever
 * rejects these, that variant is the first thing to look at.
 *
 * The counts SATURATE at 65535 rather than wrap.
 */
#define TAKION_LOSS_REPORT_SIZE 15u

size_t takion_loss_report_build(unsigned long received, unsigned long lost,
                               uint8_t *buf, size_t buf_size);

void takion_control_sealer_reset(takion_control_sealer *sealer);

/*
 * THE RECEIVE HALF.
 *
 * GMAC authenticates without encrypting, so without verification a control message from anyone at all
 * parses perfectly well.
 *
 * The keystream offset is taken FROM THE PACKET rather than from a local counter: the receiver does not
 * predict where the sender is in its own byte sequence, and a forged position produces a tag that does
 * not verify.
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
