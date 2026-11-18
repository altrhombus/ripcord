/*
 * libripcord - lowercase hex encode/decode.
 *
 * A generic codec, not protocol-specific: /sess/init's RP-Registkey header is the hex of the raw
 * registration-key bytes, and the pairing-record file (session/halyard_pairing_file.c) stores its key
 * material as hex.
 */
#ifndef RC_HEX_H
#define RC_HEX_H

#include <stddef.h>
#include <stdint.h>

#define RC_HEX_ENCODED_SIZE(input_length) ((input_length) * 2 + 1)

/* Encodes input/input_length into out as lowercase hex, NUL-terminated. out must be at least
 * RC_HEX_ENCODED_SIZE(input_length) bytes; always succeeds. */
void rc_hex_encode(const uint8_t *input, size_t input_length, char *out);

/* Decodes a hex string (even length, no separators, upper/lower/mixed case accepted) into out. Returns
 * the decoded byte count, or (size_t)-1 if the input has an odd length, contains a non-hex character,
 * or the decoded result would not fit in out_size. */
size_t rc_hex_decode(const char *text, uint8_t *out, size_t out_size);

#endif /* RC_HEX_H */
