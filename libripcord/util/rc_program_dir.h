/*
 * libripcord - "where is this program's copy of itself" resolution.
 *
 * argv[0] is how a homebrew program (a .3dsx launched from the Homebrew Launcher, for one) learns its
 * own path; this turns that into a directory prefix any on-device program can use to find a file that
 * travels with it (a log, in util/rc_log.c's case; the pairing record, in
 * session/halyard_pairing_file.c's).
 */
#ifndef RC_PROGRAM_DIR_H
#define RC_PROGRAM_DIR_H

#include <stddef.h>

/*
 * The writable root used when argv[0] carries no directory. The default is the 3DS's SD card root; a
 * port whose root differs defines its own at build time (the PS3 Makefile passes its log directory).
 * On a platform where the default device does not exist, the only symptom is a file that never appears.
 */
#ifndef RC_PROGRAM_DIR_FALLBACK
#define RC_PROGRAM_DIR_FALLBACK "sdmc:/"
#endif

/*
 * Writes the directory containing argv0 (trailing slash included) into out, NUL-terminated and
 * truncated to fit out_size. Falls back to RC_PROGRAM_DIR_FALLBACK if argv0 is NULL or contains no
 * '/' - e.g. launched over a network loader (3dslink) rather than from the SD card.
 */
void rc_program_dir(const char *argv0, char *out, size_t out_size);

#endif /* RC_PROGRAM_DIR_H */
