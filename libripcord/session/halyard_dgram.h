/*
 * libripcord - the account route's datagram control transport (UDP 9303): framing and association.
 *
 * WHAT THIS IS. A console reached through the cloud rendezvous - the account ("no-PIN") route, and so
 * every internet session - does not serve its control plane on TCP 9295. It serves /sess/rgst,
 * /sess/init, /sess/ctrl and then the persistent binary control frames over UDP 9303, in a light
 * reliable-datagram framing that is NOT Takion and shares no bytes with it. The same 88-byte prelude
 * also opens the A/V leg on 9297 before its first Takion byte, where it doubles as the NAT hole-punch.
 * docs/protocol/ps5-session-transport.md, "The account-route control transport", is the spec.
 *
 * Ported from src/Ripcord.Protocol.Halyard.Common/Control/, which remains the reference:
 *
 *   HalyardControlPrelude.cs      -> halyard_dgram_prelude_*       (halyard_dgram_wire.c)
 *   HalyardControlChunk.cs        -> halyard_dgram_chunk_*         (halyard_dgram_wire.c)
 *   HalyardControlAssociation.cs  -> halyard_dgram_assoc_*         (halyard_dgram_assoc.c)
 *
 * plus HalyardDatagramControlChannel.IsCompleteHttpMessage, which is pure and so lives here too.
 *
 * TWO TRANSLATION UNITS, NEITHER WITH I/O. No sockets, no clock, and no CSPRNG: the association draws
 * its tags, tokens and sequences through a callback the caller supplies, exactly as the .NET type takes
 * a Func<int, byte[]>. That keeps it reachable by the fuzzer with nothing else linked (the reason
 * rc_stun.c and rc_stun_client.c are split, see rc_stun.h), and it is what lets the host suite replay
 * the .NET association's own transcript byte for byte with the same counting "random" source. The
 * socket pump is halyard_dgram_channel.h.
 *
 * NO ALLOCATION. The .NET types return fresh arrays and lists; here a parsed chunk BORROWS its body from
 * the caller's datagram, and the association emits through callbacks into one scratch buffer it owns.
 */
#ifndef HALYARD_DGRAM_H
#define HALYARD_DGRAM_H

#include <stddef.h>
#include <stdint.h>

/* ==== the 88-byte prelude (HalyardControlPrelude) ==================================================== */

#define HALYARD_DGRAM_PRELUDE_LENGTH 88
#define HALYARD_DGRAM_HASHED_ID_LENGTH 20 /* a localHashedId, the same 20 bytes the signaling OFFER carries */
#define HALYARD_DGRAM_PRELUDE_TAIL_LENGTH 8

#define HALYARD_DGRAM_PRELUDE_INIT 6u        /* opens the exchange; sent by both sides */
#define HALYARD_DGRAM_PRELUDE_COOKIE_ECHO 7u /* the reply once a side holds the peer's token */

/*
 * One prelude datagram. Every field the .NET record has, fixed-size. The type is the protocol's only
 * little-endian field; every other one is big-endian on the wire and host-order here.
 *
 *   tag_pair      two BE16 halves. The opener sends (1, X); the answer carries them SWAPPED.
 *                 [X] their meaning past that is unconfirmed.
 *   request_word  nonzero only in the opener's Init. [X] - see HALYARD_DGRAM_INITIATOR_REQUEST_WORD.
 *   token         in an Init, the sender's own; in a CookieEcho, the peer's returned verbatim. Near-
 *                 certainly a microsecond timestamp (the spec says why); [X] ours is a fixed random.
 *   tail          zero in an Init; in a CookieEcho, the peer's endpoint as this side sees it - build it
 *                 with halyard_dgram_reflect_peer_endpoint, never by hand.
 */
typedef struct {
    uint32_t type;
    uint8_t sender_id[HALYARD_DGRAM_HASHED_ID_LENGTH];
    uint8_t peer_id[HALYARD_DGRAM_HASHED_ID_LENGTH];
    uint32_t tag_pair;
    uint32_t request_word;
    uint32_t token;
    uint8_t tail[HALYARD_DGRAM_PRELUDE_TAIL_LENGTH];
} halyard_dgram_prelude;

/* Serializes into `out`. Returns HALYARD_DGRAM_PRELUDE_LENGTH, or 0 for NULL or a buffer too small. */
size_t halyard_dgram_prelude_write(const halyard_dgram_prelude *prelude, uint8_t *out, size_t out_size);

