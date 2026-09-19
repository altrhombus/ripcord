/*
 * ripcord-ps3 - the two-button chord state machine, exercised on the host.
 *
 * THIS IS THE TEST THE LOGIC EARNED. The chord that opens the in-session menu has had two bugs, both
 * invisible in review and both a wrong answer to a particular sequence of (buttons, time): a lone tap
 * that was withheld and then never sent, and a stray press left on the console when the two buttons were
 * released a moment apart. rc_chord.c is that logic with nothing else attached - a mask in, a mask out,
 * the clock an argument - so the sequences that broke it can be run here in microseconds instead of found
 * on a television.
 *
 * The buttons are named as the wire sees them (Options/Create); on the pad they are START/SELECT. The
 * chord is those two together. Every case drives rc_chord_apply step by step and asserts what the console
 * would actually receive.
 */
#include "../source/input/rc_chord.h"
#include "../../common/input/halyard_input.h"

#include <stdio.h>
#include <string.h>

static int g_passed;
static int g_failed;

#define CHECK(cond, ...) do { \
    if (cond) { g_passed++; } \
    else { g_failed++; printf("FAIL %s:%d: ", __FILE__, __LINE__); printf(__VA_ARGS__); printf("\n"); } \
} while (0)

#define CHORD (HALYARD_PAD_OPTIONS | HALYARD_PAD_CREATE)
#define A HALYARD_PAD_OPTIONS   /* START on the pad */
#define B HALYARD_PAD_CREATE    /* SELECT on the pad */

/*
 * A LONE TAP MUST STILL REACH THE CONSOLE - the bug that made START seem to need holding.
 *
 * Press one button, hold it a moment inside the window, release it before the window closes. While held
 * it is withheld (it might yet be a chord); once released as a tap it is replayed briefly, because
 * withholding it was only ever right until the release. Silence throughout would be the bug.
 */
static void test_lone_tap_is_replayed(void)
{
    rc_chord c;
    memset(&c, 0, sizeof(c));

    /* t=0: A goes down. Withheld - it could be the start of a chord. */
    CHECK(rc_chord_apply(&c, CHORD, A, 0u) == 0u, "a lone press is withheld inside the window");
    /* t=100: still held, still inside the window, still withheld. */
    CHECK(rc_chord_apply(&c, CHORD, A, 100u) == 0u, "and stays withheld while held inside the window");
    /* t=150: released as a tap. From here it is replayed. */
    CHECK((rc_chord_apply(&c, CHORD, 0u, 150u) & A) == A, "a tap released inside the window is replayed");
    /* t=200: replay still active (started at 150, lasts RC_CHORD_REPLAY_MS). */
    CHECK((rc_chord_apply(&c, CHORD, 0u, 200u) & A) == A, "the replay outlives the release briefly");
    /* well past the replay window: gone. */
    CHECK((rc_chord_apply(&c, CHORD, 0u, 150u + RC_CHORD_REPLAY_MS + 1u) & A) == 0u,
          "and then it stops - a tap is a tap, not a hold");
    CHECK(c.edges == 0u, "a lone tap is not a chord");
}

/*
 * THE CHORD ITSELF: both buttons together fire it once, and neither reaches the console.
 */
static void test_chord_fires_and_swallows(void)
{
    rc_chord c;
    memset(&c, 0, sizeof(c));

    CHECK(rc_chord_apply(&c, CHORD, A, 0u) == 0u, "first button withheld");
    /* t=40: the partner arrives inside the window - the chord. */
    CHECK(rc_chord_apply(&c, CHORD, CHORD, 40u) == 0u, "both buttons together reach the console as nothing");
    CHECK(c.edges == 1u, "the chord completed once");
    /* Holding it does not re-fire. */
    CHECK(rc_chord_apply(&c, CHORD, CHORD, 60u) == 0u, "still swallowed while held");
    CHECK(c.edges == 1u, "and does not count again while held");
}

/*
 * RELEASING THE CHORD ONE THUMB AT A TIME LEAVES NO STRAY PRESS - the second bug.
 *
 * After the chord fires, lifting one button before the other must not let the remaining one look like a
 * fresh press. The latch swallows both until both are up; without it a stray START opened the options
 * menu behind the in-session menu every single time.
 */
static void test_staggered_release_leaves_nothing(void)
{
    rc_chord c;
    memset(&c, 0, sizeof(c));

    (void)rc_chord_apply(&c, CHORD, A, 0u);
    (void)rc_chord_apply(&c, CHORD, CHORD, 30u);   /* chord fires */
    CHECK(c.edges == 1u, "chord fired");
    /* Release A, keep B down: B must NOT travel, and must NOT be replayed later. */
    CHECK(rc_chord_apply(&c, CHORD, B, 60u) == 0u, "the button still down after a chord is swallowed");
    /* Release B too: both up, nothing sent, nothing replayed. */
    CHECK(rc_chord_apply(&c, CHORD, 0u, 90u) == 0u, "and releasing the second leaves no stray press");
    CHECK(rc_chord_apply(&c, CHORD, 0u, 300u) == 0u, "still nothing, well past any window");
    CHECK(c.edges == 1u, "the staggered release did not fire a second chord");
}

/*
 * THE HONEST EDGE THE DESIGN ACCEPTS: a fumbled chord - one button, a long pause, then the other - lets
 * the first button through after the window and still fires the chord. It costs one stray press, by
 * design, and the test states it so a future change cannot quietly turn it into something worse.
 */
static void test_fumbled_chord_leaks_the_first_then_fires(void)
{
    rc_chord c;
    memset(&c, 0, sizeof(c));

    CHECK(rc_chord_apply(&c, CHORD, A, 0u) == 0u, "first button withheld at once");
    /* Past the window with only A held: it now travels. */
    CHECK((rc_chord_apply(&c, CHORD, A, RC_CHORD_WINDOW_MS + 10u) & A) == A,
          "after the window a still-held lone button travels - it was not a chord in time");
    /* Now the partner arrives late: the chord still fires. */
    CHECK(rc_chord_apply(&c, CHORD, CHORD, RC_CHORD_WINDOW_MS + 50u) == 0u,
          "the late partner still forms the chord");
    CHECK(c.edges == 1u, "and it counts");
}

/*
 * A BUTTON THAT IS NOT PART OF THE CHORD PASSES STRAIGHT THROUGH, always. The chord must never withhold,
 * swallow or replay anything but its own two buttons.
 */
static void test_unrelated_button_untouched(void)
{
    rc_chord c;
    memset(&c, 0, sizeof(c));

    CHECK(rc_chord_apply(&c, CHORD, HALYARD_PAD_CROSS, 0u) == HALYARD_PAD_CROSS,
          "an unrelated button is passed through unchanged");
    /* Even while a chord button is pending, the unrelated one is untouched and the chord one withheld. */
    CHECK(rc_chord_apply(&c, CHORD, (uint32_t)(HALYARD_PAD_CROSS | A), 20u) == HALYARD_PAD_CROSS,
          "a pending chord button is withheld but the unrelated one still travels");
    CHECK(c.edges == 0u, "no chord from one button plus an unrelated one");
}

int main(void)
{
    test_lone_tap_is_replayed();
    test_chord_fires_and_swallows();
    test_staggered_release_leaves_nothing();
    test_fumbled_chord_leaks_the_first_then_fires();
    test_unrelated_button_untouched();

    if (g_failed != 0) {
        printf("chord: %d passed, %d failed\n", g_passed, g_failed);
        return 1;
    }
    printf("chord: %d passed, 0 failed\n", g_passed);
    return 0;
}
