/*
 * ripcord-ps3 - the session state, on the television.
 *
 * Separate from the diagnostics panel because of WHEN each is drawn. The panel sits over a running
 * picture and has to be copied in behind the RSX's own blit every frame; this is what the screen shows
 * when there is no picture - before one arrives, and after one stops. It draws a card into the
 * overlay's bitmap, clears the back buffer behind it, queues the copy to the middle of the screen and
 * flips.
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
