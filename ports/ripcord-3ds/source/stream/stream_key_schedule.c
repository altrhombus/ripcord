#include "stream_key_schedule.h"
#include "../crypto/rc_crypto.h"

#include <string.h>

void stream_key_schedule_derive_direction(const uint8_t *shared_secret, size_t shared_secret_length,
                                          const uint8_t handshake_key[16], unsigned direction,
                                          uint8_t out_aes_key[16], uint8_t out_base_iv[16])
{
    uint8_t info[21];
    uint8_t block[32];

    info[0] = 0x01;
    info[1] = (uint8_t)direction;
    info[2] = 0x00;
    memcpy(info + 3, handshake_key, 16);
    info[19] = 0x01; /* big-endian output length in bits: 0x0100 = 256 */
    info[20] = 0x00;

    rc_hmac_sha256(shared_secret, shared_secret_length, info, sizeof(info), block);

    memcpy(out_aes_key, block, 16);
    memcpy(out_base_iv, block + 16, 16);
}
