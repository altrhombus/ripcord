/* See rc_pad_ps3.h - especially on what a DualShock 3 cannot send. */
#include "rc_pad_ps3.h"

#include <io/pad.h>

#include "platform/rc_platform.h"
#include <string.h>

#define RC_PAD_PORTS 7

static int s_open;
static int s_connected;
static unsigned s_reads;
static unsigned s_changes;

int rc_pad_open(void)
{
    if (s_open)
        return 1;
    if (ioPadInit(RC_PAD_PORTS) != 0)
        return 0;
    s_open = 1;
    return 1;
}

/*
 * A pad axis is 0..255 with 128 at rest; the wire wants a signed 16-bit value with left and up
 * NEGATIVE. The PS3's axes already run that way - 0 is left, and 0 is UP - so this scales without
 * inverting, which the 3DS port does have to do. Getting that backwards is invisible in a log and
 * obvious the moment someone tries to walk forwards.
 *
 * The two halves are scaled separately because the range is not symmetric: 128 counts below centre and
 * 127 above. One factor leaves either full-left short of the rail or full-right past it.
 */
static int16_t axis(unsigned int v)
{
    int c = (int)(v & 0xffu) - 128;

    if (c > 0)
        return (int16_t)((c * 32767) / 127);
    return (int16_t)(c * 256);
}

/*
 * A SMALL DEADZONE, and it is about packets as much as about aim.
 *
 * A DualShock 3's sticks do not rest at exactly 128 and they wander by a count or two untouched.
 * Without this the state differs from the previous one on nearly every poll, so a controller sitting on
 * a table sends continuously - and the drift is a real analog reading the console will act on, not
 * noise it will ignore. Small enough to cost no usable range.
 */
#define RC_PAD_DEADZONE 2048

static int16_t deadzone(int16_t v)
{
    return (v > -RC_PAD_DEADZONE && v < RC_PAD_DEADZONE) ? 0 : v;
}

/*
 * A ZERO-LENGTH READ MEANS "NOTHING NEW", NOT "NOTHING THERE", and getting that backwards is what b271
 * shipped.
 *
 * ioPadGetData fills `len` only when the pad has reported since the last call. Polling at 125 Hz
 * against a pad that reports far less often, and only when something changes, means most calls come
 * back empty - and treating each one as an absent controller produced 59 answered polls in sixty
 * seconds and 118 connect/disconnect events, which is the pad apparently vanishing and returning
 * between every pair of frames.
 *
 * It cost more than the counters. A poll that reported "no pad" sent NOTHING - so the 200 ms state
 * keepalive never ran, and the console heard from the controller about once a second.
 *
 * Presence is now decided by info.status alone, which is the field that actually answers it, and the
 * last good reading is held and re-sent while the pad has nothing new to say. That is also the truth:
 * a stick that has not moved is still where it was.
 */
static halyard_input_state s_last;
static int s_have_last;
static unsigned s_fresh;
/* Set once a shoulder reports a level that is neither off nor fully on - the only evidence that
 * pressure was actually granted, since a refusal reads exactly like a trigger nobody touched. */
static int s_analog_seen;
static int s_chord_held;
static unsigned s_chord_edges;
static uint64_t s_pending_since;
/* A tap that was withheld and turned out not to be a chord: which button, and until when. */
static uint32_t s_replay;
static uint64_t s_replay_until;
/* What the previous read held back, so a release knows which button to replay - by then `held` is 0. */
static uint32_t s_withheld;
/* A chord has fired and neither of its buttons may act again until both are released. */
static int s_chord_latched;

/*
 * How long a chord button is withheld while waiting for its partner. Long enough for two thumbs to
 * arrive together without hurrying, short enough that a press which turns out to be ordinary is late
 * rather than lost.
 */
#define RC_CHORD_WINDOW_MS 250u

/*
 * AND HOW LONG A TAP THAT TURNED OUT TO BE ORDINARY IS THEN HELD FOR.
 *
 * The hold-back above withholds a chord button while its partner might still arrive. The comment below
 * claimed that a press which turns out to be ordinary "is released and travels normally, a fifth of a
 * second late" - and for a press somebody is still holding, it does. For a TAP it did not: the button
 * came back up before the window expired, s_pending_since was cleared, and the press was never sent at
 * all. On the console that was a screenshot that did not happen; on this port's own menu it was START
 * appearing to need holding down to open the options, which is how it was noticed.
 *
 * So a tap inside the window is replayed - held for this long from the moment it is released, which is
 * what turns "swallowed" back into "late". Long enough that an edge-driven menu and a console reading
 * state at 200 Hz both see it, short enough that it is still a tap.
 */
