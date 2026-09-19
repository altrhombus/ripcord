/*
 * ripcord-ps3 - the two-button chord, as a pure state machine.
 *
 * SELECT+START opens the in-session menu, and reaching for it must not leak either button to the console
 * on the way - SELECT arrives at a PS5 as Create, its screenshot button. So a press of either chord
 * button is WITHHELD briefly while its partner is waited for; if the partner comes, the pair is swallowed
 * and the chord fires; if it does not, the withheld press is released and travels late, and a lone TAP is
 * replayed for a moment so it still reaches the console rather than being eaten.
 *
 * WHY THIS IS ITS OWN FILE. The logic caused two hardware bugs - a tap that was withheld and then never
 * sent, and a stray press left behind when the chord's buttons were released a moment apart - and both
 * were invisible in review and obvious the instant a sequence of (buttons, time) was run through them. So
 * the decision is factored out of rc_pad_read, which is bound to PSL1GHT's pad types, into this: plain C
 * over a mask and a clock, driven by rc_pad on hardware and by rc_chord_test on the host.
 */
#ifndef RC_CHORD_H
#define RC_CHORD_H

#include <stdint.h>

/*
 * How long a lone chord button is withheld while its partner is awaited. A fifth of a second: long
 * enough that a two-thumb press registers as a chord, short enough that a real lone press is not
 * perceptibly delayed when it finally travels.
 */
#define RC_CHORD_WINDOW_MS 250u

/*
 * How long a released-inside-the-window tap is replayed for. A tap is up before the window ends, so
 * withholding it was right until that instant and wrong after: without a replay the console would never
 * see the button at all. Long enough that a console sampling at 200 Hz cannot miss it.
 */
#define RC_CHORD_REPLAY_MS 120u

typedef struct {
    int      held;          /* both chord buttons were down last step                             */
    int      latched;       /* the chord fired and is swallowing both buttons until both are up   */
    int      pending;       /* a lone chord button is being withheld, awaiting its partner        */
    uint64_t pending_since; /* when that lone button was first seen - only meaningful if `pending` */
    uint32_t withheld;      /* what was suppressed last step, so a tap knows what to replay        */
    uint32_t replay;        /* a tap being replayed, 0 when none is                                */
    uint64_t replay_until;  /* when the replay ends                                                */
    unsigned edges;         /* COMPLETED chords - the caller watches this for a change             */
} rc_chord;

/*
 * One step. Given the raw button mask and the current time in ms, returns the mask the console should
 * actually see: the chord's partners withheld or swallowed, a just-released tap replayed. `chord_mask`
 * is the two buttons that form the chord. `c` must be zero-initialised before the first call.
 *
 * `edges` rises each time the chord completes (both buttons down together); the caller acts on the count
 * changing and never has to agree with this file on what the chord is for.
 */
uint32_t rc_chord_apply(rc_chord *c, uint32_t chord_mask, uint32_t buttons, uint64_t now);

#endif /* RC_CHORD_H */
