/*
 * ripcord-ps3 - the session state, on the television.
 *
 * Separate from rc_overlay because of WHEN each is drawn. The overlay is a panel over a running picture
 * and has to be copied in behind the RSX's own blit every frame; this is what the screen shows when
 * there is no picture - before one arrives, and after one stops. Nothing is racing it, so it draws
 * into the back buffer directly and flips. Reusing the overlay would mean one piece of code with two
 * ordering rules.
 */
#ifndef RC_STATUS_SCREEN_H
#define RC_STATUS_SCREEN_H

#include "rc_session_state.h"

/* Draws the state and flips. Cheap enough to call on every change and no more often than that. */
void rc_status_screen_draw(const rc_session_state *state);

/*
 * True when this state is worth putting on the screen at all. Streaming is not - the picture is the
 * status - which is the one case where drawing would replace something better with something worse.
 */
int rc_status_screen_wants_draw(const rc_session_state *state);

#endif /* RC_STATUS_SCREEN_H */
