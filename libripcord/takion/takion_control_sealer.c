#include "takion_control_sealer.h"

#include <string.h>

void takion_control_sealer_init(takion_control_sealer *sealer,
                                const uint8_t aes_key[16], const uint8_t base_iv[16])
{
    if (sealer == NULL || aes_key == NULL || base_iv == NULL)
        return;

    memset(sealer, 0, sizeof(*sealer));
    stream_packet_crypto_init(&sealer->crypto, aes_key, base_iv);
    sealer->keystream_offset = 0u;
    sealer->enabled = 1;
}

static void seal_at(takion_control_sealer *sealer, uint8_t *packet, size_t length,
                    unsigned tag_offset, unsigned keystream_offset_at)
{
    uint64_t keystream_offset;
    size_t remainder;
    size_t aligned;

    if (sealer == NULL || !sealer->enabled || packet == NULL)
        return;
    if (length < (size_t)(keystream_offset_at + 4u))
        return;

    /*
     * ONE COUNTER FOR EVERY OUTGOING SEALED PACKET, whatever its shape. See the header: control DATA,
     * SACKs and the loss report share this sequence precisely so that no position - and therefore no
     * GMAC nonce - is ever spent twice.
     */
    keystream_offset = sealer->keystream_offset;
    remainder = length % 16u;
    aligned = length + ((remainder == 0u) ? 0u : (16u - remainder));
    sealer->keystream_offset += (uint64_t)aligned;

    packet[keystream_offset_at + 0u] = (uint8_t)(keystream_offset >> 24);
    packet[keystream_offset_at + 1u] = (uint8_t)(keystream_offset >> 16);
    packet[keystream_offset_at + 2u] = (uint8_t)(keystream_offset >> 8);
    packet[keystream_offset_at + 3u] = (uint8_t)keystream_offset;

    (void)stream_packet_crypto_seal(&sealer->crypto, keystream_offset, packet, length, (int)tag_offset, 1);
}

void takion_control_sealer_seal_loss_report(takion_control_sealer *sealer, uint8_t *packet, size_t length)
{
    seal_at(sealer, packet, length,
            TAKION_LOSS_REPORT_TAG_OFFSET, TAKION_LOSS_REPORT_KEYSTREAM_OFFSET_AT);
}

/* See the header. Separate from seal_at because input differs in all three of the things seal_at
 * fixes: where the fields live, what the AAD zeroes, and whether the payload is encrypted at all. */
void takion_control_sealer_seal_input(takion_control_sealer *sealer, uint8_t *packet, size_t length,
                                      size_t payload_offset)
{
    uint64_t keystream_offset;
    size_t remainder;
    size_t aligned;

    if (sealer == NULL || !sealer->enabled || packet == NULL)
        return;
    if (length < TAKION_INPUT_TAG_OFFSET + 4u || payload_offset > length)
        return;

    keystream_offset = sealer->keystream_offset;
    remainder = length % 16u;
    aligned = length + ((remainder == 0u) ? 0u : (16u - remainder));
    sealer->keystream_offset += (uint64_t)aligned;

    packet[TAKION_INPUT_KEYSTREAM_OFFSET_AT + 0u] = (uint8_t)(keystream_offset >> 24);
    packet[TAKION_INPUT_KEYSTREAM_OFFSET_AT + 1u] = (uint8_t)(keystream_offset >> 16);
    packet[TAKION_INPUT_KEYSTREAM_OFFSET_AT + 2u] = (uint8_t)(keystream_offset >> 8);
    packet[TAKION_INPUT_KEYSTREAM_OFFSET_AT + 3u] = (uint8_t)keystream_offset;

    /* ENCRYPT, THEN SEAL. The order is the whole point - see the header. */
    stream_packet_crypto_crypt_payload(&sealer->crypto, keystream_offset,
                                       packet + payload_offset, length - payload_offset);
    (void)stream_packet_crypto_seal(&sealer->crypto, keystream_offset, packet, length,
                                    (int)TAKION_INPUT_TAG_OFFSET, 0);
}

