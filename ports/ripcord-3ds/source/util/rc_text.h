/*
 * ripcord-3ds - small text helpers shared by the wire formats that are HTTP-shaped but hand-parsed
 * (SRCH discovery, the /sess/init and /sess/ctrl exchanges). No protocol knowledge here - just the
 * trim/case-insensitive-match logic every one of those parsers needs for "is this line the header named
 * X, and what's its value with the whitespace stripped."
 *
 * Factored out once a second module needed the exact same dozen lines source/discovery/halyard_discovery.c
 * already had as private statics, not written ahead of any real use.
 */
#ifndef RC_TEXT_H
#define RC_TEXT_H

#include <stddef.h>

/* Case-insensitive, fixed-length compare. Avoids depending on strncasecmp (POSIX <strings.h>, not
 * guaranteed present in every C99 environment this port might be compiled in) for one small check. */
int rc_text_equals_ci(const char *a, const char *b, size_t length);

/* Narrows [*start, *end) by advancing/retreating past leading/trailing ASCII whitespace. */
void rc_text_trim(const char **start, const char **end);

/* Copies the trimmed range [start, end) into dst, truncating to fit and always NUL-terminating. */
void rc_text_copy_trimmed(const char *start, const char *end, char *dst, size_t dst_size);

/* True if the trimmed range [start, end) is exactly `name`, case-insensitively - the "is this header
 * line's name field the one I'm looking for" check. */
int rc_text_field_is(const char *start, const char *end, const char *name);

#endif /* RC_TEXT_H */
