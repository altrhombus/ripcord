/*
 * ripcord-3ds - the /sess/init and /sess/ctrl HTTP-like exchange (spec sec 2.1).
 *
 * These requests are HTTP-shaped but hand-built rather than real HTTP - re-derived here from
 * Ripcord.Protocol.Halyard.Common.Control.SessProtocol, which this is checked against, not translated
 * from. Both requests are GET, header-only (no body this port ever sends), so the builder always emits
 * "Content-Length: 0" rather than taking a body parameter - if a future phase needs POST /sess/rgst
 * (registration, out of scope: see README's "Pairing happens on a PC, not here"), that is a reason to
 * widen this, not a reason to generalise ahead of it now.
 */
#ifndef HALYARD_SESS_REQUEST_H
#define HALYARD_SESS_REQUEST_H

#include <stddef.h>

#define HALYARD_SESS_MAX_HEADERS 16

typedef struct {
    const char *name;
    const char *value;
} halyard_sess_header;

typedef struct {
    const char *method;       /* "GET" - every request this port sends is one */
    const char *path;         /* e.g. "/sie/ps5/rp/sess/init" - see halyard_sess_path() */
    const char *http_version; /* "HTTP/1.1" */
    halyard_sess_header headers[HALYARD_SESS_MAX_HEADERS];
    size_t header_count;
} halyard_sess_request;

/* The /sie/{family}/rp/sess/{endpoint} path for a console family - PS4 = "ps4", PS5 = "ps5"
 * (wire-confirmed), endpoint is "init" or "ctrl". Returns a pointer into a small internal static
 * buffer, valid until the next call - callers use it immediately when building a request, never store
 * it. */
const char *halyard_sess_path(int is_ps5, const char *endpoint);

/* "1.0" for PS5, "10.0" for PS4 - the RP-Version value a console family expects on any /sess endpoint. */
const char *halyard_sess_version(int is_ps5);

void halyard_sess_request_init(halyard_sess_request *req, const char *method, const char *path);

/* Adds one header. Returns 1 on success, 0 if HALYARD_SESS_MAX_HEADERS is already reached - callers own
 * the string lifetimes (this struct only stores pointers), same as the request/header structs it mirrors. */
int halyard_sess_request_add_header(halyard_sess_request *req, const char *name, const char *value);

/* Serializes the request line, headers, "Content-Length: 0" and the trailing blank line into buf.
 * Returns the byte count written, or 0 if buf_size was too small (nothing is written in that case). */
size_t halyard_sess_request_serialize(const halyard_sess_request *req, char *buf, size_t buf_size);

/* A parsed response: just enough to act on. `data`/`header_block_length` point back into the buffer
 * passed to halyard_sess_response_parse() - use halyard_sess_response_header() on the SAME buffer to
 * look up individual header values; nothing here is copied. */
typedef struct {
    int status_code;
    const char *data;
    size_t header_block_length; /* bytes from the response's start up to (not including) the blank line */
} halyard_sess_response;

/*
 * Attempts to parse one complete response (status line + headers + any declared Content-Length body)
 * from the front of [data, length). Returns the total bytes consumed on success, or 0 if the header
 * block or body has not fully arrived yet - a stream reader should read more and retry, the same
 * "incomplete, not malformed" convention halyard_ctrl_message_parse() uses for the same reason (TCP
 * delivers a byte stream, not message boundaries).
 */
size_t halyard_sess_response_parse(const char *data, size_t length, halyard_sess_response *out);

/* Looks up one header's value (trimmed, NUL-terminated into out) from a response previously parsed by
 * halyard_sess_response_parse() - pass the SAME data buffer. Case-insensitive name match. Returns 1 if
 * found. */
int halyard_sess_response_header(const char *data, const halyard_sess_response *response,
                                 const char *name, char *out, size_t out_size);

#endif /* HALYARD_SESS_REQUEST_H */
