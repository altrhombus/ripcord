/*
 * AES-128 forward cipher (FIPS-197).
 *
 * Encrypt-only, on purpose: CFB, OFB and CTR are keystream modes that only ever call the forward
 * permutation, so the inverse S-box and InvMixColumns would be dead weight on a 128 MB handheld.
 *
 * State layout is the FIPS-197 column-major one: byte i of the block is state[row = i % 4][col = i / 4].
 * Every index below assumes that; ShiftRows in particular is unreadable under any other convention.
 *
 * THE STATE LIVES IN FOUR WORDS, NOT A BYTE ARRAY, AND THAT IS LOAD-BEARING. Column c is held as one
 * big-endian uint32 with row 0 in the most significant byte. The obvious transcription of FIPS-197 keeps
 * `uint8_t s[16]` and walks it, which reads beautifully and is ruinous on an in-order core: every step
 * stores a byte and the next step immediately loads it back, and a store-to-load forward of the same
 * address is precisely the case such a pipeline cannot shortcut. Measured on the PS3's PPE, the packet
 * cipher and its GMAC together cost 837 us per ~1,400-byte packet - about 99% of the demuxer's whole
 * per-packet budget - with the arithmetic itself accounting for a small fraction of that. Word-at-a-time
 * keeps the state in registers, so SubBytes/ShiftRows/MixColumns never round-trip through memory.
 *
 * This is the same lesson rc_gcm.h records for GHASH, one layer down, and the fix has the same shape.
 *
 * The S-box stays a 256-byte table, deliberately: the faster-still form is a set of 4 KB "T-tables", and
 * a table that large is materially easier to observe through the data cache. The word rewrite gets most
 * of the win without widening that surface.
 */
#include "rc_crypto.h"

#include <string.h>

/* FIPS-197 figure 7. */
static const uint8_t kSbox[256] = {
    0x63, 0x7c, 0x77, 0x7b, 0xf2, 0x6b, 0x6f, 0xc5, 0x30, 0x01, 0x67, 0x2b, 0xfe, 0xd7, 0xab, 0x76,
    0xca, 0x82, 0xc9, 0x7d, 0xfa, 0x59, 0x47, 0xf0, 0xad, 0xd4, 0xa2, 0xaf, 0x9c, 0xa4, 0x72, 0xc0,
    0xb7, 0xfd, 0x93, 0x26, 0x36, 0x3f, 0xf7, 0xcc, 0x34, 0xa5, 0xe5, 0xf1, 0x71, 0xd8, 0x31, 0x15,
    0x04, 0xc7, 0x23, 0xc3, 0x18, 0x96, 0x05, 0x9a, 0x07, 0x12, 0x80, 0xe2, 0xeb, 0x27, 0xb2, 0x75,
    0x09, 0x83, 0x2c, 0x1a, 0x1b, 0x6e, 0x5a, 0xa0, 0x52, 0x3b, 0xd6, 0xb3, 0x29, 0xe3, 0x2f, 0x84,
    0x53, 0xd1, 0x00, 0xed, 0x20, 0xfc, 0xb1, 0x5b, 0x6a, 0xcb, 0xbe, 0x39, 0x4a, 0x4c, 0x58, 0xcf,
    0xd0, 0xef, 0xaa, 0xfb, 0x43, 0x4d, 0x33, 0x85, 0x45, 0xf9, 0x02, 0x7f, 0x50, 0x3c, 0x9f, 0xa8,
    0x51, 0xa3, 0x40, 0x8f, 0x92, 0x9d, 0x38, 0xf5, 0xbc, 0xb6, 0xda, 0x21, 0x10, 0xff, 0xf3, 0xd2,
    0xcd, 0x0c, 0x13, 0xec, 0x5f, 0x97, 0x44, 0x17, 0xc4, 0xa7, 0x7e, 0x3d, 0x64, 0x5d, 0x19, 0x73,
    0x60, 0x81, 0x4f, 0xdc, 0x22, 0x2a, 0x90, 0x88, 0x46, 0xee, 0xb8, 0x14, 0xde, 0x5e, 0x0b, 0xdb,
    0xe0, 0x32, 0x3a, 0x0a, 0x49, 0x06, 0x24, 0x5c, 0xc2, 0xd3, 0xac, 0x62, 0x91, 0x95, 0xe4, 0x79,
    0xe7, 0xc8, 0x37, 0x6d, 0x8d, 0xd5, 0x4e, 0xa9, 0x6c, 0x56, 0xf4, 0xea, 0x65, 0x7a, 0xae, 0x08,
    0xba, 0x78, 0x25, 0x2e, 0x1c, 0xa6, 0xb4, 0xc6, 0xe8, 0xdd, 0x74, 0x1f, 0x4b, 0xbd, 0x8b, 0x8a,
    0x70, 0x3e, 0xb5, 0x66, 0x48, 0x03, 0xf6, 0x0e, 0x61, 0x35, 0x57, 0xb9, 0x86, 0xc1, 0x1d, 0x9e,
    0xe1, 0xf8, 0x98, 0x11, 0x69, 0xd9, 0x8e, 0x94, 0x9b, 0x1e, 0x87, 0xe9, 0xce, 0x55, 0x28, 0xdf,
    0x8c, 0xa1, 0x89, 0x0d, 0xbf, 0xe6, 0x42, 0x68, 0x41, 0x99, 0x2d, 0x0f, 0xb0, 0x54, 0xbb, 0x16
};

