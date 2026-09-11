# PS5 Remote Play — A/V + input stream framing (UDP 9297)

Status: draft, from one capture session (see provenance log). Covers the multiplexed streaming
flow on the console's UDP port 9297. The framing layer (channel, sequence, fragment/frame
indexing) was recovered in the clear; the media/input **payloads are encrypted** and only their
sizes/positions are documented. Field meanings marked "tentative" need a second capture (ideally
with annotated inputs and known content) to confirm.

Source: own packet capture of network traffic on the author's own LAN between a real PS5 and a
connected client, at IP/port level plus the plaintext framing bytes. Same session as the transport/
establishment specs. Capture and this document written 2026-07-10. No source code from any existing
Remote Play client project was consulted — see `docs/protocol-research-log.md`.

**Redaction note**: no captured payload bytes, tags, or sequence values are reproduced verbatim.
Field offsets, sizes, encodings, and *observed structural behavior* (e.g. "this field stays
constant across a frame's fragments") are documented — the structure, not the data.

## Where this fits

This is tier 3's stream port (see `ps5-network-architecture.md`). It runs alongside the control
channel on UDP 9303 (`ps5-session-transport.md`): the control channel does the handshake and
carries `RPCS` control frames and keepalive; **9297 carries the actual video, audio, controller
input, and reliability feedback**, all multiplexed together.

In the captured session: stream up ~24 s, ~9.5k packets total. Direction split was heavily
asymmetric — ~9.1k console→client (A/V down) vs. ~0.3k client→console (input + feedback up), as
expected for a video stream.

## Common packet header

Every 9297 packet begins with a small plaintext header before any channel-specific fields or the
encrypted payload. The consistent leading structure across all channels:

| Offset | Size | Field | Notes |
|---|---|---|---|
| 0 | 1 byte | **Channel / type** | Selects the sub-protocol (table below). This is the multiplexing key. |
| 1 | 1 byte | Sub-flags | Usually `00` on media channels; other values seen on the feedback channel. Not fully decoded. |
| 2 | 1 byte | Counter | Slowly-incrementing byte, cadence not 1:1 with the sequence number below — possibly a key/epoch or per-channel counter. Tentative. |
| 3 | 2 bytes | Tag/checksum | Differs on every packet, no visible pattern — a per-packet checksum or truncated MAC fragment. Tentative. |
| 5 | 4 bytes | **Sequence number** | Big-endian, monotonic per channel. Gaps line up with packets of other channels interleaved on the wire, i.e. it is a per-channel sequence. |

After offset 9 the layout is channel-specific. This common front matches the controller-input
packet header documented separately (`ps5-controller-input-packet.md`) — input is just one channel
of this same framing, which the earlier standalone analysis didn't yet realize.

## Channel map (observed)

Byte 0 values seen, by direction:

**Console → client (down):**

| Channel | Meaning (inferred) | Volume | Notes |
|---|---|---|---|
| `0x02` | Video | dominant (~7.4k pkts) | Full-MTU fragments + short frame-tail fragments |
| `0x03` | Audio | ~1.5k pkts | Fixed-ish ~310-byte packets, steady ~87/s |
| `0x00` | Control / keepalive | ~200 pkts | Includes a repeated byte-identical idle/heartbeat packet |
| `0x12` | Stats / report (tentative) | ~85 pkts | Low-rate (~3–4/s); likely a periodic sender/quality report |
| `0x06`, `0x07` | Rare control | few | Not characterized |

**Client → console (up):**

| Channel | Meaning (inferred) | Volume | Notes |
|---|---|---|---|
| `0x0e` | **Controller input** | ~130 pkts | See `ps5-controller-input-packet.md` |
| `0x00` (+ sub-flags) | Congestion / reliability feedback | ~100+ pkts | Receiver reports; occasional large packets (~0.9–1.3 kB) likely loss/NACK or corruption reports |
| `0x05`, `0x01`, `0x03`, `0x08` | Per-channel ACK/feedback (tentative) | few each | Small; likely per-channel acknowledgement |

The feedback channel (up, `0x00` with non-zero sub-flags) is the one most relevant to an adaptive
bitrate controller: it is where the client reports reception quality back to the console, and it
carries both small periodic reports and occasional large ones (candidate NACK/retransmit-request
lists). Worth targeting in a follow-up capture with deliberately induced packet loss.

## Video channel (`0x02`) — fragment/frame structure

Beyond the common header, video packets carry reassembly fields. Observed layout (offsets into the
UDP payload):

| Offset | Size | Field | Observed behavior |
|---|---|---|---|
| 9 | 2 bytes | Unit index | Monotonic across the stream (a running fragment counter). Tentative vs. the byte-offset field below. |
| 11 | 2 bytes | **Frame index** | Big-endian; **constant across all fragments of one video frame**, increments by 1 per frame. The primary reassembly key. |
| 13 | 2 bytes | **Fragment position within frame** | Resets to 0 at each new frame, then steps by a fixed increment (0x20 observed) per fragment. Marks a fragment's place within its frame. |
| 15 | 1 byte | Frame flags / type | `0x28` on a large multi-fragment frame (read as keyframe/IDR), `0x04`/`0x0c` on small frames (read as inter/P-frames). A GOP structure is directly visible in these values. Tentative bit meanings. |
| 16 | 2 bytes | Marker | Constant (`0x0103` observed) across video packets — a fixed codec/NAL-unit or descriptor marker. Tentative. |
| 18 | 4 bytes | Per-fragment tag | Differs per fragment — likely FEC/authentication material. |
| 22 | 4 bytes | Cumulative byte offset | Grows per fragment within a frame by roughly the payload size — a running byte offset into the frame's encoded data. Useful as a second reassembly cross-check. |
| 26+ | rest | Encrypted video payload | High entropy; the encoded H.264/HEVC slice data once decrypted. |

Frame structure directly observed: large frames spanning ~11+ full-MTU fragments (keyframes)
interleaved with small 1–4 fragment frames (inter-frames), i.e. a normal GOP. Full-size fragments
were 1459 bytes on the wire (≈1409 payload after 42-byte L2/L3/L4 headers, i.e. sized to a ~1400 B
path); each frame ends with a shorter tail fragment.

**Stream-init packet**: the very first video packet of the session was a distinct all-zero/`0xff`-
patterned packet (not frame data) sent just before frame fragments began — read as a codec/stream
configuration packet (parameter sets, e.g. SPS/PPS-equivalent) that a decoder needs before the
first frame. A receiver must handle this class separately from ordinary fragments. Tentative.

Frame cadence: the audio-tail/short-fragment and frame-index rates are consistent with ~60 frames
per second, but frame rate should be confirmed against a decode rather than inferred from packet
timing.

### The elementary stream itself **[W]**

Measured by parsing the parameter sets out of two decrypted Annex-B dumps written by the 3DS port's
`dumpvideo=1` path (1,214 and 941 pictures). Both agree exactly, and a single SPS and a single PPS
serve the whole session in each.

| Field | Value | Consequence for a decoder |
|---|---|---|
| `profile_idc` | **77 — Main** | No 8×8 transform and no scaling matrices; those are High-profile only |
| `level_idc` | **31 — Level 3.1** | At the observed 640×368. A higher resolution must raise it — 720p60 exceeds 3.1's MB rate **[X]** |
| `constraint_flags` | `0x40` (`constraint_set1_flag`) | Main-conformant |
| `entropy_coding_mode_flag` | **1 — CABAC** | The expensive half to implement, and the part that resists parallelism |
| `chroma_format_idc` | 1 — 4:2:0 | |
| `frame_mbs_only_flag` | **1 — progressive** | No field coding, no MBAFF |
| `num_slice_groups_minus1` | 0 | One slice group; no FMO/ASO |
| `pic_order_cnt_type` | **2** | Picture order **is** decode order. No `pic_order_cnt_lsb` is coded at all and reordering is not representable — which corroborates the I-and-P-only slice types from a second direction |
| `log2_max_frame_num` | 7 | `frame_num` is 7 bits and wraps at 128 |
| `max_num_ref_frames` | **9** | A nine-frame DPB. With no B-slices, long P-prediction chains are where the efficiency comes from — and nine 720p NV12 frames is ~12.5 MB a decoder must hold |
| `frame_cropping_flag` | 1 | 640×368 coded, cropped to the 640×360 displayed |
| `pic_init_qp` | 26 | |
| `deblocking_filter_control_present_flag` | 1 | Slice headers carry deblocking overrides |
| `redundant_pic_cnt_present_flag` | 0 | No redundant slices |
| Slice types present | **I and P only** | No B-slices: no reordering delay, no bipredictive MC, no DPB reorder logic |
| Coded size | 640×368 | Macroblock-aligned; 640×360 displayed, per `ports/ripcord-3ds/HARDWARE-PROBES.md` |

**Slices per picture: min 1, max 22, mean 2.0–2.3.** The maximum is the IDR picture — one keyframe of
21–22 slices — while ordinary P-pictures carry one or two. This is consistent with the one-slice-per-MTU
behaviour recorded in `ports/ripcord-3ds/source/mvdreplay/main.c`, and that file's independent count (941
pictures, 20 SPS, a 21-NAL first keyframe) reproduces exactly.

