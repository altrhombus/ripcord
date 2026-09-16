/*
 * ripcord-3ds - the /sess/ctrl encrypted field plaintexts (spec sec 2.1).
 *
 * Each field's plaintext is built here, then sealed by the already-ported control-field cipher
 * (halyard_control_field_encrypt in ../halyard/halyard_v1.h) with the field's own counter, and
 * base64-encoded (rc_base64_encode) for the HTTP header. Field order fixes each counter:
 * RP-Auth=0, RP-Did=1, RP-OSType=2, RP-StartBitrate=3, RP-StreamingType=4.
 *
 * THE COUNTER IS ONE RUNNING PER-CONNECTION VALUE, NOT PER-FIELD-TYPE. The login passcode submitted
 * later on the binary control channel continues the SAME counter at 5 (the sixth field-encrypt on this
 * connection) - callers must never reset it back to 0 for a later message on an already-established
 * connection, or they reuse an IV.
 *
 * Ported from Ripcord.Protocol.Halyard.Common.Control.HalyardSessCtrlFields.
 *
 * CONFIDENCE LEVELS carry over unchanged: the registkey zero-padding, the RP-Did fixed-structure shape
 * and the "WinX.Y" string are wire-confirmed [V]. RP-StartBitrate/RP-StreamingType's little-endian 4-byte
 * encoding is tentative [X] - it does not appear in any verified vector on either side of this port,
 * only in shipped (but unverified) behaviour. That uncertainty is the protocol's, not a clean-room
 * artifact - porting the .NET code directly doesn't resolve it, only a live capture would.
 */
#ifndef HALYARD_SESS_FIELDS_H
#define HALYARD_SESS_FIELDS_H

#include <stddef.h>
#include <stdint.h>

#define HALYARD_SESS_COUNTER_AUTH             0u
#define HALYARD_SESS_COUNTER_DID              1u
#define HALYARD_SESS_COUNTER_OS_TYPE          2u
#define HALYARD_SESS_COUNTER_START_BITRATE    3u
#define HALYARD_SESS_COUNTER_STREAMING_TYPE   4u
#define HALYARD_SESS_COUNTER_LOGIN_PIN_START  5u

/*
 * THE CONSOLE'S OWN COUNTER, which is a separate sequence from the five above.
 *
 * The control-field cipher's counter is per-connection and shared across a whole DIRECTION, so each side
 * counts independently. Ours spends 0-4 on the /sess/ctrl request fields, which is why a login passcode
 * is 5. The console's spends 0 on its /sess/ctrl response, so the next frame it encrypts is 1.
 *
 * Only payload-carrying frames spend a counter; heartbeats and the login prompt carry nothing and spend
 * nothing. Confirmed on the .NET side with a known-plaintext oracle - the session-id frame decrypts at
 * counter 2 to a length-prefixed "InvalidSessionId", and at no other counter to anything at all.
 */
#define HALYARD_SESS_COUNTER_CONSOLE_START    1u

#define HALYARD_SESS_AUTH_PLAINTEXT_SIZE 16
#define HALYARD_SESS_DID_PLAINTEXT_SIZE 32

/* RP-Auth plaintext: the raw registration key (up to 16 bytes; the real key is always 8) zero-padded to
 * 16 bytes. */
void halyard_sess_field_auth_plaintext(const uint8_t *registration_key, size_t registration_key_length,
                                       uint8_t out[HALYARD_SESS_AUTH_PLAINTEXT_SIZE]);

/*
 * RP-Did plaintext: a fixed 32-byte structure, NOT length-prefixed - a 10-byte marker prefix (read from
 * the vendor wire), then up to 16 identifying bytes (a device id - the .NET side uses the Windows
 * MachineGuid; this port has no equivalent stable id yet, so the caller supplies whatever bytes it has),
 * then 6 zero bytes. The console treats the middle as an opaque client id.
 */
void halyard_sess_field_did_plaintext(const uint8_t *device_id, size_t device_id_length,
                                      uint8_t out[HALYARD_SESS_DID_PLAINTEXT_SIZE]);

/*
 * RP-OSType plaintext: the ASCII string "Win{major}.{minor}" with a trailing NUL - e.g. major=10,
 * minor=0 gives "Win10.0\0" (8 bytes). Returns the plaintext length (including the trailing NUL), or 0
 * if buf_size is too small. This port reports a real Windows version rather than inventing a 3DS one:
 * the console has only ever been observed accepting this field, and there is no evidence changing it
 * would do anything but add risk.
 */
/*
 * RP-OSType's plaintext: "Win<major>.<minor>" with a trailing NUL.
 *
 * THE "Win" IS NOT DECORATION AND IS NOT OURS TO CHANGE. **[V]** The console's own remote-play list
 * names every session this project opens as a PC, so both this field and the plaintext `User-Agent:
 * remoteplay Windows` header were made settable to find out which one it reads. The console VALIDATES
 * BOTH: a value it does not recognise is rejected and the session does not open.
 *
 * So the device name is not a string the client chooses. Whatever the console shows is selected from a
 * set it already knows, and getting a PS3 to appear as anything other than a PC would mean finding a
 * value in that set - not inventing one. The experiment is recorded here rather than the code, because
 * the code it needed was reverted and the finding is the part worth keeping.
 */
size_t halyard_sess_field_os_type_plaintext(int major, int minor, char *buf, size_t buf_size);

/* RP-StartBitrate / RP-StreamingType plaintext: a 4-byte little-endian integer - see the [X] note above. */
void halyard_sess_field_int32le_plaintext(int32_t value, uint8_t out[4]);

/*
 * A BOUND THIS PORT SET, NOT ONE THE PROTOCOL STATES. The plaintext is simply the digits, so nothing in
 * the wire format caps their number, and no capture we hold shows the console refusing a length. C
 * callers need a buffer size, though, and the alternative - every caller inventing one - is how a
 * truncated passcode ends up being reported as a wrong passcode. Generous on purpose: if a console ever
 * asks for more than this, raise it here rather than at a call site.
 */
#define HALYARD_SESS_LOGIN_PIN_MAX 32u

/* The login passcode plaintext: its digits as ASCII characters, nothing more (e.g. "1234" -> 31 32 33
 * 34). Returns the plaintext length (equal to pin_length), or 0 if pin contains a non-digit or
 * buf_size is too small - the console will never accept anything else, so this refuses to encrypt it. */
size_t halyard_sess_field_login_pin_plaintext(const char *pin, size_t pin_length, uint8_t *buf, size_t buf_size);

#endif /* HALYARD_SESS_FIELDS_H */
