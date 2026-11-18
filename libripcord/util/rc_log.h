/*
 * libripcord - logging to the screen (or a line sink), to a log file, and to a ring of recent lines.
 *
 * An on-device program prints to its console and, with this, writes the same text to a file next to its
 * own executable - typing multi-stage results off a console screen by hand is slow and error-prone, and
 * a run's output can be copied off the device afterward instead.
 *
 * Shared by the core and every port's programs; it has no PlayStation or networking knowledge of its
 * own, so it carries no dependency either way.
 */
#ifndef RC_LOG_H
#define RC_LOG_H

/*
 * Opens <directory containing argv0>/filename for append, first rotating it to filename.prev if it has
 * passed RC_LOG_MAX_BYTES. argv[0] is how a .3dsx launched from the Homebrew Launcher learns its own
 * path - this rides on that rather than requiring a hardcoded location, so the log lands next to
 * whichever copy of the app produced it. Falls back to RC_PROGRAM_DIR_FALLBACK/filename if argv0 does not
 * look like a path (see rc_program_dir.h).
 *
 * A failure to open is reported on screen but not fatal - rc_log() still reaches the console either way.
 * Call once at startup, after the port has set up whatever stdout prints to.
 */
void rc_log_open(const char *argv0, const char *filename);

/*
 * printf to the console and, if rc_log_open() succeeded, the same bytes to the log file. Flushed after
 * every call: these are human-scale, once-per-stage messages, not a per-packet hot path, so the extra
 * write cost buys crash/power-loss safety for free.
 */
void rc_log(const char *fmt, ...) __attribute__((format(printf, 1, 2)));

/*
 * A LINE SINK, for a host that has somewhere better than stdout to put these.
 *
 * On a console rc_log() output belongs on the screen. A host with its own logger (reached through
 * halyard_client's `log` callback) wants the same lines there instead.
 *
 * With a sink set, each complete line goes to `sink` INSTEAD OF stdout - without its newline and with
 * any ANSI colour sequences removed, since those are for a terminal - and the ring and the log file
 * still receive every line. A line split across several rc_log() calls is delivered once, when its
 * newline arrives. `sink` NULL restores the default. The sink is process-wide, like the rest of this
 * module, and single-threaded for the same reason s_format is static: see rc_log.c.
 */
typedef void (*rc_log_sink_fn)(void *user, const char *line);

void rc_log_set_sink(rc_log_sink_fn sink, void *user);

/* The sink's `user`, or NULL - so a caller can clear a sink only if it is still its own. */
void *rc_log_sink_user(void);

/* Closes the log file if one was opened. Safe to call even if rc_log_open() was never called or failed. */
void rc_log_close(void);

/*
 * How large the log may get before rc_log_open rotates it.
 *
 * The log is opened for append so a run can be compared against the ones before it, and rotated so it
 * cannot fill a console partition (on hardware it reached 44 KB across fourteen runs). One previous
 * generation is kept.
 */
#ifndef RC_LOG_MAX_BYTES
#define RC_LOG_MAX_BYTES (128u * 1024u)
#endif

/*
 * RECENT LINES, so the 3DS's bottom screen can be given away to an overlay and taken back.
 *
 * libctru's console owns that framebuffer and anything drawn over it destroys the rendered text -
 * PrintConsole keeps a character buffer but exposes no redraw.
 *
 * 30 lines is exactly one bottom-screen page at the console's 8x8 font (320/8 = 40 columns, 240/8 = 30
 * rows); a line longer than RC_LOG_RING_COLUMNS - 1 characters is truncated in the ring. This is a
 * VIEW, not the record: every line still goes to the log file, flushed.
 */
#define RC_LOG_RING_LINES 30
#define RC_LOG_RING_COLUMNS 64

/* Clears the console and reprints the ring, oldest first. */
void rc_log_replay(void);

#endif /* RC_LOG_H */
