/*
 * libripcord - a minimal STUN client: send a Binding Request, read back this host's reflexive address.
 *
 * Ported from src/Ripcord.Core.Net/Stun/StunMessage.cs and StunClient.cs, which remain the reference. It
 * is public RFC material (RFC 3489, RFC 5389, with RFC 5769's sample vectors as ground truth) and nothing
 * console-specific: the one value it produces is the public ip:port a NAT assigns to a local socket,
 * which is what a distant console must be told to send to. The LAN path needs none of this.
 *
 * TWO LAYERS, IN TWO TRANSLATION UNITS, declared together here:
 *
 *   rc_stun.c         the message layer. Builds a request into a caller buffer, parses a response. No
 *                     sockets, no clock, no randomness, so the host suite and the fuzzer reach it with
 *                     nothing else linked.
 *   rc_stun_client.c  the socket layer. Sends on a caller's socket and waits for the matching answer.
 *
 * The split is the same one halyard_control_arm.c / halyard_control_probe.c make, for a reason specific
 * to how the fuzz build links: the harnesses link against an archive of the whole core, and an object is
 * pulled in whole. A parser sharing a file with a function that calls rc_random_bytes() would drag in a
 * CSPRNG the host deliberately does not provide (tests/rc_platform_host.c says why), and the fuzz link
 * would fail for a reason that has nothing to do with parsing.
 *
 * WHAT THIS IS NOT. Only what a client needs to learn its own reflexive address: no attributes are sent,
 * none are authenticated. The vendor's own STUN endpoints expect USERNAME + MESSAGE-INTEGRITY and belong
 * to its relay tier; this targets ordinary public servers, and is lenient reading their answers - an
 * unknown or integrity-bearing attribute is skipped, never rejected. IPv4 transport only, like the rest
 * of the stack, although an IPv6 address inside a response is decoded rather than dropped (see
 * rc_stun_address), because the .NET side decodes it too.
 */
#ifndef RC_STUN_H
#define RC_STUN_H

#include <stddef.h>
#include <stdint.h>

/* Declared, not included: the message layer needs no socket header, and a caller of it should not have
 * to pull one in. Only rc_stun_gather takes one, by pointer. */
struct sockaddr_in;

#define RC_STUN_HEADER_SIZE 20
#define RC_STUN_MAGIC_COOKIE 0x2112A442u

/*
 * THE TRANSACTION ID COMES IN TWO SIZES, which are two framings of the same 16 header bytes.
 *
 * RFC 5389 splits bytes 4..20 into the fixed magic cookie and a 12-byte id; that is what the .NET side
 * sends, and what every modern server echoes. RFC 3489 calls all 16 bytes the transaction id and has no
 * cookie at all. Accepting both costs one branch: given 12 bytes, the builder writes the cookie and then
 * the id; given 16, it writes them verbatim. The .NET side implements only the 12-byte form - the 16-byte
 * one exists here for a server that predates the cookie, and a caller that wants one should pass 16.
 */
#define RC_STUN_TRANSACTION_ID_SIZE 12
#define RC_STUN_CLASSIC_TRANSACTION_ID_SIZE 16

#define RC_STUN_BINDING_REQUEST 0x0001u
#define RC_STUN_BINDING_SUCCESS 0x0101u
#define RC_STUN_BINDING_ERROR 0x0111u

#define RC_STUN_FAMILY_IPV4 0x01u
#define RC_STUN_FAMILY_IPV6 0x02u

/*
 * A reflexive endpoint. `address` is in network byte order - the first 4 bytes for IPv4, all 16 for
 * IPv6 - so it copies straight into a sockaddr's address field. `port` is in HOST order, because the one
 * thing a caller does with it is print it or pass it to htons(), and a number already swapped once is the
 * classic way to end up swapping it twice.
 */
typedef struct {
    uint8_t family; /* RC_STUN_FAMILY_IPV4 or RC_STUN_FAMILY_IPV6 */
    uint16_t port;
    uint8_t address[16];
} rc_stun_address;

/* A parsed message: StunMessage's three properties. */
typedef struct {
    uint16_t type;
    uint8_t transaction_id[RC_STUN_TRANSACTION_ID_SIZE];
    int has_mapped_address;
    rc_stun_address mapped_address;
} rc_stun_message;

/* ---- the message layer (rc_stun.c) ---- */

/*
 * Writes a 20-byte Binding Request, with no attributes, into `out`. `transaction_id` must be 12 or 16
 * bytes (see above). Returns the length written - always RC_STUN_HEADER_SIZE - or 0 for a NULL argument,
 * any other id length, or a buffer too small, so a caller never sends a truncated datagram.
 *
 * The id must be unpredictable to anything off-path: it is the only thing tying a datagram to this
 * request, on a socket that is also about to carry the session. rc_stun_gather draws it from the CSPRNG.
 */
size_t rc_stun_build_binding_request(const uint8_t *transaction_id, size_t transaction_id_len,
                                     uint8_t *out, size_t out_size);