#define RC_CHORD_REPLAY_MS 120u

int rc_pad_read(halyard_input_state *out)
{
    padInfo info;
    padData data;
    u32 port;

    if (!s_open || out == NULL)
        return 0;
    if (ioPadGetInfo(&info) != 0)
        return 0;

    for (port = 0u; port < (u32)RC_PAD_PORTS; port++) {
        if (info.status[port] == 0)
            continue;

        if (!s_connected) {
            s_connected = 1;
            s_changes++;
            /*
             * PRESSURE HAS TO BE ASKED FOR, and asking is the whole of what makes the shoulders analog.
             * Without this the PRE_ fields read zero - and zero is indistinguishable from "not pressed",
             * so the writer's digital fallback takes over and everything LOOKS right while every trigger
             * arrives fully on or fully off. Re-asked on each connect, because the setting belongs to
             * the port and a pad that was unplugged and returned is a new port state.
             */
            (void)ioPadSetPortSetting(port, PAD_SETTINGS_PRESS_ON);
        }
        s_reads++;

        if (ioPadGetData(port, &data) == 0 && data.len > 0) {
            s_fresh++;
            memset(&s_last, 0, sizeof(s_last));
            s_last.buttons =
                  (data.BTN_CROSS    ? HALYARD_PAD_CROSS      : 0u)
                | (data.BTN_CIRCLE   ? HALYARD_PAD_CIRCLE     : 0u)
                | (data.BTN_SQUARE   ? HALYARD_PAD_SQUARE     : 0u)
                | (data.BTN_TRIANGLE ? HALYARD_PAD_TRIANGLE   : 0u)
                | (data.BTN_UP       ? HALYARD_PAD_DPAD_UP    : 0u)
                | (data.BTN_DOWN     ? HALYARD_PAD_DPAD_DOWN  : 0u)
                | (data.BTN_LEFT     ? HALYARD_PAD_DPAD_LEFT  : 0u)
                | (data.BTN_RIGHT    ? HALYARD_PAD_DPAD_RIGHT : 0u)
                | (data.BTN_L1       ? HALYARD_PAD_L1         : 0u)
                | (data.BTN_R1       ? HALYARD_PAD_R1         : 0u)
                | (data.BTN_L2       ? HALYARD_PAD_L2         : 0u)
                | (data.BTN_R2       ? HALYARD_PAD_R2         : 0u)
                | (data.BTN_L3       ? HALYARD_PAD_L3         : 0u)
                | (data.BTN_R3       ? HALYARD_PAD_R3         : 0u)
                /*
                 * START is Options and SELECT is Create. The PS5 renamed both, and the console is told
                 * the new names' codes; a DualShock 3 has the old buttons in the same places, so this
                 * maps by position rather than by name.
                 */
                | (data.BTN_START    ? HALYARD_PAD_OPTIONS    : 0u)
                | (data.BTN_SELECT   ? HALYARD_PAD_CREATE     : 0u);

            /*
             * The shoulders as LEVELS. The pressure fields are documented 0x0000-0x00FF, so they are
             * already the range the wire wants. The button bits above are still set and the writer
             * prefers the level where both are present - which means a pad without pressure, or one
             * whose pressure was refused, degrades to digital instead of to nothing.
             */
            s_last.left_trigger = (uint8_t)(data.PRE_L2 & 0xffu);
            s_last.right_trigger = (uint8_t)(data.PRE_R2 & 0xffu);
            if ((s_last.left_trigger > 0u && s_last.left_trigger < 0xffu)
                || (s_last.right_trigger > 0u && s_last.right_trigger < 0xffu))
                s_analog_seen = 1;

            s_last.left_x = deadzone(axis(data.ANA_L_H));
            s_last.left_y = deadzone(axis(data.ANA_L_V));
            s_last.right_x = deadzone(axis(data.ANA_R_H));
            s_last.right_y = deadzone(axis(data.ANA_R_V));
            s_have_last = 1;
        }

        if (!s_have_last)
            return 0;   /* connected but has not yet said anything - nothing truthful to send */

        /*
         * THE DIAGNOSTICS CHORD: Options + Create, held together.
         *
         * Two buttons rather than three, because three was awkward to press with the same hands that
         * are holding the controller. Both are menu buttons, which matters twice over: no game asks for
         * them together, and neither is latency-sensitive - which is what makes the hold-back below
         * affordable.
         *
         * L3 is out of it deliberately. It was in the first version and it is a sprint or a crouch in
         * most games, so delaying it by a fifth of a second to see whether a chord forms would be felt.
         * Delaying Options or Create is not.
         *
         * HELD BACK, NOT JUST SWALLOWED, and this is the fix rather than a refinement. The first
         * version removed the chord's buttons only once ALL of them were down, so whichever was pressed
         * first had already gone out as an ordinary press - and Create on a PS5 is the screenshot
         * button, so reaching for the overlay took a burst of screenshots with it. "A pause menu at
         * worst" was wrong.
         *
         * So a press of either button is now withheld for RC_CHORD_WINDOW_MS. If the other arrives
         * inside that window the pair is swallowed and nothing reaches the console; if it does not, the
         * press is released and travels normally, a fifth of a second late. A tap of Create still takes
         * a screenshot. Only a deliberate pair does not.
         *
         * A fumbled chord - one button, a long pause, then the other - still toggles, and still leaks
         * the first button, because after the window it has genuinely been sent. That is the honest
         * edge of this design and it costs one stray menu press, not a burst of them.
         */
        {
            const uint32_t chord = HALYARD_PAD_OPTIONS | HALYARD_PAD_CREATE;
            uint32_t held = s_last.buttons & chord;
            uint32_t suppress = 0u;
            uint64_t now = rc_time_ms();

            if (held == chord) {
                if (!s_chord_held)
                    s_chord_edges++;    /* rising edge - the caller acts on the count changing */
                s_chord_held = 1;
                s_chord_latched = 1;
                suppress = chord;
                s_pending_since = 0u;
                s_replay = 0u;          /* a chord forming cancels its first button's replay */
            } else if (s_chord_latched) {
                /*
                 * A CHORD THAT HAS FIRED SWALLOWS BOTH BUTTONS UNTIL BOTH ARE LET GO, and this latch is
                 * what makes that true of the second one. Without it, releasing one thumb a moment
                 * before the other left the remaining button looking like a fresh lone press - so it
                 * re-armed the hold-back, and then the release replayed it. Every use of the
                 * diagnostics chord would have ended with a stray Options press behind it, which on
                 * this port's menu is the options screen opening every time the overlay is toggled.
                 */
                s_chord_held = 0;
                suppress = held;
                s_pending_since = 0u;
                if (held == 0u)
                    s_chord_latched = 0;
            } else {
                s_chord_held = 0;
                if (held != 0u) {
                    if (s_pending_since == 0u)
                        s_pending_since = now;
                    if (now - s_pending_since < RC_CHORD_WINDOW_MS)
                        suppress = held;
                    else
                        s_pending_since = 0u;   /* the window passed; it travels from here on */
                } else {
                    /*
                     * LET GO INSIDE THE WINDOW, WITH NO PARTNER. It was a tap, and withholding it was
                     * right up to this instant and wrong from it - see RC_CHORD_REPLAY_MS.
                     */
                    if (s_pending_since != 0u && now - s_pending_since < RC_CHORD_WINDOW_MS) {
                        s_replay = s_withheld;
                        s_replay_until = now + RC_CHORD_REPLAY_MS;
                    }
                    s_pending_since = 0u;
                }
            }
            s_withheld = suppress;

            *out = s_last;
            out->buttons &= ~suppress;
            if (s_replay != 0u) {
                if (now < s_replay_until)
                    out->buttons |= s_replay;
                else
                    s_replay = 0u;
            }
            return 1;
        }
    }

    if (s_connected) {
        s_connected = 0;
        s_changes++;
        s_have_last = 0;
    }
    return 0;
}

/*
 * The number of times the chord has been COMPLETED, not whether it is held. A count lets the caller act
 * on a change without this file knowing what the chord is for, and without either side having to agree
 * on when a press stops being new.
 */
unsigned rc_pad_chord_edges(void)
{
    return s_chord_edges;
}

int rc_pad_analog_triggers_seen(void)
{
    return s_analog_seen;
}

void rc_pad_stats(int *connected, unsigned *reads, unsigned *fresh, unsigned *changes)
{
    if (connected != NULL)
        *connected = s_connected;
    if (reads != NULL)
        *reads = s_reads;
    /* Separately from reads, because the two being far apart is the normal and healthy case and their
     * being EQUAL would mean the pad is reporting on every poll. Conflating them is what hid b271. */
    if (fresh != NULL)
        *fresh = s_fresh;
    if (changes != NULL)
        *changes = s_changes;
}

void rc_pad_close(void)
{
    if (!s_open)
        return;
    (void)ioPadEnd();
    s_open = 0;
}
