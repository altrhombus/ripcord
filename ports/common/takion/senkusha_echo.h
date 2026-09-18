/*
 * ripcord-3ds - the senkusha RTT echo packet.
 *
 * Ported from src/Ripcord.Protocol.Halyard.Takion/SenkushaEchoProbe.cs, which is this project's own
 * reference implementation and carries the provenance for every field below. That file cites
 * docs/protocol/captures/session8-wireshark.pcapng frames 155-175 by number: ten pings, each returned by
 * the console BYTE-IDENTICALLY, in two independent captures.
 *
 * WHY A CLIENT CAN BUILD ONE OF THESE AT ALL. Bytes 22-26 hold a monotonic counter whose deltas matched
 * the capture's own inter-packet timing to within a few microseconds - a send timestamp, not a checksum.
 * Because the console returns the packet unchanged, only WE ever read that field, so any monotonic value
 * is as good as the vendor's. Had it been a checksum over the payload, generating a valid ping would have
 * required reversing it first.
 *
 * RTT is still measured with our own clock across the send/receive pair rather than by reading the echoed
 * timestamp back, for the same reason the .NET side does: the field is a convenience, the stopwatch is
 * the measurement.
 */
#ifndef SENKUSHA_ECHO_H
#define SENKUSHA_ECHO_H

#include <stddef.h>
#include <stdint.h>

/* 548 payload, 556 as a UDP datagram, 576 as an IP datagram - RFC 791's minimum reassembly buffer, and
 * the reason a probe that must survive any path picks this size. */
#define SENKUSHA_ECHO_PAYLOAD 548u

/* IPv4 header 20 + UDP header 8. Confirmed twice at different sizes in one capture: the downstream probe
 * requested 1454 and carried 1426, the upstream requested 1254 and carried 1226. So an mtuReq denotes the
 * whole IP datagram, not the payload. */
#define SENKUSHA_IP_UDP_OVERHEAD 28u

/* Pings the vendor sends before turning echo back off. */
#define SENKUSHA_PING_COUNT 10u

/*
 * Builds one ping into `buf`. Returns the length written, or 0 if the buffer is too small.
 *
 * `padding` fills the tail from offset 27: zero for the RTT ping, 0x47 for the MTU test. That asymmetry
 * is in both captures and looks deliberate - a zero-filled payload is trivially compressible, so a link
 * doing compression could carry it in fewer bytes than requested and an MTU test measured that way would
 * pass at a size the path cannot actually deliver.
 */
size_t senkusha_echo_build(uint8_t sequence, uint64_t microseconds, size_t payload_length,
                           uint8_t padding, uint8_t *buf, size_t buf_size);

/*
 * Is this datagram an echo of one of our pings, and if so which sequence?
 *
 * Matched on the marker bytes and the sequence rather than by comparing the whole packet: the console
 * echoes verbatim today, but keying off the fields we know the meaning of is more robust than depending
 * on every byte we do not.
 */
int senkusha_echo_is_echo(const uint8_t *datagram, size_t length, uint8_t *out_sequence);

#endif /* SENKUSHA_ECHO_H */