/*
 * HalyardControlPrelude.TryParse: 1 for an 88-byte datagram of type 6 or 7, else 0. The prelude and the
 * chunk layer share a port, and a chunk can be exactly 88 bytes too, so this is how a reader tells them
 * apart: a chunk always starts with nonzero high bits where this has a small little-endian type.
 */
int halyard_dgram_prelude_parse(const uint8_t *data, size_t length, halyard_dgram_prelude *out);

/* The tag pair with its halves exchanged - the form the peer answers with. */
uint32_t halyard_dgram_swap_halves(uint32_t tag_pair);

/*
 * HalyardControlPrelude.ReflectPeerEndpoint: a CookieEcho's tail, STUN's XOR-MAPPED-ADDRESS trick keyed
 * on the tag pair of the same datagram:
 *
 *     tail[0..4] = peer IPv4 (network order) XOR tag_pair (big-endian)
 *     tail[4..6] = peer port (big-endian)    XOR tag_pair >> 16
 *     tail[6..8] = zero
 *
 * [C] against every echo in every capture the project holds, both directions. `peer_address` is 4 bytes
 * in network order; `peer_port` is host order.
 */
void halyard_dgram_reflect_peer_endpoint(const uint8_t peer_address[4], uint16_t peer_port,
                                         uint32_t tag_pair, uint8_t tail[HALYARD_DGRAM_PRELUDE_TAIL_LENGTH]);

/* ==== the chunk layer (HalyardControlChunk / HalyardControlChunkCodec) ================================ */

/*
 * Observed chunk types. [X] These are COMBINATIONS of a flags bitmap, not an enumeration - see the .NET
 * enum and the spec's "The type byte is a flags bitmap". A combination not listed reaches the
 * association as an unknown type and is reported, never guessed at.
 */
#define HALYARD_DGRAM_CHUNK_DATA 0x02u
#define HALYARD_DGRAM_CHUNK_DATA_RETRANSMIT 0x12u
#define HALYARD_DGRAM_CHUNK_ACK 0x20u
#define HALYARD_DGRAM_CHUNK_ACK_EXTENDED 0x24u /* two trailing bytes, [X] unexplained */
#define HALYARD_DGRAM_CHUNK_HELLO 0x80u
#define HALYARD_DGRAM_CHUNK_HELLO_ECHO 0x90u
#define HALYARD_DGRAM_CHUNK_ACCEPT 0xA0u
#define HALYARD_DGRAM_CHUNK_CLOSE 0xC0u
#define HALYARD_DGRAM_CHUNK_COOKIE 0xD0u

/* The service port the port words carry - 9295, the control port. A required on-wire value. */
#define HALYARD_DGRAM_CONTROL_PORT 0x244Fu

/* The header plus a (source, destination) port pair: what every chunk in every capture carries. */
#define HALYARD_DGRAM_PAIRED_WORD_COUNT 3u
#define HALYARD_DGRAM_TYPE_AND_FLAGS_LENGTH 2u

/* The length field is 11 bits, so a chunk is at most this long INCLUDING its header. */
#define HALYARD_DGRAM_MAX_CHUNK_LENGTH 0x7FFu

/*
 * One chunk, as read. `body` points INTO the datagram it was read from (the .NET record copies it), so
 * it is valid exactly as long as that buffer is. `word_count` is the 2-bit count including the header
 * word: 1, 2 or 3. `source_port`/`destination_port` are the control port when the shape carries none;
 * for a count of 2 the one word is both.
 */
typedef struct {
    uint8_t type;
    uint8_t flags;
    const uint8_t *body;
    size_t body_length;
    uint16_t source_port;
    uint16_t destination_port;
    uint8_t word_count;
} halyard_dgram_chunk;

/* How many bytes a chunk with `body_length` of body needs at `word_count` (not range-checked). */
size_t halyard_dgram_chunk_length(size_t body_length, unsigned word_count);

/*
 * HalyardControlChunkCodec.Write: writes one chunk and returns its length, or 0 where .NET throws - a
 * word count outside 1..3, a chunk longer than HALYARD_DGRAM_MAX_CHUNK_LENGTH, or a buffer too small.
 * Every port word written is the control port. `body` may be NULL when `body_length` is 0.
 */
