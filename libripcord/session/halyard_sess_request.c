#include "halyard_sess_request.h"
#include "../util/rc_text.h"

#include <stdarg.h>
#include <stdio.h>
#include <string.h>

static char s_path_buf[64];

const char *halyard_sess_path(int is_ps5, const char *endpoint)
{
    snprintf(s_path_buf, sizeof(s_path_buf), "/sie/%s/rp/sess/%s", is_ps5 ? "ps5" : "ps4", endpoint);
    return s_path_buf;
}

const char *halyard_sess_version(int is_ps5)
{
    return is_ps5 ? "1.0" : "10.0";
}

void halyard_sess_request_init(halyard_sess_request *req, const char *method, const char *path)
{
    req->method = method;
    req->path = path;
    req->http_version = "HTTP/1.1";
    req->header_count = 0;
}

int halyard_sess_request_add_header(halyard_sess_request *req, const char *name, const char *value)
{
    if (req->header_count >= HALYARD_SESS_MAX_HEADERS)
        return 0;
    req->headers[req->header_count].name = name;
    req->headers[req->header_count].value = value;
    req->header_count++;
    return 1;
}

/* Appends formatted text at *offset, clamping the write pointer to buf_size so an already-overflowed
 * offset never forms a pointer past one-past-the-end of buf - forming (let alone using) such a pointer
 * is undefined behaviour even when the accompanying size argument is 0. Returns 0 on a formatting
 * error; overflow itself is reported by the final offset check in the caller, not here. */
static int append(char *buf, size_t buf_size, size_t *offset, const char *fmt, ...)
{
    va_list args;
    int n;
    size_t safe_offset = (*offset < buf_size) ? *offset : buf_size;
    size_t remaining = buf_size - safe_offset;

    va_start(args, fmt);
    n = vsnprintf(buf + safe_offset, remaining, fmt, args);
    va_end(args);

    if (n < 0)
        return 0;
    *offset += (size_t)n;
    return 1;
}

size_t halyard_sess_request_serialize(const halyard_sess_request *req, char *buf, size_t buf_size)
{
    size_t offset = 0;
    size_t i;

    if (!append(buf, buf_size, &offset, "%s %s %s\r\n", req->method, req->path, req->http_version))
        return 0;
    for (i = 0; i < req->header_count; i++) {
        if (!append(buf, buf_size, &offset, "%s: %s\r\n", req->headers[i].name, req->headers[i].value))
            return 0;
    }
    if (!append(buf, buf_size, &offset, "Content-Length: 0\r\n\r\n"))
        return 0;

    return (offset <= buf_size) ? offset : 0;
}

static const char *find_header_end(const char *data, size_t length)
{
    size_t i;
    for (i = 0; i + 4 <= length; i++) {
        if (memcmp(data + i, "\r\n\r\n", 4) == 0)
            return data + i;
    }
    return NULL;
}

/* Reads the header block's Content-Length, or 0 if absent - every exchange this port makes sends
 * Content-Length: 0, but a console's own response is not this port's to assume. */
static int read_content_length(const char *headers_start, const char *header_end)
{
    const char *line = headers_start;

    while (line < header_end) {
        const char *line_end = memchr(line, '\n', (size_t)(header_end - line));
        const char *this_end = (line_end != NULL) ? line_end : header_end;
        const char *value_end = this_end;
        const char *colon;

        if (value_end > line && *(value_end - 1) == '\r')
            value_end--;

        colon = memchr(line, ':', (size_t)(value_end - line));
        if (colon != NULL && colon > line && rc_text_field_is(line, colon, "Content-Length")) {
            const char *v = colon + 1;
            int value = 0;

            rc_text_trim(&v, &value_end);
            while (v < value_end && *v >= '0' && *v <= '9') {
                value = value * 10 + (*v - '0');
                v++;
            }
            return value;
        }

        line = (line_end != NULL) ? line_end + 1 : header_end;
    }

    return 0;
}

size_t halyard_sess_response_parse(const char *data, size_t length, halyard_sess_response *out)
{
    const char *header_end = find_header_end(data, length);
    const char *status_line_end;
    const char *p;
    const char *p_end;
    size_t body_start;
    int content_length;
    int status_code = 0;
    int have_digit = 0;

    if (header_end == NULL)
        return 0; /* header block hasn't fully arrived */

    status_line_end = memchr(data, '\n', (size_t)(header_end - data));
    p_end = (status_line_end != NULL) ? status_line_end : header_end;
    p = data;
    if (p_end > p && *(p_end - 1) == '\r')
        p_end--;

    /* Status line: "HTTP/1.1 200 OK" - skip the version token and the following run of spaces, then
     * read the numeric code. */
    while (p < p_end && *p != ' ')
        p++;
    while (p < p_end && *p == ' ')
        p++;
    while (p < p_end && *p >= '0' && *p <= '9') {
        status_code = status_code * 10 + (*p - '0');
        p++;
        have_digit = 1;
    }
    if (!have_digit)
        return 0;

    body_start = (size_t)(header_end - data) + 4;
    content_length = read_content_length(
        (status_line_end != NULL) ? status_line_end + 1 : header_end, header_end);
    if (content_length < 0 || length < body_start + (size_t)content_length)
        return 0; /* body not fully arrived */

    out->status_code = status_code;
    out->data = data;
    out->header_block_length = (size_t)(header_end - data);
    return body_start + (size_t)content_length;
}

int halyard_sess_response_header(const char *data, const halyard_sess_response *response,
                                 const char *name, char *out, size_t out_size)
{
    const char *header_end = data + response->header_block_length;
    const char *first_nl = memchr(data, '\n', response->header_block_length);
    const char *line;

    if (first_nl == NULL)
        return 0;
    line = first_nl + 1;

    while (line < header_end) {
        const char *line_end = memchr(line, '\n', (size_t)(header_end - line));
        const char *this_end = (line_end != NULL) ? line_end : header_end;
        const char *value_end = this_end;
        const char *colon;

        if (value_end > line && *(value_end - 1) == '\r')
            value_end--;

        colon = memchr(line, ':', (size_t)(value_end - line));
        if (colon != NULL && colon > line && rc_text_field_is(line, colon, name)) {
            rc_text_copy_trimmed(colon + 1, value_end, out, out_size);
            return 1;
        }

        line = (line_end != NULL) ? line_end + 1 : header_end;
    }

    return 0;
}