void takion_control_sealer_seal(void *ctx, uint8_t *packet, size_t length)
{
    takion_control_sealer *sealer = (takion_control_sealer *)ctx;

    if (sealer == NULL || !sealer->enabled || packet == NULL)
        return;
    /* Too short to carry the fields this writes. Leaving it unsealed is the honest outcome - writing
     * past the end to satisfy an offset would be worse than an unauthenticated packet. */
    seal_at(sealer, packet, length, TAKION_CONTROL_TAG_OFFSET, TAKION_CONTROL_KEYSTREAM_OFFSET_AT);
}

void takion_control_sealer_reset(takion_control_sealer *sealer)
{
    if (sealer != NULL)
        memset(sealer, 0, sizeof(*sealer));
}

void takion_control_verifier_init(takion_control_verifier *verifier,
                                  const uint8_t aes_key[16], const uint8_t base_iv[16])
{
    if (verifier == NULL || aes_key == NULL || base_iv == NULL)
        return;

    memset(verifier, 0, sizeof(*verifier));
    stream_packet_crypto_init(&verifier->crypto, aes_key, base_iv);
    verifier->enabled = 1;
}

int takion_control_verifier_check(void *ctx, const uint8_t *packet, size_t length)
{
    takion_control_verifier *verifier = (takion_control_verifier *)ctx;
    uint64_t keystream_offset;
    int ok;

    if (verifier == NULL || !verifier->enabled)
        return 1; /* not armed: nothing claims to be authenticated yet */
    if (packet == NULL || length < (size_t)(TAKION_CONTROL_KEYSTREAM_OFFSET_AT + 4u)) {
        verifier->checked++;
        verifier->failed++;
        return 0;
    }

    /* The sender's position, as the sender states it. See the header: the tag is what makes this safe. */
    keystream_offset = ((uint64_t)packet[TAKION_CONTROL_KEYSTREAM_OFFSET_AT + 0u] << 24)
            | ((uint64_t)packet[TAKION_CONTROL_KEYSTREAM_OFFSET_AT + 1u] << 16)
            | ((uint64_t)packet[TAKION_CONTROL_KEYSTREAM_OFFSET_AT + 2u] << 8)
            | (uint64_t)packet[TAKION_CONTROL_KEYSTREAM_OFFSET_AT + 3u];

    ok = stream_packet_crypto_verify(&verifier->crypto, keystream_offset, packet, length,
                                     (int)TAKION_CONTROL_TAG_OFFSET, 1);
    verifier->checked++;
    if (!ok)
        verifier->failed++;
    return ok;
}

void takion_control_verifier_reset(takion_control_verifier *verifier)
{
    if (verifier != NULL)
        memset(verifier, 0, sizeof(*verifier));
}

size_t takion_loss_report_build(unsigned long received, unsigned long lost,
                               uint8_t *buf, size_t buf_size)
{
    unsigned r;
    unsigned l;

    if (buf == NULL || buf_size < TAKION_LOSS_REPORT_SIZE)
        return 0u;

    /* Saturate. See the header: a u16 cannot carry more, and an interval that busy is a reporting
     * problem rather than a reason to report a small number. */
    r = (received > 65535ul) ? 65535u : (unsigned)received;
    l = (lost > 65535ul) ? 65535u : (unsigned)lost;

    memset(buf, 0, TAKION_LOSS_REPORT_SIZE);
    buf[0] = 0x05u;                       /* base type 5                                    */
    /* buf[1..2] are zero in every observed packet - left as the memset above made them.    */
    buf[3] = (uint8_t)(r >> 8);
    buf[4] = (uint8_t)r;
    buf[5] = (uint8_t)(l >> 8);
    buf[6] = (uint8_t)l;
    /* buf[7..10] GMAC and buf[11..14] keystream offset are the sealer's to write. */
    return TAKION_LOSS_REPORT_SIZE;
}
