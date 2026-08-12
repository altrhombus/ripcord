#!/usr/bin/env python3
"""
Host-side sender for ripcord-3ds's Phase 2 UDP link test (see ../SETUP.md).

Sends fixed-size UDP payloads at a ramp of target rates, in Mbps, holding each for --stage-seconds
before moving to the next. The 3DS-side receiver is source/linktest/main.c - build it with
`make -C ports/ripcord-3ds linktest`, run it via the Homebrew Launcher, and it prints the IP/port to
point this at.

Wire format (this test's own, not a PlayStation protocol - source/linktest/main.c parses the same
layout):
    offset  0, 8 bytes : sequence number, little-endian u64, resets to 0 at the start of each stage
    offset  8, 2 bytes : target rate for this stage in Mbps, little-endian u16; 0xFFFF is the
                         end-of-run marker
    offset 10, 2 bytes : reserved, 0
    offset 12, 4 bytes : this sender's send time in microseconds since its own start, truncated to
                         32 bits, little-endian - unused by the current receiver, kept in case a
                         later capture wants one-way delay instead of receiver-side jitter
    offset 16..1425    : filler, zero-filled

Usage:
    python3 udp_link_test_sender.py <3ds-ip> [--port 9000] [--stage-seconds 5] [--start 1] [--end 12]
"""
import argparse
import socket
import struct
import time

PACKET_SIZE = 1426
HEADER = struct.Struct("<QHHI")  # seq, stage_mbps, reserved, send_time_us (truncated)
PAYLOAD = b"\x00" * (PACKET_SIZE - HEADER.size)
END_MARKER_STAGE = 0xFFFF


def send_stage(sock, addr, stage_mbps, duration_s, clock_start):
    """Pace packets at stage_mbps for duration_s. Returns the number of packets sent."""
    interval = (PACKET_SIZE * 8) / (stage_mbps * 1_000_000)
    seq = 0
    next_send = time.monotonic()
    end_time = next_send + duration_s

    while time.monotonic() < end_time:
        now = time.monotonic()
        if now < next_send:
            time.sleep(next_send - now)

        send_time_us = int((time.monotonic() - clock_start) * 1_000_000) & 0xFFFFFFFF
        packet = HEADER.pack(seq, stage_mbps, 0, send_time_us) + PAYLOAD
        sock.sendto(packet, addr)

        seq += 1
        next_send += interval

    return seq


def send_end_marker(sock, addr, repeats=20, gap_s=0.02):
    """A short burst rather than one packet - UDP has no delivery guarantee, and this marker is the
    only thing that tells the receiver its last stage is really over."""
    for seq in range(repeats):
        packet = HEADER.pack(seq, END_MARKER_STAGE, 0, 0) + PAYLOAD
        sock.sendto(packet, addr)
        time.sleep(gap_s)


def main():
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    parser.add_argument("host", help="3DS IP address, printed on its top screen at startup")
    parser.add_argument("--port", type=int, default=9000)
    parser.add_argument("--stage-seconds", type=float, default=5.0)
    parser.add_argument("--start", type=int, default=1, help="first stage rate, in Mbps")
    parser.add_argument("--end", type=int, default=12, help="last stage rate, in Mbps")
    args = parser.parse_args()

    if not (1 <= args.start <= args.end < 0xFFFF):
        parser.error("expected 1 <= --start <= --end < 65535")

    addr = (args.host, args.port)
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    clock_start = time.monotonic()

    for mbps in range(args.start, args.end + 1):
        print(f"stage {mbps:2d} Mbps for {args.stage_seconds}s ...")
        sent = send_stage(sock, addr, mbps, args.stage_seconds, clock_start)
        print(f"  sent {sent} packets")

    print("sending end-of-run marker")
    send_end_marker(sock, addr)
    print("done - read the stage lines on the 3DS's top screen")


if __name__ == "__main__":
    main()
