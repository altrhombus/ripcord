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
    RC_OSK_TEXT = 0,      /* a full keyboard - addresses, account ids */
    RC_OSK_NUMBERS        /* a keypad - PINs */
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

rc_osk_status rc_osk_ask(rc_osk_kind kind, const char *prompt, const char *initial,
                         char *out, size_t out_size);

/* Text for a status, for a caller putting it on the screen. Never NULL. */
const char *rc_osk_status_text(rc_osk_status status);

#endif /* RC_OSK_PS3_H */
