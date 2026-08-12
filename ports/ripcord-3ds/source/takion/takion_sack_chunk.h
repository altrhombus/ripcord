/*
 * ripcord-3ds - Phase 5 Takion transport: the SACK chunk.
 *
 * Ported from Ripcord.Protocol.Halyard.Takion.TakionSackChunk - real SCTP's SACK chunk (RFC 4960),
 * unmodified. This port only ever builds the cumulative-only form (no gap-ack or duplicate-TSN blocks),
 * matching "minimal in-order + timeout-retransmit of DATA/SACK is sufficient" for a clean LAN
 * (docs/protocol/ps5-remoteplay-v1-spec.md sec8.2) - gap/dup blocks were never observed on a clean-LAN
 * capture either. Parsing tolerates a peer sending them (skips over the bytes, reports the counts) since
 * nothing about receiving a SACK should assume the sender made the same simplifying choice.
 *
 * WIRE FORMAT, confirmed byte-for-byte against two captured SACK chunks in tests/takion_test.c:
 *   offset 0, 1 byte : chunk type = TAKION_CHUNK_SACK (0x03)
 *   offset 1, 1 byte : flags, always 0
 *   offset 2, 2 bytes: chunk length, big-endian, = 4 + the value length below
 *   offset 4, 4 bytes: cumulative TSN ack - every chunk up to and including this TSN is acknowledged
 *   offset 8, 4 bytes: advertised receive window (a_rwnd)
 *   offset 12,2 bytes: number of gap-ack blocks that follow (0 in every capture seen)
 *   offset 14,2 bytes: number of duplicate-TSN entries that follow (0 in every capture seen)
 *   offset 16        : gap-ack blocks (4 bytes each: start offset u16 BE, end offset u16 BE, both
 *                      relative to the cumulative TSN ack), then duplicate TSNs (4 bytes each, u32 BE)
 */
#ifndef TAKION_SACK_CHUNK_H
#define TAKION_SACK_CHUNK_H

#include <stddef.h>
#include <stdint.h>

typedef struct {
    uint32_t cumulative_tsn_ack;
    uint32_t a_rwnd;
    unsigned gap_ack_block_count;
    unsigned dup_tsn_count;
} takion_sack_info;

/* Builds a cumulative-only SACK chunk (no gap-ack or duplicate-TSN blocks). Returns the bytes written,
 * or 0 if buf_size is too small. */
size_t takion_sack_build(uint32_t cumulative_tsn_ack, uint32_t a_rwnd, uint8_t *buf, size_t buf_size);

/*
 * Parses a SACK chunk of any shape (cumulative-only or with gap/dup blocks - this port does not
 * interpret the individual blocks, only reports how many were present). Returns 1 and fills *out on
 * success, 0 if data is not a well-formed SACK chunk.
 */
int takion_sack_parse(const uint8_t *data, size_t length, takion_sack_info *out);

#endif /* TAKION_SACK_CHUNK_H */
