/*
 * ripcord-ps3 - the DualShock 3, mapped to the wire's controller state.
 *
 * The mapping is the only part of input this port owns. Everything else - the two packet shapes, the
 * transition history, the sealing and the encryption - is in ports/common and in the sealer, where it
 * is shared with every other front end and with the .NET client that derived it.
 *
 * WHAT THIS PAD CAN AND CANNOT SEND. A DualShock 3 has both stick clicks, which the shared layer only
 * gained when this port needed them. It has pressure-sensitive shoulders, which are still sent as a
 * digital 0x00/0xff because the shared state struct carries no level - the one place this is behind
 * the .NET writer. And its PS button never reaches an application: the system claims it, so
 * HALYARD_PAD_PS is never set from here, and a user wanting the console's own menu has the real one in
 * front of them.
 */
#ifndef RC_PAD_PS3_H
#define RC_PAD_PS3_H

#include "halyard_input.h"

/* Opens the pad subsystem. 0 if it refuses, in which case the session runs without input rather than
 * not running - a stream you cannot steer is still worth more than no stream. */
int rc_pad_open(void);

/*
 * Reads whichever port has a pad on it into `out`. Returns 1 when a pad answered, 0 when none is
 * connected - the caller should then send nothing rather than send neutral, because neutral is a
 * position and "no controller" is not.
 */
int rc_pad_read(halyard_input_state *out);

/* Whether a pad is present, how many polls answered, how many of those carried NEW data, and how many
 * times presence changed. Reads and fresh are separate on purpose - see rc_pad_stats in the .c. */
void rc_pad_stats(int *connected, unsigned *reads, unsigned *fresh, unsigned *changes);

void rc_pad_close(void);

#endif /* RC_PAD_PS3_H */
