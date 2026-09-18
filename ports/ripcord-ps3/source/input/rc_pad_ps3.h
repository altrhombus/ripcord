/*
 * ripcord-ps3 - the DualShock 3, mapped to the wire's controller state.
 *
 * The mapping is the only part of input this port owns. Everything else - the two packet shapes, the
 * transition history, the sealing and the encryption - is in ports/common and in the sealer, where it
 * is shared with every other front end and with the .NET client that derived it.
 *
 * WHAT THIS PAD CAN AND CANNOT SEND. A DualShock 3 has both stick clicks and pressure-sensitive
 * shoulders, and the shared layer gained the bits for the first and a level for the second when this
 * port needed them. Its PS button never reaches an application: the system claims it, so
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

/*
 * Whether a shoulder has ever reported a level that is neither off nor fully on. That is the only
 * evidence pressure sensitivity was actually granted: a port that refused it reports zeros, which is
 * exactly what an untouched trigger reports, so "it works" and "it silently does not" are otherwise the
 * same observation.
 */
int rc_pad_analog_triggers_seen(void);

/*
 * How many times the in-session menu's chord - SELECT and START together - has been completed. The
 * caller watches this for a change rather than being told "pressed", so neither side has to agree on
 * when a press stops being new.
 *
 * A press of either chord button is WITHHELD briefly while waiting for its partner, so reaching for the
 * menu does not send the buttons on the way - SELECT arrives at a PS5 as Create, which is its
 * screenshot button. See the .c for which buttons, why those, and the one edge it does not close.
 */
unsigned rc_pad_chord_edges(void);

void rc_pad_close(void);

#endif /* RC_PAD_PS3_H */
