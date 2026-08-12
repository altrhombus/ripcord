/*
 * ripcord-3ds - Phase 3 LAN discovery (the SRCH broadcast/response).
 *
 * Ported from Ripcord.Protocol.Halyard.Common.Discovery.HalyardSearchClient, and checked in
 * tests/discovery_test.c against docs/protocol/ps5-local-discovery.md's own worked examples - if this
 * ever disagrees with the spec text, trust the spec over the .NET code, since both were meant to
 * implement the same document.
 *
 * WIRE FORMAT (docs/protocol/ps5-local-discovery.md, "UDP 9302 SRCH discovery broadcast"):
 *
 *   Request (client -> LAN broadcast, CRLF-terminated):
 *     SRCH * HTTP/1.1\r\n
 *     device-discovery-protocol-version:00030010\r\n
 *     \r\n
 *
 *   Response (console -> client, unicast, CRLF-terminated):
 *     HTTP/1.1 200 Ok\r\n              (or "HTTP/1.1 620 Server Standby" if resting)
 *     host-id:<12 hex digits>\r\n
 *     host-type:PS5\r\n
 *     host-name:<user-assigned device name>\r\n
 *     host-request-port:997\r\n
 *     device-discovery-protocol-version:00030010\r\n
 *     system-version:<8-digit numeric string>\r\n
 *     \r\n
 *
 * `host-request-port` is deliberately not parsed here: the spec calls it "a red herring" - the LAN
 * wake exchange (not yet implemented on this port) goes to the discovery port itself (9302/987), never
 * to this advertised value, on both console families. Carrying a field this port cannot act on would
 * just be a second thing to keep in sync with a spec section that already says not to use it.
 *
 * PLAIN ASCII, HTTP-STATUS-LINE-LIKE BUT NOT FULL HTTP: no header-section framing beyond the trailing
 * blank line, no Host:/User-Agent:. Only PS5 is a build target for this port (README's "Can the
 * hardware actually do this?" - HEVC-only-decoder New 3DS story), but the PS4 profile is included
 * because the wire format is otherwise identical and carrying both costs nothing but a second constant
 * table, the same call this repository's .NET side already made.
 */
#ifndef HALYARD_DISCOVERY_H
#define HALYARD_DISCOVERY_H

#include <stddef.h>

#define HALYARD_DISCOVERY_HOST_ID_MAX 32
#define HALYARD_DISCOVERY_HOST_TYPE_MAX 16
#define HALYARD_DISCOVERY_HOST_NAME_MAX 128
#define HALYARD_DISCOVERY_SYSTEM_VERSION_MAX 16
#define HALYARD_DISCOVERY_ADDRESS_MAX 16 /* dotted-quad IPv4, e.g. "255.255.255.255" + NUL */

typedef struct {
    const char *host_type;         /* "PS5" or "PS4" - the value this profile expects in the reply */
    unsigned short port;           /* SRCH broadcast/response UDP port for this console family */
    const char *protocol_version;  /* device-discovery-protocol-version, echoed by a matching console */
} halyard_discovery_profile;

extern const halyard_discovery_profile halyard_discovery_profile_ps5;
extern const halyard_discovery_profile halyard_discovery_profile_ps4;

typedef struct {
    char host_id[HALYARD_DISCOVERY_HOST_ID_MAX];
    char host_type[HALYARD_DISCOVERY_HOST_TYPE_MAX];
    char host_name[HALYARD_DISCOVERY_HOST_NAME_MAX];
    char system_version[HALYARD_DISCOVERY_SYSTEM_VERSION_MAX];
    char address[HALYARD_DISCOVERY_ADDRESS_MAX]; /* caller-supplied - see halyard_discovery_parse_response */
    int is_awake; /* 1 = "HTTP/1.1 200 Ok", 0 = "HTTP/1.1 620 Server Standby" (or any other status) */
} halyard_discovered_console;

/*
 * Build the SRCH probe datagram for `profile` into `buf`. Returns the number of bytes written
 * (excluding the implicit NUL, which is also written if `buf_size` allows it), or 0 if `buf` is too
 * small. The probe is identical for every broadcast - build it once per profile and reuse it.
 */
size_t halyard_discovery_build_probe(const halyard_discovery_profile *profile, char *buf, size_t buf_size);

/*
 * Parse one UDP datagram as a SRCH response. Returns 1 and fills `*out` on success, 0 if `data` is not
 * a recognisable SRCH response (wrong status line, no host-id - the one field the spec's own client
 * treats as mandatory).
 *
 * This module has no socket knowledge of its own - `source_address` is a caller-supplied, already
 * formatted dotted-quad string (e.g. from inet_ntoa()), copied verbatim into `out->address`. Pass NULL
 * to leave it empty, which host-side tests do since a hand-written response text has no real sender.
 */
int halyard_discovery_parse_response(const char *data, size_t length, const char *source_address,
                                     halyard_discovered_console *out);

#endif /* HALYARD_DISCOVERY_H */
