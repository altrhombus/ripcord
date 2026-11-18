/*
 * ripcord-ps3 - pairing with a console, driven from the console itself.
 *
 * A pairing record can also be produced by ProtocolLab on a PC and copied over by hand; this asks the
 * three questions on the television and writes the answer.
 *
 * What it asks: the console's ADDRESS, because discovery may not have run or may have found several.
 * The PSN ACCOUNT ID, because the console checks it and nothing on this machine knows it - that is the
 * piece worth removing later if the PS3's own account can be read, and it is asked for plainly rather
 * than guessed at. And the PIN, which the console shows on its own screen for a few minutes.
 *
 * Every step reports through rc_session_state, so a refusal reaches the person standing in front of it
 * instead of a log file on another machine - see that header.
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
 * THIS PORT'S MEASURED DEFAULTS, for a record that was never loaded. libripcord defaults to a 3DS's
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
 * `console_id` is the id discovery reported. `host` is a DHCP lease and this is not, so a record
 * carrying it can be found again after the console moves instead of having to be re-paired. NULL when
 * pairing from a typed address, which is a normal state - the id is learned the next time that console
 * answers a broadcast.
 *
 * The consoles already paired are kept: this adds one or replaces the entry at the same address, and
 * does not overwrite the file.
 *
 * `host_type` is the family, "PS5" or "PS4", as discovery reported it. NULL means nobody knows yet -
 * the address was typed - and the flow finds out for itself rather than assuming. The two families
 * take different registration paths, different key tables and different wrap tables, and a console
 * sent the wrong family's registration refuses it.
 */
int rc_pair_run(const char *host, const char *name, const char *console_id, const char *host_type);

#endif /* RC_PAIR_PS3_H */
