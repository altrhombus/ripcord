/* See rc_status_screen.h. */
#include "rc_status_screen.h"

#include <stddef.h>

#include "rc_overlay.h"
#include "rc_video_ps3.h"

int rc_status_screen_wants_draw(const rc_session_state *state)
{
    if (state == NULL)
        return 0;
    /* Streaming has a better status than any words: the picture. */
    return state->phase != RC_PHASE_STREAMING;
}

void rc_status_screen_draw(const rc_session_state *state)
{
    rc_video_info info;
    uint32_t accent;
    int x, y, line, pad;

    if (state == NULL || !rc_video_info_get(&info))
        return;

    /*
     * THE SAME BITMAP AS THE DIAGNOSTICS PANEL, moved to the middle. Not thrift: it is in main memory
     * with a VRAM copy and a blit already worked out, and drawing a second card straight into the back
     * buffer would mean BLENDING there - reads from RSX memory, which are the slow direction by two
     * orders of magnitude. Antialiased text needs blending, so the surface it is drawn on has to be one
     * that can be read cheaply.
     */
    x = (info.width - rc_overlay_width()) / 2;
    y = (info.height - rc_overlay_height()) / 2;

    if (!rc_overlay_begin_now())
        return;

    /*
     * A FAILURE IS COLOURED AND NOTHING ELSE IS. Progress is the normal case, and a screen that shouts
     * at every step teaches people to ignore it - which is what has to still work the one time it
     * matters.
     */
    accent = rc_session_is_error(state) ? RC_OV_BAD
           : (state->phase == RC_PHASE_ENDED ? RC_OV_LABEL : RC_OV_ACCENT);

    rc_overlay_rect(0, 0, rc_overlay_width(), rc_overlay_height(), RC_OV_PANEL);
    rc_overlay_rect(0, 0, rc_overlay_width(), rc_overlay_px(6), accent);
    rc_overlay_rect(0, rc_overlay_height() - 1, rc_overlay_width(), 1, RC_OV_EDGE);
    rc_overlay_rect(0, 0, 1, rc_overlay_height(), RC_OV_EDGE);
    rc_overlay_rect(rc_overlay_width() - 1, 0, 1, rc_overlay_height(), RC_OV_EDGE);

    pad = rc_overlay_px(36);
    line = rc_overlay_px(76);
    rc_overlay_text(pad, line - rc_overlay_ascent(2), 2, RC_OV_TEXT, "%s", state->headline);
    line += rc_overlay_px(58);

    if (state->detail[0] != '\0') {
        rc_overlay_text(pad, line - rc_overlay_ascent(1), 1, RC_OV_LABEL, "%s", state->detail);
        line += rc_overlay_px(42);
    }
    if (state->hint[0] != '\0') {
        /*
         * The hint is the only line below the headline set in white, because it is the only one the
         * reader can act on - see rc_session_state.h on why a failure is not allowed to omit it.
         */
        rc_overlay_text(pad, line - rc_overlay_ascent(1), 1, RC_OV_TEXT, "%s", state->hint);
    }

    /*
     * Cleared behind the card because there may be a stale last frame there, and a half-covered picture
     * under an error message reads as a fault in the message rather than in the stream.
     */
    rc_video_clear_back(0x00000000u);
    rc_overlay_end_now(x, y);
    rc_video_flip();
}
