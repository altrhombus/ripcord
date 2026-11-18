/*
 * ripcord-ps3 - the screen a person actually starts at.
 *
 * The start-up checks in source/app/main.c report to a log; this is where somebody chooses a console,
 * changes a setting, and finds out what went wrong without fetching a file from the machine.
 *
 * WHAT THIS OWNS AND WHAT IT DOES NOT. It owns the home screen, the settings screen, and the loop that
 * turns a d-pad into a cursor. It owns none of the drawing primitives (rc_overlay), none of the list
 * arithmetic (libripcord/ui/rc_menu.c, which is tested on a host with no console attached), and none
 * of the protocol. It decides what is on the screen and what happens when Cross is pressed.
 *
 * IT DRAWS ONTO THE OVERLAY'S SURFACE rather than into the back buffer, for the reason that file gives
 * at length: antialiased text has to be blended, blending means reading what is already there, and
 * reads from RSX memory on this machine are about a hundred times slower than writes. The surface is
 * in main memory and is copied to video memory once a frame.
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
