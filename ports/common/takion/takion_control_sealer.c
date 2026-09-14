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

void takion_control_sealer_seal(void *ctx, uint8_t *packet, size_t length)
{
    takion_control_sealer *sealer = (takion_control_sealer *)ctx;
    uint64_t key_pos;
    size_t remainder;
    size_t aligned;

    if (sealer == NULL || !sealer->enabled || packet == NULL)
        return;
    /* Too short to carry the fields this writes. Leaving it unsealed is the honest outcome - writing
     * past the end to satisfy an offset would be worse than an unauthenticated packet. */
    if (length < (size_t)(TAKION_CONTROL_KEYPOS_OFFSET + 4u))
        return;

    /*
     * Reserve first, advance immediately. The position is a byte count rounded up to the block size, and
     * a repeated position is a repeated GMAC nonce under the same key - the same class of mistake as a
     * repeated IV, and just as quiet.
     */
    key_pos = sealer->key_pos;
    remainder = length % 16u;
    aligned = length + ((remainder == 0u) ? 0u : (16u - remainder));
    sealer->key_pos += (uint64_t)aligned;

    packet[TAKION_CONTROL_KEYPOS_OFFSET + 0u] = (uint8_t)(key_pos >> 24);
    packet[TAKION_CONTROL_KEYPOS_OFFSET + 1u] = (uint8_t)(key_pos >> 16);
    packet[TAKION_CONTROL_KEYPOS_OFFSET + 2u] = (uint8_t)(key_pos >> 8);
    packet[TAKION_CONTROL_KEYPOS_OFFSET + 3u] = (uint8_t)key_pos;

    /* zero_key_pos = 1: the control AAD zeroes the key-position field as well as the tag. See the
     * header - this is the half that was established by recomputing 727 packets, not by reading. */
    (void)stream_packet_crypto_seal(&sealer->crypto, key_pos, packet, length,
                                    (int)TAKION_CONTROL_TAG_OFFSET, 1);
}

void takion_control_sealer_reset(takion_control_sealer *sealer)
{
    if (sealer != NULL)
        memset(sealer, 0, sizeof(*sealer));
}
