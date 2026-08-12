#include "halyard_discovery.h"

#include <ctype.h>
#include <stdio.h>
#include <string.h>

const halyard_discovery_profile halyard_discovery_profile_ps5 = { "PS5", 9302, "00030010" };
const halyard_discovery_profile halyard_discovery_profile_ps4 = { "PS4", 987, "00020020" };

size_t halyard_discovery_build_probe(const halyard_discovery_profile *profile, char *buf, size_t buf_size)
{
    int written = snprintf(buf, buf_size,
        "SRCH * HTTP/1.1\r\ndevice-discovery-protocol-version:%s\r\n\r\n",
        profile->protocol_version);

    if (written < 0 || (size_t)written >= buf_size)
        return 0;
    return (size_t)written;
}

/* Case-insensitive, fixed-length compare - avoids depending on strncasecmp (POSIX <strings.h>, not
 * guaranteed present in every C99 environment this port might be compiled in) for one small check. */
static int equals_ci(const char *a, const char *b, size_t length)
{
    size_t i;
    for (i = 0; i < length; i++) {
        if (tolower((unsigned char)a[i]) != tolower((unsigned char)b[i]))
            return 0;
    }
    return 1;
}

static void trim_range(const char **start, const char **end)
{
    while (*start < *end && isspace((unsigned char)**start))
        (*start)++;
    while (*end > *start && isspace((unsigned char)*(*end - 1)))
        (*end)--;
}

/* Copies the trimmed range [start, end) into dst, truncating to fit and always NUL-terminating. */
static void copy_trimmed(const char *start, const char *end, char *dst, size_t dst_size)
{
    size_t length;

    trim_range(&start, &end);
    length = (size_t)(end - start);
    if (length >= dst_size)
        length = dst_size - 1;
    memcpy(dst, start, length);
    dst[length] = '\0';
}

/* True if the trimmed range [start, end) is exactly `name`, case-insensitively. */
static int field_is(const char *start, const char *end, const char *name)
{
    size_t name_len = strlen(name);

    trim_range(&start, &end);
    if ((size_t)(end - start) != name_len)
        return 0;
    return equals_ci(start, name, name_len);
}

int halyard_discovery_parse_response(const char *data, size_t length, const char *source_address,
                                     halyard_discovered_console *out)
{
    /* SRCH responses are a handful of short header lines - well under this, even with a generous
     * host-name. A datagram that somehow exceeds it is truncated, not rejected: the status line and
     * host-id (both near the front) are what parsing actually depends on. */
    char text[1024];
    size_t copy_len = (length < sizeof(text) - 1) ? length : sizeof(text) - 1;
    const char *text_end;
    const char *line_start;
    int have_host_id = 0;
    int is_awake = 0;
    int seen_status_line = 0;

    memcpy(text, data, copy_len);
    text[copy_len] = '\0';
    text_end = text + copy_len;

    memset(out, 0, sizeof(*out));
    strncpy(out->host_type, "PS5", sizeof(out->host_type) - 1); /* spec's own GetValueOrDefault fallback */
    if (source_address != NULL)
        copy_trimmed(source_address, source_address + strlen(source_address),
                     out->address, sizeof(out->address));

    line_start = text;
    while (line_start <= text_end) {
        const char *newline = memchr(line_start, '\n', (size_t)(text_end - line_start));
        const char *line_end = (newline != NULL) ? newline : text_end;

        /* The response is CRLF-terminated per spec (unlike the LF-only WAKEUP datagram this module
         * does not build); strip a trailing CR so it never ends up inside a parsed field. */
        if (line_end > line_start && *(line_end - 1) == '\r')
            line_end--;

        if (!seen_status_line) {
            seen_status_line = 1;
            if ((size_t)(line_end - line_start) < 8 || !equals_ci(line_start, "HTTP/1.1", 8))
                return 0; /* not a SRCH-shaped reply at all */

            {
                const char *p = line_start + 8;
                while (p < line_end && isspace((unsigned char)*p))
                    p++;
                is_awake = (line_end - p >= 3 && strncmp(p, "200", 3) == 0);
            }
        } else if (line_start < line_end) {
            const char *colon = memchr(line_start, ':', (size_t)(line_end - line_start));
            if (colon != NULL && colon > line_start) {
                if (field_is(line_start, colon, "host-id")) {
                    copy_trimmed(colon + 1, line_end, out->host_id, sizeof(out->host_id));
                    have_host_id = 1;
                } else if (field_is(line_start, colon, "host-type")) {
                    copy_trimmed(colon + 1, line_end, out->host_type, sizeof(out->host_type));
                } else if (field_is(line_start, colon, "host-name")) {
                    copy_trimmed(colon + 1, line_end, out->host_name, sizeof(out->host_name));
                } else if (field_is(line_start, colon, "system-version")) {
                    copy_trimmed(colon + 1, line_end, out->system_version, sizeof(out->system_version));
                }
            }
        }

        if (newline == NULL)
            break;
        line_start = newline + 1;
    }

    if (!have_host_id)
        return 0;

    out->is_awake = is_awake;
    return 1;
}
