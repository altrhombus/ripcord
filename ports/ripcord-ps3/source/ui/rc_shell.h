/*
 * ripcord-ps3 - the screen a person actually starts at.
 *
 * Everything before this was a bring-up harness: it ran a fixed list of checks, read its one console's
 * address out of a text file put there over FTP, and reported to a log. That is the right shape for
 * finding out whether a decoder works and the wrong shape for anything else - there was no way to
 * choose a console, no way to change a setting, and no way to find out what went wrong except by
 * fetching a file from the machine afterwards.
 *
 * WHAT THIS OWNS AND WHAT IT DOES NOT. It owns the home screen, the settings screen, and the loop that
 * turns a d-pad into a cursor. It owns none of the drawing primitives (rc_overlay), none of the list
 * arithmetic (ports/common/ui/rc_menu.c, which is tested on a host with no console attached), and none
 * of the protocol. It decides what is on the screen and what happens when Cross is pressed, which is
 * the part that is genuinely about this platform.
 *
 * IT DRAWS ONTO THE OVERLAY'S SURFACE rather than into the back buffer, for the reason that file gives
 * at length: antialiased text has to be blended, blending means reading what is already there, and
 * reads from RSX memory on this machine are about a hundred times slower than writes. The surface is
 * in main memory with one queued copy per change - which also means the menu costs nothing at all on
 * the frames where nobody pressed anything.
 */
#ifndef RC_SHELL_H
#define RC_SHELL_H

typedef enum {
    RC_SHELL_QUIT = 0,   /* the person asked to leave */
    RC_SHELL_CONNECT     /* ...or to stream, with a pairing record in place */
} rc_shell_action;

/*
 * Runs the home screen until one of those two things is chosen. `dirs` is the same ordered list of
 * candidate directories the rest of the program uses to find "pairing.txt" - passed in so the shell
 * cannot end up editing a record in one place while the connect path reads another.
 *
 * Returns what to do next. It does NOT connect: streaming is main's business and belongs where the rest
 * of the session lifecycle already lives.
 */
rc_shell_action rc_shell_run(const char *const *dirs, int dir_count);

#endif /* RC_SHELL_H */
