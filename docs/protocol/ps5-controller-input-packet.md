# PS5 Remote Play — controller input packet (tentative)

Status: draft. The header shape below was derived from 5 packets in the original capture session -
enough to see a consistent shape, not enough to be confident about every field's exact meaning. A
later, much larger sample (~660 packets from a full WAN session, see `ps5-wan-relay.md`) confirmed
the overall channel/shape but corrected one field (see below) and did not attempt to re-derive the
rest byte-by-byte. The originally-requested corroboration - a capture with deliberate, documented
button presses - has now been done, with a **negative** result for traffic-pattern analysis: see
"What's still needed." An implementation still cannot rely on the exact field meanings below
without payload decryption.

Source: own packet capture, same session as the transport/establishment docs. Client→console UDP
traffic on port 9297 (the high-volume stream port - see `ps5-session-transport.md` and the fuller
`ps5-av-stream.md`) forms a distinct size class of 66-byte UDP payloads (108 bytes on the wire
after the 42-byte Ethernet/IPv4/UDP prefix), ~130 packets in the captured session. Given the
capture session included moving the controller stick and pressing buttons after connecting, this
size class is the leading candidate for the controller-input packet. Not yet confirmed by
correlating specific button presses to specific packets/bytes - that needs a follow-up capture
with a documented sequence of "press this button now" annotations.

**Correction / context (2026-07-10 deep-dive pass)**: an earlier draft of this document gave the
size as "74 bytes on the wire" — that was an arithmetic slip; 66 bytes of UDP payload is 108 bytes
on the wire, not 74. The 66-byte-payload figure was correct. The same pass also established that
this packet is **not** a standalone format: it is channel `0x0e` of the multiplexed 9297 stream
framing documented in `ps5-av-stream.md`, and its header below is the common stream header (channel/
counter/tag/sequence) plus input-specific fields. Offsets below are unchanged and were
re-verified.

**Further correction (from a large-sample WAN capture, ~660 channel-`0x0e` packets across a full
~200 s session, vs. the 5-packet sample below)**: the field documented below as offset 0, "2 bytes,
`0E 00` constant in all 5" is only constant over a short window. Over a full session, the second of
those two bytes (offset 1) turns out to **increment very slowly** - from `0x00` at session start to
`0x0a` roughly 180 seconds later, about once every 16-18 seconds - and was never observed to wrap
back to `0x00`. Five consecutive packets (spanning well under a second) simply aren't enough
packets to see this change. Read as a coarse, session-relative counter (e.g. minutes-elapsed, or a
long keepalive-interval tick) rather than either a fixed sub-type marker or a fast rotating tag.
The offset-2 field below (the "05, 05, 06, 07, 07" counter) is a **separate, faster-moving** field -
in the WAN sample, the equivalent byte position advanced by roughly 4 per packet (packets arriving
about every 270 ms), too fast to be the same field as offset 1. The table below has not been
re-derived from the WAN sample byte-by-byte beyond this - offsets 3 onward are unverified against
the new data and should still be treated as tentative.

**Redaction note**: the encrypted-payload region is real capture data reproduced only as byte
*counts*/entropy observations, never verbatim - see the note in `ps5-session-establishment.md` for
why (this region is very likely the actual controller state once decrypted, which is a smaller
concern than session keys, but there's no reason to reproduce raw captured bytes verbatim either).

## Observed header (UDP payload, before the encrypted region)

Computed directly from the hex dump (Ethernet 14 bytes + IPv4 20 bytes + UDP 8 bytes = 42-byte
frame prefix before the UDP payload begins), across 5 consecutive packets in the capture:

| Offset (in UDP payload) | Size | Observed values across 5 packets | Read as |
|---|---|---|---|
| 0 | 2 bytes | `0E 00` in all 5 | Byte 0 = channel `0x0e` (controller input), constant. Byte 1 looked constant in this 5-packet sample but is **not** - see the large-sample correction above; it is a slow session-relative counter, not a fixed sub-flags value. |
| 2 | 1 byte | `05, 05, 06, 07, 07` | Slowly-incrementing counter, unclear cadence relative to the field below |
| 3 | 2 bytes | Different every packet, no visible pattern | Possibly a partial checksum/MAC fragment, or the start of the encrypted region - not resolved |
| 5 | 4 bytes | `00000001, 00000002, 00000003, 00000004, 00000006` (big-endian) | Sequence number - monotonic, one gap observed (5 missing = one packet not captured or not sent) |
| 9 | 4 bytes | Different every packet, no visible pattern | Likely a MAC/IV fragment covering the payload |
| 13 | 4 bytes | `000000D0, 00000120, 00000170, 000001C0, 00000240` (big-endian) | Increments by a roughly-constant ~80 (0x50) per sequence step for consecutive sequence numbers, but the one observed 2-sequence-number gap only produced a ~128 (0x80) jump rather than ~160 - reads more like an independent, real-time-following counter (e.g. a timestamp at a fixed tick rate) than a value strictly derived from the sequence number. Tentative. |
| 17 | 4 bytes | `01 00 00 01` in all 5 | Constant flags/sub-type field for this packet class |
| 21 | 45 bytes | High entropy, changes completely every packet | Consistent with an encrypted+authenticated controller-state payload (buttons/sticks/triggers, encrypted using the session key established during `ps5-session-establishment.md`, likely with the sequence number and/or timestamp field above contributing to the AEAD nonce) |

## What's still needed

- ~~Confirm this class really is controller input by capturing with known, deliberate button
  presses at known times and checking size/timing correlation.~~ **Done, with a negative result**:
  a capture with ~30 documented, deliberate button/stick/system-button actions showed the input
  channel running at a constant rate and constant 66-byte payload size throughout, active or idle
  - no timing or size signal correlates to specific presses. See `ps5-wan-relay.md` for the full
  writeup. **Confirming the actual button-to-byte mapping now requires payload decryption**, not
  further traffic-pattern capture.
- Resolve the offset-2 fast counter (confirmed distinct from, and faster-moving than, the offset-1
  slow counter - see the large-sample correction above) and offset-3 unpredictable 2 bytes -
  possibly a checksum that would validate once the packet-type/length framing is fully understood.
- Re-derive this table's offsets 2 onward against a large sample (the WAN capture's ~660 packets),
  the way offsets 0-1 were - only offsets 0-1 have been checked against large-sample data so far.
- A/V stream packet framing (the much larger, more numerous console→client packets on the same
  port) - now covered in `ps5-av-stream.md`.
- Console→client acknowledgment/feedback packets for input: the up-direction feedback channels
  (channel `0x00` + sub-flags, and the small per-channel ACKs) are catalogued in `ps5-av-stream.md`
  but their formats are not yet decoded.
