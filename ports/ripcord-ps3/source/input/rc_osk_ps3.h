/*
 * ripcord-ps3 - asking the user for text, with the console's own keyboard.
 *
 * The OSK is the system's dialog, so it looks and behaves the way everything else on the machine does
 * and costs this port no UI of its own. What it costs instead is that it is ASYNCHRONOUS: the call that
 * raises it returns immediately, and the result arrives through the sysutil callback that has to be
 * pumped until it does. This wraps that into one blocking call, because every caller here wants to ask
 * a question and wait for the answer.
 *
 * STRINGS ARE UTF-16 ON THE WIRE BETWEEN US AND IT, and ASCII on both sides of that. Everything this
 * port asks for is an address, a number or an account id, so the conversion is a widen and a narrow,
 * and a character outside ASCII is refused rather than mangled - a PIN with a full-width digit in it
 * would otherwise parse as something surprising.
 */
#ifndef RC_OSK_PS3_H
#define RC_OSK_PS3_H

#include <stddef.h>

typedef enum {
    RC_OSK_OK = 0,
    RC_OSK_CANCELLED,     /* the user backed out, which is not a failure */
    RC_OSK_UNAVAILABLE,   /* the dialog could not be raised at all */
    RC_OSK_TOO_LONG,      /* what came back does not fit, or is not ASCII */
    RC_OSK_TIMED_OUT
} rc_osk_status;

typedef enum {
    RC_OSK_TEXT = 0,      /* a full keyboard, letters first - addresses */
    RC_OSK_NUMBERS,       /* a keypad only - PINs, which are always eight digits */
    /*
     * Digits first, letters still reachable. For a PSN account id, which is a nineteen-digit number in
     * every case this port has seen but is not guaranteed to be one - so starting on the keypad saves
     * the typing without making the other case impossible.
     */
    RC_OSK_DIGITS_FIRST
} rc_osk_kind;

/*
 * Raises the keyboard and blocks until the user finishes or backs out.
 *
 * `prompt` is shown above the field and `initial` pre-fills it (either may be NULL). `out` receives
 * NUL-terminated ASCII. Returns a status rather than a bool because "cancelled" and "could not be
 * shown" are different things to tell someone about.
 */
/*
 * WHAT TO DRAW BEHIND THE KEYBOARD, and it is not decoration.
 *
 * A system dialog here composites into the APPLICATION'S flip stream rather than drawing itself onto
 * the screen, so an application that stops presenting while one is up stops it appearing at all. The
 * hook is called once a frame and is expected to draw something and flip. Without one this falls back
 * to flipping whatever is already in the buffer, which is enough to make the dialog visible but will
 * show whatever was last drawn behind it.
 */
void rc_osk_set_present_hook(void (*present)(void));

/*
 * WHAT TO KEEP ALIVE WHILE THE KEYBOARD IS UP, which is a different question from what to draw.
 *
 * This blocks, and somebody typing a passcode can take half a minute over it. A caller that holds a
 * connection open across the call has that whole time taken away from it - and the console's control
 * session disconnects a client that stops answering its heartbeats within a few seconds, so asking for
 * a passcode would reliably destroy the session the passcode was for.
 *
 * So the hook is called on every pass of the wait loop, before the frame is presented, and is expected
 * to do whatever the caller must not stop doing. It must not block: the keyboard's own responsiveness
 * is this loop's rate. NULL, the default, means there is nothing to keep alive.
 */
void rc_osk_set_pump_hook(void (*pump)(void));

rc_osk_status rc_osk_ask(rc_osk_kind kind, const char *prompt, const char *initial,
                         char *out, size_t out_size);

/* Text for a status, for a caller putting it on the screen. Never NULL. */
const char *rc_osk_status_text(rc_osk_status status);

#endif /* RC_OSK_PS3_H */