size_t halyard_dgram_chunk_write(uint8_t *out, size_t out_size, uint8_t type, uint8_t flags,
                                 const uint8_t *body, size_t body_length, unsigned word_count);

/*
 * HalyardControlChunkCodec.TryRead: reads the chunk at the start of `data`. Returns 1 with `*out` and
 * `*consumed` set, or 0 for anything the vendor's own receive loop abandons a datagram on: shorter than
 * the header, a zero word count, a length that cannot hold its own prefix plus type and flags, or one
 * that overruns the datagram. Bits 13..11 of the header are masked away, not checked, as the receiver
 * does. The port words are read through, not required to be the control port.
 */
int halyard_dgram_chunk_read(const uint8_t *data, size_t length, halyard_dgram_chunk *out, size_t *consumed);

/*
 * HalyardControlChunkCodec.ReadAll, as an iterator (no list to allocate). Start with *offset = 0; each
 * call returns 1 with the next chunk and advances *offset, or 0 at the end OR at the first malformed
 * chunk - whatever parsed before it stands, which is what the vendor's loop does and what .NET does.
 */
int halyard_dgram_chunk_next(const uint8_t *data, size_t length, size_t *offset, halyard_dgram_chunk *out);

/* ==== HTTP completeness (HalyardDatagramControlChannel.IsCompleteHttpMessage) ========================= */

/*
 * Whether an HTTP message is complete, judged by Content-Length against what follows the blank line - a
 * datagram boundary says nothing about a message boundary. 0 until the header terminator has arrived;
 * then 1 if no Content-Length header parses as an integer, else whether the body has reached it.
 * Matches .NET's reading exactly, down to the first PARSEABLE Content-Length winning and a line whose
 * value does not parse being skipped.
 */
int halyard_dgram_http_complete(const uint8_t *message, size_t length);

/* ==== the association (HalyardControlAssociation) ===================================================== */

/*
 * HOW FAR IT HAS GOT. The same five phases as the .NET enum, in the same order.
 */
typedef enum {
    HALYARD_DGRAM_PHASE_IDLE = 0,    /* nothing sent or received yet */
    HALYARD_DGRAM_PHASE_HANDSHAKING, /* prelude in progress */
    HALYARD_DGRAM_PHASE_ESTABLISHED, /* prelude complete in both directions */
    HALYARD_DGRAM_PHASE_CONNECTED,   /* a chunk-layer connection is open */
    HALYARD_DGRAM_PHASE_CLOSED       /* the connection was torn down (the association survives it) */
} halyard_dgram_phase;

/*
 * How a chunk we originate addresses the peer - the header's word count, named for what it selects. The
 * receiver looks a connection up by the LAST port word, and a chunk with none is matched on the peer
 * address alone, so this is a real protocol choice. PORT_PAIR is what every capture carries; the other
 * two are accepted by the receiver and never observed - PEER_ADDRESS_ONLY is an [X] experiment kept
 * because the .NET side keeps it.
 */
typedef enum {
    HALYARD_DGRAM_ADDRESS_PEER_ONLY = 1,
    HALYARD_DGRAM_ADDRESS_SINGLE_PORT = 2,
    HALYARD_DGRAM_ADDRESS_PORT_PAIR = 3
} halyard_dgram_addressing;

typedef enum {
    HALYARD_DGRAM_EVENT_PRELUDE_ESTABLISHED = 0,
    HALYARD_DGRAM_EVENT_CONNECTION_OPENED, /* opened_by_peer says which side opened it */
    HALYARD_DGRAM_EVENT_DATA_RECEIVED,     /* data/data_length: EVERYTHING accumulated since the last clear */
    HALYARD_DGRAM_EVENT_PEER_CLOSED,
    HALYARD_DGRAM_EVENT_UNHANDLED          /* reason + the whole datagram; reported, never silently dropped */
} halyard_dgram_event_kind;

/*
 * Something the association observed. Every pointer is valid for the duration of the callback only.
 *
 * DATA_RECEIVED reports the whole accumulation, as .NET's DataReceived(Payload) does, because a caller
 * waiting for one complete HTTP response wants to test all of it; `delta`/`delta_length` is the part
 * this chunk added, which .NET does not report and C callers want because they have no cheap snapshot.
 *
 * UNHANDLED carries a fixed English `reason` rather than .NET's formatted string; `chunk_type` names the
 * type for the unknown-type case (and is 0 otherwise), which is the one thing .NET put in its text.
 */
