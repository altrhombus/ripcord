/*
 * ripcord-3ds - base64 (RFC 4648, standard alphabet, with padding).
 *
 * A generic codec, not protocol-specific: /sess/ctrl's encrypted RP-* header values are base64, and
 * /sess/init's RP-Nonce response header is base64-encoded 16 bytes. No PlayStation knowledge here -
 * source/session sits on top of this the same way source/halyard sits on top of source/crypto.
 */
#ifndef RC_BASE64_H
#define RC_BASE64_H

#include <stddef.h>
#include <stdint.h>

/* Bytes needed to hold the base64 encoding of `input_length` raw bytes, including the NUL terminator
 * this module always writes. */
#define RC_BASE64_ENCODED_SIZE(input_length) ((((input_length) + 2) / 3) * 4 + 1)

/*
 * Encode `input`/`input_length` into `out`, NUL-terminated. Returns the encoded length (excluding the
 * NUL), or 0 if `out_size` is too small (see RC_BASE64_ENCODED_SIZE).
 */
size_t rc_base64_encode(const uint8_t *input, size_t input_length, char *out, size_t out_size);

/*
 * Decode a base64 string (length-delimited, NOT required to be NUL-terminated) into `out`. Returns the
 * decoded byte count, or (size_t)-1 if the input is malformed (bad character, wrong padding/length) or
 * `out_size` is too small for the decoded result.
 */
size_t rc_base64_decode(const char *input, size_t input_length, uint8_t *out, size_t out_size);

#endif /* RC_BASE64_H */