**Both dumps are 640×368, so the slice count at higher resolutions is not measured `[X]`.** One slice per
MTU implies it scales with macroblock count — 720p is roughly four times the macroblocks — but that is an
inference, and it matters to anyone parallelising entropy decode across cores. Measurable from a 720p
dump whenever one is taken.

IDRs are infrequent: one IDR picture in 1,214, and 89 IDR slices across 941 pictures in the other dump.
That matches the separately recorded observation of two keyframes in 25 seconds.

## Audio channel (`0x03`)

Fixed-structure packets, ~310 bytes on the wire, ~87/s, one encrypted audio unit each (1:1
sequence increments — audio is not fragmented the way video is). The common header (channel, seq)
is followed by a couple of small index fields and a constant marker, then the encrypted audio
payload. Codec not identifiable from the ciphertext (PS Remote Play audio is understood publicly to
be Opus, but that is not something this capture can confirm and should be verified at decode time).

## Control / feedback channels (`0x00`)

- **Down `0x00`**: includes a repeated, byte-identical short packet (~70 B) — an idle heartbeat /
  no-op keepalive on the stream — plus a few larger control packets.
- **Up `0x00` + sub-flags**: the client's reception feedback, described in the channel map above.

These are the reliability/congestion-control plumbing of the stream. Fully decoding the receiver-
report format is a priority for Phase 4 (adaptive bitrate), less so for a first video-only
receiver.

## What's still needed

- ~~Confirm channel semantics and the video reassembly fields against an actual decode (decrypt →
  feed a decoder → verify frame boundaries reconstruct correctly).~~ **Done.** Both front ends decode
  this stream end to end, and the elementary-stream parameters are now measured rather than inferred —
  see [The elementary stream itself](#the-elementary-stream-itself-w). The framing table above remains
  structural inference from plaintext framing, and the fields still marked tentative there are still
  tentative.
- Measure slices per picture at 720p. The 640×368 dumps give a mean of 2.0–2.3, and whether that scales
  with macroblock count decides how far entropy decode can be parallelised on a multi-core port **[X]**.
- Decode the up-direction feedback/report format (channel `0x00` + sub-flags) — needed for
  adaptive bitrate; capture with induced loss to force NACK/report variety.
- Characterize the `0x12` low-rate down channel (stats/report vs. something else).
- Nail down the offset-2 counter and offset-3 tag semantics (shared with the input packet header).
- The encryption itself: which session key (from `ps5-session-establishment.md`) protects 9297
  payloads, and how the sequence/counter fields feed the AEAD nonce — a decode can't happen
  without this, and this capture can't recover it (keys never traverse the wire in the clear).
