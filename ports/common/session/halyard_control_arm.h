/*
 * ripcord-3ds - the control-listener arming probe, ported from
 * Ripcord.Protocol.Halyard.Discovery.HalyardControlSearch.
 *
 * The console does not keep its TCP control listener (port 9295) open continuously - a cold connect
 * gets a TCP RST ("connection actively refused"). It opens the listener briefly in response to a 4-byte
 * UDP probe on the SAME port: "SRC3" for PS5, "SRC2" for PS4, replied to with "RES3"/"RES2". One probe
 * arms the listener for both /sess/init and the /sess/ctrl reconnect that follows it, so send it once
 * per connection attempt, not once per TCP connect.
 *
 * Sending the probe is what arms the console, so a caller that never sees a reply should still proceed
 * to the TCP connect after a short settle - the reply is confirmation, not a prerequisite.
 */
#ifndef HALYARD_CONTROL_ARM_H
#define HALYARD_CONTROL_ARM_H

#include <stddef.h>
#include <stdint.h>

#define HALYARD_CONTROL_ARM_PORT 9295
#define HALYARD_CONTROL_ARM_PROBE_SIZE 4

/* How long to listen for the reply, and how long to let the console settle before speaking to it. */
#define HALYARD_CONTROL_ARM_REPLY_WINDOW_MS 2000u
#define HALYARD_CONTROL_ARM_SETTLE_MS       200u

/* Writes the 4-byte ASCII probe ("SRC3" or "SRC2") into out. Always succeeds; out is not NUL-terminated
 * (it is a raw UDP payload, not a C string) and out[] must be at least HALYARD_CONTROL_ARM_PROBE_SIZE. */
void halyard_control_arm_build_probe(int is_ps5, char out[HALYARD_CONTROL_ARM_PROBE_SIZE]);

/* True if `data` (at least 4 bytes) starts with the expected reply magic for `is_ps5` - "RES3"/"RES2".
 * Only the magic is checked; any trailing bytes (a status byte or two, observed on the vendor wire) are
 * not interpreted by this port. */
int halyard_control_arm_is_reply(int is_ps5, const uint8_t *data, size_t length);

/*
 * SEND THE PROBE AND WAIT BRIEFLY FOR THE REPLY, then settle.
 *
 * The console does not hold TCP 9295 open continuously; this 4-byte UDP probe opens it. Best-effort by
 * design: SENDING is what arms it, so a lost reply is not fatal and callers proceed regardless.
 *
 * IT IS NOT OPTIONAL FOR REGISTRATION EITHER, which is why it lives here rather than inside the control
 * session that first needed it. A registration POST from a client the console has not just heard from
 * is refused with the generic 80108bff - a byte-perfect request, rejected, with an application reason
 * that says nothing. The vendor's own client always searches first.
 *
 * Returns 1 if a reply was seen, 0 if not; both are usable outcomes and the distinction is only worth
 * reporting.
 */
int halyard_control_arm_probe(const char *host, int is_ps5);

#endif /* HALYARD_CONTROL_ARM_H */
