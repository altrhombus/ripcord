#include "rc_log.h"
#include "rc_program_dir.h"

#include <stdarg.h>
#include <stdio.h>
#include <string.h>

static FILE *s_log = NULL;

/*
 * Rotate `path` to `path.prev` if it has grown past RC_LOG_MAX_BYTES. Returns nothing and reports
 * nothing: a log that cannot be rotated is not a reason to fail a run, and the next line written will
 * make it obvious anyway.
 *
 * Size is measured by seeking rather than by stat(), because this has to work on every platform the
 * ports run on and fopen/fseek/ftell is the part of the C library all of them agree about.
 */
static void rotate_if_large(const char *path)
{
    char previous[512];
    long size;
    FILE *existing = fopen(path, "r");

    if (existing == NULL)
        return;  /* nothing there yet - the common case on a fresh install */

    if (fseek(existing, 0L, SEEK_END) != 0) {
        (void)fclose(existing);
        return;
    }
    size = ftell(existing);
    (void)fclose(existing);

    if (size < 0L || (unsigned long)size < (unsigned long)RC_LOG_MAX_BYTES)
        return;

    if (strlen(path) + 5u >= sizeof(previous))
        return;
    strcpy(previous, path);
    strcat(previous, ".prev");

    /* remove() first because rename() onto an existing file is not portable. Both are allowed to fail. */
    (void)remove(previous);
    if (rename(path, previous) != 0) {
        /*
         * Rotation failed - truncate instead. Losing the history is worse than keeping it, but a log
         * that grows without bound on a console partition is worse than both.
         */
        FILE *truncated = fopen(path, "w");
        if (truncated != NULL)
            (void)fclose(truncated);
    }
}

void rc_log_open(const char *argv0, const char *filename)
{
    char path[512];

    rc_program_dir(argv0, path, sizeof(path));
    strncat(path, filename, sizeof(path) - strlen(path) - 1);

    rotate_if_large(path);

    s_log = fopen(path, "a");
    if (s_log != NULL) {
        fprintf(s_log, "\n---- new run ----\n");
        fflush(s_log);
        printf("logging to %s\n", path);
    } else {
        printf("\x1b[33mNOTE\x1b[0m could not open %s for logging - screen output only\n", path);
    }
}

/*
 * Scrollback, and a warning about where its buffer lives.
 *
 * THE FORMAT BUFFER IS STATIC, NOT A LOCAL, and that is not a style preference. The first version of
 * this put a 512-byte `char line[512]` on rc_log's stack frame. rc_log is called from deep inside the
 * connect flow - including immediately before the P-521 SESSION_REPLY verification, which is itself
 * stack-hungry - and the main thread gets 32 KB for everything. The result was reproducible: SESSION_REPLY
 * rejected on every attempt, i.e. a purely cosmetic logging change broke the crypto handshake.
 *
 * Static is safe here because logging is single-threaded: the scale worker that once ran on core 2 is
 * shelved, and nothing else logs. If a second thread ever logs again, this needs a lock, not a local.
 */
static char s_format[256];
static char s_ring[RC_LOG_RING_LINES][RC_LOG_RING_COLUMNS];
static int s_ring_head;
static int s_ring_count;
static char s_pending[RC_LOG_RING_COLUMNS];
static size_t s_pending_len;

static void ring_push_pending(void)
{
    s_pending[s_pending_len] = '\0';
    memcpy(s_ring[s_ring_head], s_pending, s_pending_len + 1u);
    s_ring_head = (s_ring_head + 1) % RC_LOG_RING_LINES;
    if (s_ring_count < RC_LOG_RING_LINES)
        s_ring_count++;
    s_pending_len = 0;
}

/* Splits already-formatted output on newlines. Long lines are truncated - the file has them in full. */
static void ring_append(const char *text)
{
    size_t i;

    for (i = 0; text[i] != '\0'; i++) {
        if (text[i] == '\n')
            ring_push_pending();
        else if (s_pending_len < RC_LOG_RING_COLUMNS - 1u)
            s_pending[s_pending_len++] = text[i];
    }
}

void rc_log_replay(void)
{
    int i;

    printf("\x1b[2J\x1b[H");
    for (i = 0; i < s_ring_count; i++) {
        int slot = (s_ring_head - s_ring_count + i + RC_LOG_RING_LINES * 2) % RC_LOG_RING_LINES;
        printf("%s\n", s_ring[slot]);
    }
}

void rc_log(const char *fmt, ...)
{
    va_list args;

    va_start(args, fmt);
    (void)vsnprintf(s_format, sizeof(s_format), fmt, args);
    va_end(args);
    fputs(s_format, stdout);
    ring_append(s_format);

    if (s_log != NULL) {
        va_start(args, fmt);
        vfprintf(s_log, fmt, args);
        va_end(args);
        fflush(s_log);
    }
}

void rc_log_close(void)
{
    if (s_log != NULL) {
        fclose(s_log);
        s_log = NULL;
    }
}