/* Multiply by x in GF(2^8) modulo the AES polynomial 0x11b. Branch-free so it does not leak the high bit
 * through a timing channel; neither target is a threat model where that matters much, but the free
 * version is the correct habit. */
static uint8_t xtime(uint8_t value)
{
    return (uint8_t)((value << 1) ^ (uint8_t)((value >> 7) * 0x1b));
}

/*
 * xtime applied to all four bytes of a word at once. The high bits are masked off before the shift so a
 * byte cannot carry into its neighbour, and the conditional 0x1b is reintroduced by multiplying the
 * collected high bits (each byte now 0 or 1) by 0x1b - safe because 0x1b fits in a byte, so that
 * multiply cannot carry either.
 */
static uint32_t xtime_word(uint32_t w)
{
    uint32_t high = w & 0x80808080u;
    return ((w & 0x7f7f7f7fu) << 1) ^ ((high >> 7) * 0x1bu);
}

/* Rotate a word left by one byte: [a0,a1,a2,a3] -> [a1,a2,a3,a0]. FIPS-197's RotWord. */
static uint32_t rol8(uint32_t w)
{
    return (w << 8) | (w >> 24);
}

static uint32_t load_be32(const uint8_t *p)
{
    return ((uint32_t)p[0] << 24) | ((uint32_t)p[1] << 16) | ((uint32_t)p[2] << 8) | (uint32_t)p[3];
}

static void store_be32(uint8_t *p, uint32_t w)
{
    p[0] = (uint8_t)(w >> 24);
    p[1] = (uint8_t)(w >> 16);
    p[2] = (uint8_t)(w >> 8);
    p[3] = (uint8_t)w;
}

/* Byte of `w` at state row `r` - row 0 is the most significant. */
#define ROW(w, r) ((uint8_t)((w) >> (8 * (3 - (r)))))

/* SubBytes over a whole word. */
static uint32_t sub_word(uint32_t w)
{
    return ((uint32_t)kSbox[ROW(w, 0)] << 24) | ((uint32_t)kSbox[ROW(w, 1)] << 16) |
           ((uint32_t)kSbox[ROW(w, 2)] << 8)  | (uint32_t)kSbox[ROW(w, 3)];
}