typedef struct {
    halyard_dgram_event_kind kind;
    int opened_by_peer;
    const uint8_t *data;
    size_t data_length;
    const uint8_t *delta;
    size_t delta_length;
    const char *reason;
    uint8_t chunk_type;
} halyard_dgram_event;

/*
 * WHAT THE ASSOCIATION ASKS OF ITS CALLER, as callbacks rather than a returned action list, because a
 * list of datagrams is an allocation and this core makes none. Where .NET returns
 * HalyardControlAction(Send, Events), C calls send() once per datagram and event() once per event, in
 * the order they arise. The sets are identical; only the interleaving is observable, and nothing in the
 * protocol depends on it.
 *
 *   send    put one datagram on the wire. No return value: a failed send is the transport's problem to
 *           record (halyard_dgram_channel does), and the association's state must not depend on it any
 *           more than the .NET one's does.
 *   event   optional (NULL ignores events). It MAY call halyard_dgram_assoc_clear_inbound; it must not
 *           call back into the association otherwise.
 *   random  fill `out` with `length` unpredictable bytes; 1 on success, 0 on failure. Production passes
 *           rc_random_bytes; a test passes a counter so a whole exchange is reproducible.
 */
typedef struct {
    void (*send)(void *ctx, const uint8_t *datagram, size_t length);
    void (*event)(void *ctx, const halyard_dgram_event *event);
    int (*random)(void *ctx, uint8_t *out, size_t length);
    void *ctx;
} halyard_dgram_callbacks;

typedef enum {
    HALYARD_DGRAM_OK = 0,           /* done - which may mean "nothing to do", exactly where .NET returns None */
    HALYARD_DGRAM_NO_ENTROPY,       /* the random callback failed; nothing was sent and no state changed */
    HALYARD_DGRAM_NOT_CONNECTED,    /* send() with no open connection; nothing was sent */
    HALYARD_DGRAM_TOO_LONG,         /* a payload that would not fit one chunk; nothing was sent */
    HALYARD_DGRAM_BAD_ARGUMENT
} halyard_dgram_status;

/*
 * The largest payload one send() carries: one data chunk at the paired word count, minus its sequence.
 * .NET throws for more (from the chunk encoder) and so does not fragment either; a caller with more to
 * say must split it, which no request in this protocol has needed - the rgst POST is 744 bytes.
 */
#define HALYARD_DGRAM_MAX_PAYLOAD (HALYARD_DGRAM_MAX_CHUNK_LENGTH - 2u * HALYARD_DGRAM_PAIRED_WORD_COUNT \
                                   - HALYARD_DGRAM_TYPE_AND_FLAGS_LENGTH - 2u)

/* The largest datagram the association ever emits: a 12-byte ack chunk (prefix, type and flags, then a
 * sequence pair) in front of a full data chunk. */
#define HALYARD_DGRAM_ACK_CHUNK_LENGTH 12u
#define HALYARD_DGRAM_MAX_DATAGRAM (HALYARD_DGRAM_ACK_CHUNK_LENGTH + HALYARD_DGRAM_MAX_CHUNK_LENGTH)

/*
 * How much received payload the association will hold before the caller drains it. .NET's is an
 * unbounded List<byte>. A response larger than this is not delivered and not acknowledged - it is
 * reported as UNHANDLED, and the peer retransmits once the caller has cleared room - so the bound costs
 * a retransmission, never a silently truncated message. The largest thing this transport carries is the
 * encrypted pairing record in the rgst response, well under this.
 */
#define HALYARD_DGRAM_INBOUND_MAX 16384u

/*
 * [X] The request word the opening side sends. NOT a constant on the wire - the spec surveys the values
 * real clients send (a small counter that grows over a client's lifetime) and records that 0x40 appears
 * in no capture at all. It is 0x40 here because it is 0x40 in HalyardControlAssociation, and a port
 * that "fixed" it silently would stop being checkable against the reference. Change both together.
 */
#define HALYARD_DGRAM_INITIATOR_REQUEST_WORD 0x40u

