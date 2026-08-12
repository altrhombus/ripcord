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
 * CONFIDENCE LEVELS (mirrors Ripcord.Protocol.Halyard.Common.Control.HalyardSessCtrlFields, which this
 * is checked against, not translated from): the registkey zero-padding, the RP-Did fixed-structure
 * shape and the "WinX.Y" string are wire-confirmed [V]. RP-StartBitrate/RP-StreamingType's little-endian
 * 4-byte encoding is tentative [X] - it does not appear in any verified vector on either side of this
 * port, only in the shipped (but unverified) .NET behaviour.
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
size_t halyard_sess_field_os_type_plaintext(int major, int minor, char *buf, size_t buf_size);

/* RP-StartBitrate / RP-StreamingType plaintext: a 4-byte little-endian integer - see the [X] note above. */
void halyard_sess_field_int32le_plaintext(int32_t value, uint8_t out[4]);

/* The login passcode plaintext: its digits as ASCII characters, nothing more (e.g. "1234" -> 31 32 33
 * 34). Returns the plaintext length (equal to pin_length), or 0 if pin contains a non-digit or
 * buf_size is too small - the console will never accept anything else, so this refuses to encrypt it. */
size_t halyard_sess_field_login_pin_plaintext(const char *pin, size_t pin_length, uint8_t *buf, size_t buf_size);

#endif /* HALYARD_SESS_FIELDS_H */
