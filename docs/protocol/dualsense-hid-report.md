# DualSense HID input report

Status: **USB and Bluetooth both confirmed** from live captures (`054C:0CE6` — 37,437 USB reports, 47,804 BT
reports).

## Three report formats

- **USB full** — report id `0x01`, 64 bytes. Full field block right after the id (below).
- **Bluetooth compatibility** — report id `0x01`, delivered padded to 78 bytes, in a **DS4-style compact
  layout** (sticks + buttons + triggers only; **no touchpad position or motion**). This is what Windows sends
  over Bluetooth *by default*, and what our BT capture actually contained (not `0x31`). Distinguished from USB
  full by length (USB full is exactly 64 bytes).
- **Bluetooth full** — report id `0x31`, 78 bytes: one extra lead byte before the same full field block, plus
  a trailing CRC-32. Only sent **after activation** — reading feature report `0x05` (calibration) switches the
  controller out of compatibility mode. The transport does this on open (best-effort); if it doesn't take, the
  compatibility report still parses (minus touchpad-xy/gyro).

This is the physical controller's own HID report (not the PS5 Remote Play protocol). We read it directly so we
get inputs Windows.Gaming.Input hides — most importantly the **PS button**, plus the touchpad and (later) gyro.
The generic gamepad API stays the baseline for standard pads; this raw-HID path is DualSense-specific.

## How it was reversed

Differential analysis of the capture (`tools/Ripcord.HidCapture` → `[u16 len][bytes]` records): for each byte,
how many distinct values it took and its range, plus enumerating the distinct values of the button bytes while
every control was exercised. That pins each field to the byte/bit it actually moves; cross-checked against the
public DualSense layout. The parser (`src/Ripcord.Input.Common/DualSenseReportParser.cs`) encodes this; tests
use synthesized reports (no capture bytes committed).

## Full report field block (USB id `0x01` / Bluetooth-full id `0x31`)

Offsets are absolute within the USB report. The Bluetooth-full report (id `0x31`) carries **one extra lead
byte** before the same field block (so add 1 to every offset below) and appends a CRC-32; the parser handles
both by parsing a shared body at base offset 1 (USB) or 2 (Bluetooth-full).

| Offset | Field |
|---|---|
| 0 | report id (`0x01`) |
| 1..4 | left X, left Y, right X, right Y (0–255, 128 center; **Y is 0 = up**) |
| 5..6 | L2, R2 analog (0–255) |
| 7 | report counter |
| 8 | D-pad hat in low nibble (0=up, 2=right, 4=down, 6=left, 8=neutral; odd = diagonals); face buttons in high nibble — Square `0x10`, Cross `0x20`, Circle `0x40`, Triangle `0x80` |
| 9 | L1 `0x01`, R1 `0x02`, L2 `0x04`, R2 `0x08`, Create `0x10`, Options `0x20`, L3 `0x40`, R3 `0x80` |
| 10 | **PS `0x01`**, touchpad-click `0x02`, mute `0x04` |
| 16..27 | IMU: gyro (3× int16 LE) then accel (3× int16 LE) — parsed later, needs calibration |
| 28..31 | device timestamp (u32 LE) |
| 33..36 | touch point 1: `[id/active][x lo][x hi(4b)\|y lo(4b)][y hi]` — bit 7 of the id byte set = not touching; x is 12-bit (0–1919), y 12-bit (0–1079) |
| 37..40 | touch point 2 (same shape) |

Neutral state: sticks = `0x80`, triggers = `0x00`, byte 8 = `0x08` (hat neutral, no face), bytes 9/10 = `0x00`,
touch id byte bit 7 set.

## Compatibility report field block (Bluetooth default, id `0x01`)

Confirmed from the BT capture (differential analysis; only bytes 0–9 ever nonzero). Same as the DS4 compact
report — no touchpad position, no motion.

| Offset | Field |
|---|---|
| 0 | report id (`0x01`) |
| 1..4 | left X, left Y, right X, right Y |
| 5 | D-pad hat (low nibble) + face buttons (high nibble) — same masks as the full report's byte 8 |
| 6 | L1/R1/L2/R2/Create/Options/L3/R3 — same masks as the full report's byte 9 |
| 7 | **PS `0x01`**, touchpad-click `0x02`, mute `0x04`, then a counter in the upper bits |
| 8..9 | L2, R2 analog (0–255) |

So the button/face masks are identical to the full report; only the field *positions* differ (buttons at
5/6/7 + triggers at 8/9, vs. the full report's triggers at 5/6 + buttons at 8/9/10).

## Mapping to the neutral `ControllerStateFrame`

Cross→South, Circle→East, Square→West, Triangle→North; Options→Start, Create→Select, **PS→Guide**,
touchpad-click→TouchpadClick. Stick Y is inverted to the shared neutral convention (up = +1); the protocol
layer re-inverts for the console wire. Gyro/accel are left null until calibrated (the feedback sender then
sends idle motion). Touchpad finger position is decoded into `TouchpadSample` but not yet forwarded on the wire
(the feedback writer currently sends only touchpad *click*; finger-drag events are a follow-up).
