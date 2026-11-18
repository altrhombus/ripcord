/*
 * ripcord - making sense of a PSN account id somebody typed in.
 *
 * The account id is a 64-bit number that tools and guides print as decimal, hex, or the base64 of its
 * eight bytes. halyard_regist_message.c encodes an all-decimal id as a little-endian uint64 and anything
 * else as its own UTF-8 bytes - which is correct, and means a hex id is sent as the ASCII characters
 * "1a2b..." rather than as the number, and the console refuses it without saying why.
 *
 * SO THE INPUT IS CLASSIFIED BEFORE IT TRAVELS. What can be read as a 64-bit id is normalised to decimal;
 * what cannot is REFUSED WITH A REASON.
 *
 * BASE64 IS RECOGNISED AND DELIBERATELY NOT ACCEPTED. Eight bytes have two possible orders and this
 * project has confirmed only the one the WIRE uses - not the one tools print. Guessing could send a
 * byte-reversed id, so the user is asked for the decimal form instead.
 */
#ifndef HALYARD_ACCOUNT_ID_H
#define HALYARD_ACCOUNT_ID_H

#include <stddef.h>

/* Decimal, 20 digits and a terminator. */
#define HALYARD_ACCOUNT_ID_TEXT_MAX 21

typedef enum {
    HALYARD_ACCOUNT_ID_OK = 0,
    HALYARD_ACCOUNT_ID_EMPTY,
    HALYARD_ACCOUNT_ID_BASE64,      /* recognised; its byte order is not ours to guess */
    HALYARD_ACCOUNT_ID_UNREADABLE
} halyard_account_id_status;

/*
 * Reads `in` as a 64-bit account id and writes it back as decimal. `out` needs
 * HALYARD_ACCOUNT_ID_TEXT_MAX bytes. Surrounding whitespace is ignored - a pasted value very often
 * carries some, and refusing over it would be a poor use of the only error a user sees.
 *
 * ACCEPTED: decimal digits; hex with an 0x prefix; and sixteen hex characters without one, which cannot
 * be anything else. [X] A bare hex id is read as a NUMBER, most significant digit first, which is what
 * hex means everywhere - but a tool that prints the eight bytes in wire order would produce the reverse,
 * and this project has not seen one to check against.
 */
halyard_account_id_status halyard_account_id_normalise(const char *in, char *out, size_t size);

/* Why it was refused, in words a person in front of a television can act on. Never NULL. */
const char *halyard_account_id_status_text(halyard_account_id_status status);

#endif /* HALYARD_ACCOUNT_ID_H */
