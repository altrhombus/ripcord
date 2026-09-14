#include "takion_control_sealer.h"

#include <string.h>

void takion_control_sealer_init(takion_control_sealer *sealer,
                                const uint8_t aes_key[16], const uint8_t base_iv[16])
{
    if (sealer == NULL || aes_key == NULL || base_iv == NULL)
        return;

    memset(sealer, 0, sizeof(*sealer));
    stream_packet_crypto_init(&sealer->crypto, aes_key, base_iv);
    sealer->key_pos = 0u;
    sealer->enabled = 1;
}

static void seal_at(takion_control_sealer *sealer, uint8_t *packet, size_t length,
                    unsigned tag_offset, unsigned keypos_offset)
{
    uint64_t key_pos;
    size_t remainder;
    size_t aligned;

    if (sealer == NULL || !sealer->enabled || packet == NULL)
        return;
    if (length < (size_t)(keypos_offset + 4u))
        return;

    /*
     * ONE COUNTER FOR EVERY OUTGOING SEALED PACKET, whatever its shape. See the header: control DATA,
     * SACKs and congestion feedback share this sequence precisely so that no position - and therefore no
     * GMAC nonce - is ever spent twice.
     */
    key_pos = sealer->key_pos;
    remainder = length % 16u;
    aligned = length + ((remainder == 0u) ? 0u : (16u - remainder));
    sealer->key_pos += (uint64_t)aligned;

    packet[keypos_offset + 0u] = (uint8_t)(key_pos >> 24);
    packet[keypos_offset + 1u] = (uint8_t)(key_pos >> 16);
    packet[keypos_offset + 2u] = (uint8_t)(key_pos >> 8);
    packet[keypos_offset + 3u] = (uint8_t)key_pos;

    (void)stream_packet_crypto_seal(&sealer->crypto, key_pos, packet, length, (int)tag_offset, 1);
}

void takion_control_sealer_seal_congestion(takion_control_sealer *sealer, uint8_t *packet, size_t length)
{
    seal_at(sealer, packet, length,
            TAKION_CONGESTION_TAG_OFFSET, TAKION_CONGESTION_KEYPOS_OFFSET);
}

void takion_control_sealer_seal(void *ctx, uint8_t *packet, size_t length)
{
    takion_control_sealer *sealer = (takion_control_sealer *)ctx;

    if (sealer == NULL || !sealer->enabled || packet == NULL)
        return;
    /* Too short to carry the fields this writes. Leaving it unsealed is the honest outcome - writing
     * past the end to satisfy an offset would be worse than an unauthenticated packet. */
    seal_at(sealer, packet, length, TAKION_CONTROL_TAG_OFFSET, TAKION_CONTROL_KEYPOS_OFFSET);
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
    uint64_t key_pos;
    int ok;

    if (verifier == NULL || !verifier->enabled)
        return 1; /* not armed: nothing claims to be authenticated yet */
    if (packet == NULL || length < (size_t)(TAKION_CONTROL_KEYPOS_OFFSET + 4u)) {
        verifier->checked++;
        verifier->failed++;
        return 0;
    }

    /* The sender's position, as the sender states it. See the header: the tag is what makes this safe. */
    key_pos = ((uint64_t)packet[TAKION_CONTROL_KEYPOS_OFFSET + 0u] << 24)
            | ((uint64_t)packet[TAKION_CONTROL_KEYPOS_OFFSET + 1u] << 16)
            | ((uint64_t)packet[TAKION_CONTROL_KEYPOS_OFFSET + 2u] << 8)
            | (uint64_t)packet[TAKION_CONTROL_KEYPOS_OFFSET + 3u];

    ok = stream_packet_crypto_verify(&verifier->crypto, key_pos, packet, length,
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

size_t takion_congestion_build(unsigned long received, unsigned long lost,
                               uint8_t *buf, size_t buf_size)
{
    unsigned r;
    unsigned l;

    if (buf == NULL || buf_size < TAKION_CONGESTION_PACKET_SIZE)
        return 0u;

    /* Saturate. See the header: a u16 cannot carry more, and an interval that busy is a reporting
     * problem rather than a reason to report a small number. */
    r = (received > 65535ul) ? 65535u : (unsigned)received;
    l = (lost > 65535ul) ? 65535u : (unsigned)lost;

    memset(buf, 0, TAKION_CONGESTION_PACKET_SIZE);
    buf[0] = 0x05u;                       /* base type 5                                    */
    /* buf[1..2] are zero in every observed packet - left as the memset above made them.    */
    buf[3] = (uint8_t)(r >> 8);
    buf[4] = (uint8_t)r;
    buf[5] = (uint8_t)(l >> 8);
    buf[6] = (uint8_t)l;
    /* buf[7..10] GMAC and buf[11..14] key position are the sealer's to write. */
    return TAKION_CONGESTION_PACKET_SIZE;
}
