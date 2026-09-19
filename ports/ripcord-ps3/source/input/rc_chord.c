/*
 * ripcord-ps3 - the two-button chord state machine. See rc_chord.h for what it is and why it is separate.
 *
 * This is the exact logic that lived inside rc_pad_read, moved out unchanged so it can be driven from a
 * host test. Nothing here touches PSL1GHT: a mask in, a mask out, the clock passed as an argument.
 */
#include "rc_chord.h"

uint32_t rc_chord_apply(rc_chord *c, uint32_t chord_mask, uint32_t buttons, uint64_t now)
{
    uint32_t held = buttons & chord_mask;
    uint32_t suppress = 0u;
    uint32_t result;

    if (held == chord_mask) {
        /* Both down: the chord. Count it once, on the rising edge, and swallow both. */
        if (!c->held)
            c->edges++;
        c->held = 1;
        c->latched = 1;
        suppress = chord_mask;
        c->pending = 0;
        c->replay = 0u;             /* a chord forming cancels its first button's replay */
    } else if (c->latched) {
        /*
         * A CHORD THAT FIRED SWALLOWS BOTH BUTTONS UNTIL BOTH ARE LET GO. Without this latch, releasing
         * one thumb a moment before the other leaves the remaining button looking like a fresh lone
         * press - which re-arms the hold-back, and the release then replays it, leaving a stray press
         * behind every use of the chord.
         */
        c->held = 0;
        suppress = held;
        c->pending = 0;
        if (held == 0u)
            c->latched = 0;
    } else {
        c->held = 0;
        if (held != 0u) {
            /* One button down, no partner yet. Withhold it while the window is open. */
            if (!c->pending) {
                c->pending = 1;
                c->pending_since = now;
            }
            if (now - c->pending_since < RC_CHORD_WINDOW_MS)
                suppress = held;
            else
                c->pending = 0;          /* the window passed; it travels from here on */
        } else {
            /*
             * LET GO INSIDE THE WINDOW, WITH NO PARTNER. It was a tap, and withholding it was right up
             * to this instant and wrong from it - so replay what was withheld, briefly.
             */
            if (c->pending && now - c->pending_since < RC_CHORD_WINDOW_MS) {
                c->replay = c->withheld;
                c->replay_until = now + RC_CHORD_REPLAY_MS;
            }
            c->pending = 0;
        }
    }

    c->withheld = suppress;

    result = buttons & ~suppress;
    if (c->replay != 0u) {
        if (now < c->replay_until)
            result |= c->replay;
        else
            c->replay = 0u;
    }
    return result;
}
