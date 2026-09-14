/*
 * ripcord-3ds - dual console/file logging.
 *
 * Both on-device programs print to the top screen and, with this, write the same text next to their own
 * .3dsx on the SD card - typing multi-stage results off a console screen by hand is slow and error-prone,
 * and this exists so a run's output can just be copied off the card afterward instead.
 *
 * Shared by source/app and source/linktest rather than duplicated; it has no PlayStation or networking
 * knowledge of its own, so it carries no dependency either way.
 */
#ifndef RC_LOG_H
#define RC_LOG_H

/*
 * Opens <directory containing argv0>/filename for append. argv[0] is how a .3dsx launched from the
 * Homebrew Launcher learns its own path - this rides on that rather than requiring a hardcoded location,
 * so the log lands next to whichever copy of the app produced it. Falls back to sdmc:/filename if argv0
 * does not look like a path (e.g. launched over a network loader instead of from the SD card).
 *
 * A failure to open is reported on screen but not fatal - rc_log() still reaches the console either way.
 * Call once at startup, after consoleInit().
 */
void rc_log_open(const char *argv0, const char *filename);

/*
 * printf to the console and, if rc_log_open() succeeded, the same bytes to the log file. Flushed after
 * every call: these are human-scale, once-per-stage messages, not a per-packet hot path, so the extra
 * write cost buys crash/power-loss safety for free.
 */
void rc_log(const char *fmt, ...) __attribute__((format(printf, 1, 2)));

/* Closes the log file if one was opened. Safe to call even if rc_log_open() was never called or failed. */
void rc_log_close(void);

/*
 * RECENT LINES, so the bottom screen can be given away to an overlay and taken back.
 *
 * libctru's console owns that framebuffer and anything drawn over it destroys the rendered text -
 * PrintConsole keeps a character buffer but exposes no redraw. Without a scrollback of our own, an
 * overlay could borrow the screen and never hand it back.
 *
 * 30 lines is exactly one bottom-screen page at the console's 8x8 font (320/8 = 40 columns, 240/8 = 30
 * rows). This is a VIEW, not the record: every line still goes to the SD file, flushed, as it always has.
 */
/*
 * How large the log may get before rc_log_open rotates it, and why it rotates at all.
 *
 * The log is opened for append so a run can be compared against the ones before it - which is the whole
 * reason it is not truncated every time. But nothing was ever reclaiming it: on hardware it reached
 * 44 KB across fourteen runs, all of it on a console partition, and the only thing that would eventually
 * stop it growing is the partition filling up.
 *
 * One previous generation is kept, because the run BEFORE last is exactly the one wanted when the last
 * run destroyed the evidence. Two would be a filing system; zero throws away the comparison.
 */
#ifndef RC_LOG_MAX_BYTES
#define RC_LOG_MAX_BYTES (128u * 1024u)
#endif

#define RC_LOG_RING_LINES 30
#define RC_LOG_RING_COLUMNS 64

/* Clears the console and reprints the ring, oldest first. */
void rc_log_replay(void);

#endif /* RC_LOG_H */