/*
 * StunMessage.TryParse: reads any RFC 5389 message. Returns 1 and fills `out`, or 0 when `data` is not a
 * STUN message - too short, a type with either top bit set, the wrong cookie, or attributes declared past
 * the end of the datagram. A 0 is not an error: a UDP socket receives whatever the network sends.
 *
 * Deliberately lenient where .NET is: bytes after the declared attribute region are ignored, an attribute
 * running past that region ends the walk (keeping any address already found) rather than failing the
 * message, and an attribute that cannot be read as an address is simply not one. An XOR-MAPPED-ADDRESS
 * (0x0020, or the pre-standard 0x8020) wins over a MAPPED-ADDRESS wherever each appears.
 */
int rc_stun_parse(const uint8_t *data, size_t length, rc_stun_message *out);

typedef enum {
    RC_STUN_RESPONSE_OK = 0,        /* our Binding Success, and *out holds the reflexive address */
    RC_STUN_RESPONSE_NO_ADDRESS,    /* our Binding Success, carrying no address we can read */
    RC_STUN_RESPONSE_NOT_STUN,      /* not a STUN message at all */
    RC_STUN_RESPONSE_NOT_SUCCESS,   /* STUN, but not a Binding Success (an error response, a request) */
    RC_STUN_RESPONSE_MISMATCH,      /* a Binding Success for some other transaction */
    RC_STUN_RESPONSE_BAD_ARGUMENT   /* NULL pointer, or a transaction id that is not 12 or 16 bytes */
} rc_stun_response_status;

/*
 * The question the socket layer actually asks of each datagram: is this the answer to MY request, and
 * what did it say? `transaction_id` is the one the request was built with, 12 or 16 bytes.
 *
 * With 12 bytes this is exactly StunClient's test - rc_stun_parse, then type == Binding Success, then
 * the id. With 16 the cookie check is replaced by comparing all 16 header bytes, which is what makes it
 * RFC 3489's test instead; for an id whose first four bytes are the cookie the two agree.
 *
 * `*out` is written only on RC_STUN_RESPONSE_OK.
 */
rc_stun_response_status rc_stun_parse_binding_response(const uint8_t *data, size_t length,
                                                       const uint8_t *transaction_id,
                                                       size_t transaction_id_len, rc_stun_address *out);

/* ---- the socket layer (rc_stun_client.c) ---- */

/*
 * StunClient's defaults: 500 ms per attempt and three attempts per server. Short, because a signaling
 * exchange should not stall on a slow server when another will answer; retried, because UDP to a public
 * server drops the occasional datagram.
 */
#define RC_STUN_DEFAULT_TIMEOUT_MS 500u
#define RC_STUN_DEFAULT_ATTEMPTS 3u

typedef enum {
    RC_STUN_GATHER_OK = 0,       /* *out_reflexive and *out_server_index are set */
    RC_STUN_GATHER_NO_ANSWER,    /* every attempt on every server ran out; a normal outcome */
    RC_STUN_GATHER_BAD_ARGUMENT,
    RC_STUN_GATHER_NO_ENTROPY,   /* rc_random_bytes failed; no request was sent with a guessable id */
    RC_STUN_GATHER_SOCKET_ERROR  /* the socket could not be made non-blocking, or a send failed */
} rc_stun_gather_status;

/*
 * StunClient.GatherAsync: asks each server in turn, each up to `attempts_per_server` times (0 is taken as
 * 1, as .NET clamps it), with a fresh transaction id per attempt, and returns the first answer.
 *
 * THE SOCKET IS THE CALLER'S, AND MUST BE THE ONE THAT CARRIES THE TRAFFIC. A NAT maps per source port, so
 * a reflexive address is meaningful only for the socket it was gathered on. The control association and
 * the A/V connection are separate sockets and therefore separate mappings, and each needs its own call
 * here - one gathered answer cannot be reused for the other.
 *
 * The socket must already be bound, is not connect()ed (requests go out with sendto), and is left open.
 * It is put into non-blocking mode with rc_socket_set_nonblocking if it is not already, because a
 * blocking socket here is not a failure but a hang - the deadline is never evaluated - and everything
 * that later uses this socket in the core wants it non-blocking anyway.
 *
 * Anything else arriving while an attempt waits - a stale answer to an earlier attempt, a datagram from
 * the console - is read and discarded, exactly as .NET does. Gather before the session starts talking.
 *
 * `servers` are IPv4 addresses in network order, as rc_udp_open fills them. Name resolution is the
 * caller's: the core resolves nothing, and StunClient.DefaultServers are DNS names.
 *
 * Blocks for at most server_count * attempts * per_attempt_timeout_ms (plus a poll interval per attempt).
 * There is no cancellation token; that bound is what stands in for one.
 */
rc_stun_gather_status rc_stun_gather(int sock, const struct sockaddr_in *servers, size_t server_count,
                                     unsigned attempts_per_server, uint32_t per_attempt_timeout_ms,
                                     rc_stun_address *out_reflexive, size_t *out_server_index);

#endif /* RC_STUN_H */
