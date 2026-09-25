/*
 * ripcord-3ds - Phase 5 Takion transport: the SCTP 4-way handshake.
 *
 * Ported from Ripcord.Protocol.Halyard.Takion.TakionHandshake. Chunk type values are SCTP's own
 * (RFC 4960), and the handshake is the standard SCTP association setup, client active-open only - this
 * port never needs to answer an INIT, only send one:
 *
 *   client -> server : INIT        payload{ initiate_tag, a_rwnd, out_streams, in_streams, initial_tsn }
 *   server -> client : INIT_ACK    payload{ server tag, a_rwnd, streams, initial_tsn, 32-byte cookie }
 *   client -> server : COOKIE_ECHO echoes the 32-byte cookie verbatim
 *   server -> client : COOKIE_ACK  -> ESTABLISHED (or the server just sends DATA, which is equally
 *                                     valid proof of establishment - some real captures skip an
 *                                     explicit COOKIE_ACK)
 *
 * Each side's "tag" is the SCTP verification tag: a random 32-bit value the OTHER side must echo in
 * every subsequent packet's takion_message_header.verification_tag (see takion_message.h), and it also
 * seeds that direction's initial TSN (`initial_tsn = tag`, confirmed by the INIT vector below).
 *
 * The INIT chunk is confirmed byte-for-byte against a captured vector in tests/takion_test.c.
 * INIT_ACK/COOKIE_ECHO/COOKIE_ACK have no captured vector available - they are built to the same
 * SCTP-mirroring shape the spec describes and verified by round-trip (build then parse) instead.
 */
#ifndef TAKION_HANDSHAKE_H
#define TAKION_HANDSHAKE_H

#include <stddef.h>
#include <stdint.h>

#define TAKION_CHUNK_DATA         0x00u
#define TAKION_CHUNK_INIT         0x01u
#define TAKION_CHUNK_INIT_ACK     0x02u
#define TAKION_CHUNK_SACK         0x03u
#define TAKION_CHUNK_COOKIE_ECHO  0x0au
#define TAKION_CHUNK_COOKIE_ACK   0x0bu

#define TAKION_INIT_A_RWND   0x00019000u /* wire-confirmed constant, every capture */
#define TAKION_INIT_STREAMS  0x0064u     /* wire-confirmed constant (100), both in/out */
#define TAKION_COOKIE_SIZE   32

/* Returns the SCTP chunk type byte at the front of [data, length), or -1 if length is 0. Callers use
 * this to decide which of the parse functions below to try. */
int takion_chunk_type(const uint8_t *data, size_t length);

/* Builds the 20-byte INIT chunk. initial_tsn is always set equal to initiate_tag, matching the vendor
 * wire (confirmed by the vector in tests/takion_test.c). Returns the bytes written, or 0 if too small. */
size_t takion_build_init(uint32_t initiate_tag, uint8_t *buf, size_t buf_size);

/* Parses an INIT chunk (this port never needs to answer one, but tests build-then-parse it). Returns 1
 * and fills *out_initiate_tag on success, 0 if data is not a well-formed INIT chunk. */
int takion_parse_init(const uint8_t *data, size_t length, uint32_t *out_initiate_tag);

/* Builds the INIT_ACK chunk (never sent by this port - only used to synthesize a test peer). Returns
 * the bytes written, or 0 if too small. */
size_t takion_build_init_ack(uint32_t server_tag, uint32_t initial_tsn,
                             const uint8_t cookie[TAKION_COOKIE_SIZE], uint8_t *buf, size_t buf_size);

/* Parses an INIT_ACK chunk, extracting the server's tag, its initial TSN, and the cookie to echo back
 * in COOKIE_ECHO. Returns 1 on success, 0 if data is not a well-formed INIT_ACK chunk. */
int takion_parse_init_ack(const uint8_t *data, size_t length, uint32_t *out_server_tag,
                          uint32_t *out_initial_tsn, uint8_t out_cookie[TAKION_COOKIE_SIZE]);

/* Builds the COOKIE_ECHO chunk, echoing `cookie` verbatim. Returns the bytes written, or 0 if too small. */
size_t takion_build_cookie_echo(const uint8_t cookie[TAKION_COOKIE_SIZE], uint8_t *buf, size_t buf_size);

/* Parses a COOKIE_ECHO chunk (never received by this port - only used to synthesize a test peer).
 * Returns 1 and fills out_cookie on success, 0 otherwise. */
int takion_parse_cookie_echo(const uint8_t *data, size_t length, uint8_t out_cookie[TAKION_COOKIE_SIZE]);

/* Builds the (empty-body) COOKIE_ACK chunk. Returns the bytes written, or 0 if too small. */
size_t takion_build_cookie_ack(uint8_t *buf, size_t buf_size);

/* True if [data, length) is a well-formed COOKIE_ACK chunk. */
int takion_is_cookie_ack(const uint8_t *data, size_t length);

#endif /* TAKION_HANDSHAKE_H */
