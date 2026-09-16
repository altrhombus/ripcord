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

/*
 * Runs the whole thing: three prompts, the registration exchange, and the write. Returns 1 when a
 * pairing record was written.
 *
 * `host` pre-fills the address prompt when discovery already found one; NULL leaves it empty. The
 * session state carries what happened either way, and is left on the screen.
 */
int rc_pair_run(const char *host);

#endif /* RC_PAIR_PS3_H */