/*
 * MixColumns on one column, from the same identity the byte-wise version used:
 *   b_r = a_r ^ (a0^a1^a2^a3) ^ xtime(a_r ^ a_{r+1})
 * `all` puts a0^a1^a2^a3 in every byte, and `pairs` puts a_r ^ a_{r+1} in byte r, so both correction
 * terms are computed for all four rows at once.
 */
static uint32_t mix_column(uint32_t a)
{
    uint32_t rotated = rol8(a);
    uint32_t pairs = a ^ rotated;
    uint32_t all = pairs ^ rol8(rotated) ^ rol8(rol8(rotated));

    return a ^ all ^ xtime_word(pairs);
}

void rc_aes128_init(rc_aes128 *ctx, const uint8_t key[RC_AES128_KEY_SIZE])
{
    uint8_t rcon = 0x01;
    int i;

    for (i = 0; i < 4; i++)
        ctx->round_keys[i] = load_be32(key + 4 * i);

    for (i = 4; i < 44; i++) {
        uint32_t t = ctx->round_keys[i - 1];

        /* Once per round key: RotWord, SubWord, XOR Rcon into the leading byte. */
        if ((i & 3) == 0) {
            t = sub_word(rol8(t)) ^ ((uint32_t)rcon << 24);
            rcon = xtime(rcon); /* 01 02 04 08 10 20 40 80 1b 36 */
        }

        ctx->round_keys[i] = ctx->round_keys[i - 4] ^ t;
    }
}

/*
 * SubBytes and ShiftRows fold together: row r of the output column c is row r of the input column
 * (c + r) mod 4, so each output word gathers one already-substituted byte from four different columns.
 */
#define SHIFT_ROWS_COLUMN(c0, c1, c2, c3)                    \
    (((uint32_t)kSbox[ROW((c0), 0)] << 24) |                 \
     ((uint32_t)kSbox[ROW((c1), 1)] << 16) |                 \
     ((uint32_t)kSbox[ROW((c2), 2)] << 8)  |                 \
      (uint32_t)kSbox[ROW((c3), 3)])

void rc_aes128_encrypt_block(const rc_aes128 *ctx,
                             const uint8_t in[RC_AES_BLOCK_SIZE],
                             uint8_t out[RC_AES_BLOCK_SIZE])
{
    const uint32_t *rk = ctx->round_keys;
    uint32_t s0, s1, s2, s3;
    uint32_t t0, t1, t2, t3;
    int round;

    /* Initial AddRoundKey. */
    s0 = load_be32(in + 0)  ^ rk[0];
    s1 = load_be32(in + 4)  ^ rk[1];
    s2 = load_be32(in + 8)  ^ rk[2];
    s3 = load_be32(in + 12) ^ rk[3];

    for (round = 1; round <= 9; round++) {
        t0 = SHIFT_ROWS_COLUMN(s0, s1, s2, s3);
        t1 = SHIFT_ROWS_COLUMN(s1, s2, s3, s0);
        t2 = SHIFT_ROWS_COLUMN(s2, s3, s0, s1);
        t3 = SHIFT_ROWS_COLUMN(s3, s0, s1, s2);

        rk += 4;
        s0 = mix_column(t0) ^ rk[0];
        s1 = mix_column(t1) ^ rk[1];
        s2 = mix_column(t2) ^ rk[2];
        s3 = mix_column(t3) ^ rk[3];
    }

    /* Final round omits MixColumns (FIPS-197 5.1). */
    t0 = SHIFT_ROWS_COLUMN(s0, s1, s2, s3);
    t1 = SHIFT_ROWS_COLUMN(s1, s2, s3, s0);
    t2 = SHIFT_ROWS_COLUMN(s2, s3, s0, s1);
    t3 = SHIFT_ROWS_COLUMN(s3, s0, s1, s2);

    rk += 4;
    store_be32(out + 0,  t0 ^ rk[0]);
    store_be32(out + 4,  t1 ^ rk[1]);
    store_be32(out + 8,  t2 ^ rk[2]);
    store_be32(out + 12, t3 ^ rk[3]);
}
