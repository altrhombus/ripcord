/*
 * ripcord-3ds - "where is this program's copy of itself" resolution.
 *
 * argv[0] is how a .3dsx launched from the Homebrew Launcher learns its own path; this turns that into
 * a directory prefix any on-device program can use to find a file that travels with it on the SD card
 * (a log, in source/util/rc_log.c's case; a provisional pairing record, in source/session/main.c's).
 * Factored out once a second module needed it, not written ahead of any real use.
 */
#ifndef RC_PROGRAM_DIR_H
#define RC_PROGRAM_DIR_H

#include <stddef.h>

/*
 * Writes the directory containing argv0 (trailing slash included) into out, NUL-terminated and
 * truncated to fit out_size. Falls back to "sdmc:/" if argv0 does not look like a path - e.g. launched
 * over a network loader (3dslink) rather than from the SD card.
 */
/*
 * The writable root used when argv[0] carries no directory. Each port defines it (3DS: "sdmc:/",
 * Vita: "ux0:data/"); the default keeps a build that forgets honest rather than silently writing
 * somewhere that does not exist.
 */
#ifndef RC_PROGRAM_DIR_FALLBACK
#define RC_PROGRAM_DIR_FALLBACK "sdmc:/"
#endif

void rc_program_dir(const char *argv0, char *out, size_t out_size);

#endif /* RC_PROGRAM_DIR_H */