typedef struct {
    halyard_dgram_callbacks cb;
    uint8_t local_id[HALYARD_DGRAM_HASHED_ID_LENGTH];
    uint8_t peer_id[HALYARD_DGRAM_HASHED_ID_LENGTH];
    uint8_t peer_address[4]; /* network order: what a CookieEcho reflects back */
    uint16_t peer_port;

    halyard_dgram_phase phase;
    uint32_t tag_pair;
    uint32_t our_token;
    int we_sent_echo;
    int peer_echoed;

    int have_hello;                       /* .NET: _helloBody is not null */
    uint8_t hello_body[14];               /* seq(2) | capability(6) | connection tag(4) | window(2) */
    halyard_dgram_addressing hello_addressing;
    uint16_t sequence;
    uint16_t peer_sequence;
    int delivered;                        /* whether peer_sequence yet names a payload we took */

    size_t inbound_length;
    uint8_t inbound[HALYARD_DGRAM_INBOUND_MAX];
    uint8_t scratch[HALYARD_DGRAM_MAX_DATAGRAM];
} halyard_dgram_assoc;

/*
 * The constructor. `peer_address` is the peer's IPv4 address in network order - the one we send to -
 * and `peer_port` its UDP port; both are needed as data because an echo must reflect them. Returns
 * BAD_ARGUMENT for a NULL pointer or a callbacks struct without send/random. (.NET's IPv4-only check is
 * the fixed 4-byte parameter here.)
 */
halyard_dgram_status halyard_dgram_assoc_init(halyard_dgram_assoc *assoc, const halyard_dgram_callbacks *callbacks,
                                              const uint8_t local_id[HALYARD_DGRAM_HASHED_ID_LENGTH],
                                              const uint8_t peer_id[HALYARD_DGRAM_HASHED_ID_LENGTH],
                                              const uint8_t peer_address[4], uint16_t peer_port);

/* Open the prelude ourselves. A no-op once anything has happened, as in .NET. */
halyard_dgram_status halyard_dgram_assoc_open(halyard_dgram_assoc *assoc);

/* Re-send what the current phase waits on: our Init, while handshaking and not yet echoing. */
halyard_dgram_status halyard_dgram_assoc_retry(halyard_dgram_assoc *assoc);

/*
 * Open a chunk-layer connection. Works from ESTABLISHED, and from CONNECTED or CLOSED - the console
 * closes each connection once it has answered, and its teardown of the last one is often still in
 * flight when the next is wanted. A no-op before the prelude is established.
 */
halyard_dgram_status halyard_dgram_assoc_open_connection(halyard_dgram_assoc *assoc,
                                                         halyard_dgram_addressing addressing);

/* Re-send the outstanding hello unchanged - a retransmission, not a second connection. */
halyard_dgram_status halyard_dgram_assoc_reopen_connection(halyard_dgram_assoc *assoc);

/*
 * End the open connection with a Close chunk carrying our connection tag. On datagrams nothing says it
 * implicitly, and a console never told keeps the session live and refuses further cloud sessions until
 * rebooted. [X] our Close carries the tag we offered; the console's carries its own.
 */
halyard_dgram_status halyard_dgram_assoc_close_connection(halyard_dgram_assoc *assoc);

/* Send a payload on the open connection: an ack chunk then a data chunk, in one datagram. */
halyard_dgram_status halyard_dgram_assoc_send(halyard_dgram_assoc *assoc, const uint8_t *payload, size_t length);

/*
 * Feed one received datagram; everything the association does is a reaction to this. `data` must stay
 * valid and unmodified for the duration of the call (chunk bodies are borrowed from it).
 */
halyard_dgram_status halyard_dgram_assoc_on_datagram(halyard_dgram_assoc *assoc, const uint8_t *data, size_t length);

/* The received payload accumulated since the last clear. Valid until the next call into the association. */
const uint8_t *halyard_dgram_assoc_inbound(const halyard_dgram_assoc *assoc, size_t *out_length);

/* Forget the accumulated payload (.NET ClearInbound). */
void halyard_dgram_assoc_clear_inbound(halyard_dgram_assoc *assoc);

/* Drop the first `count` bytes of the accumulation, keeping the rest - for a byte-pipe reader. */
void halyard_dgram_assoc_consume_inbound(halyard_dgram_assoc *assoc, size_t count);

/* Human-readable names, for logs. */
const char *halyard_dgram_phase_name(halyard_dgram_phase phase);
const char *halyard_dgram_event_name(halyard_dgram_event_kind kind);

#endif /* HALYARD_DGRAM_H */
