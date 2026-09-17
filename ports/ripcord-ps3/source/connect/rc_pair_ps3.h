/*
 * ripcord-ps3 - pairing with a console, driven from the console itself.
 *
 * Until now a pairing record was produced by ProtocolLab on a PC and copied over by hand, which is
 * fine for bring-up and means the port cannot be used by anyone who is not already running the desktop
 * client. This asks the three questions on the television and writes the answer.
 *
 * WHAT IT ASKS AND WHY EACH ONE. The console's ADDRESS, because discovery may not have run or may have
 * found several. The PSN ACCOUNT ID, because the console checks it and nothing on this machine knows
 * it - that is the piece worth removing later if the PS3's own account can be read, and it is asked
 * for plainly rather than guessed at. And the PIN, which the console shows on its own screen for a
 * few minutes.
 *
 * EVERY STEP REPORTS THROUGH rc_session_state, so a refusal reaches the person standing in front of it
 * instead of a log file on another machine - see that header on why the hint is the part that matters.
 */
#ifndef RC_PAIR_PS3_H
#define RC_PAIR_PS3_H

#include "halyard_pairing_file.h"

/*
 * WHERE THE PAIRING RECORD LIVES. Exposed so the shell writes settings to the same file pairing writes
 * the keys to - two answers to that question in one program is how a setting gets saved somewhere
 * nothing reads, which looks exactly like a setting that is ignored.
 */
const char *rc_pair_record_dir(void);

/*
 * THIS PORT'S MEASURED DEFAULTS, for a record that was never loaded. ports/common defaults to a 3DS's
 * settings, which is right for the port that set them and produced a 29 fps first session here - see
 * the .c for every number and what it was measured against.
 */
void rc_pair_apply_port_defaults(halyard_pairing_record *record);

/*
 * Runs the whole thing: three prompts, the registration exchange, and the write. Returns 1 when a
 * pairing record was written.
 *
 * `host` pre-fills the address prompt when discovery already found one; NULL leaves it empty. `name` is
 * what discovery called it, stored so a list of paired consoles can be read by a human - NULL is fine
 * and leaves the address to stand in for it. The session state carries what happened either way, and is
 * left on the screen.
 *
 * `console_id` is the id discovery reported, and it is the one field here that is not a convenience:
 * `host` is a DHCP lease and this is not, so a record carrying it can be found again after the console
 * moves instead of having to be re-paired. NULL when pairing from a typed address, which is a normal
 * state - the id is learned the next time that console answers a broadcast.
 *
 * THE CONSOLES ALREADY PAIRED ARE KEPT. This adds one or replaces the entry at the same address; it no
 * longer overwrites the file, which is what used to destroy the previous console's keys silently.
 */
int rc_pair_run(const char *host, const char *name, const char *console_id);

#endif /* RC_PAIR_PS3_H */
