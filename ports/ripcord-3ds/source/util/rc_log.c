#include "rc_log.h"

#include <stdarg.h>
#include <stdio.h>
#include <string.h>

static FILE *s_log = NULL;

void rc_log_open(const char *argv0, const char *filename)
{
    char path[512];
    const char *slash = (argv0 != NULL) ? strrchr(argv0, '/') : NULL;

    if (slash != NULL) {
        size_t dirlen = (size_t)(slash - argv0) + 1; /* keep the trailing slash */
        if (dirlen >= sizeof(path))
            dirlen = sizeof(path) - 1;
        memcpy(path, argv0, dirlen);
        path[dirlen] = '\0';
    } else {
        /* No usable argv[0] - e.g. launched over a network loader rather than from the SD card. */
        strncpy(path, "sdmc:/", sizeof(path) - 1);
        path[sizeof(path) - 1] = '\0';
    }
    strncat(path, filename, sizeof(path) - strlen(path) - 1);

    s_log = fopen(path, "a");
    if (s_log != NULL) {
        fprintf(s_log, "\n---- new run ----\n");
        fflush(s_log);
        printf("logging to %s\n", path);
    } else {
        printf("\x1b[33mNOTE\x1b[0m could not open %s for logging - screen output only\n", path);
    }
}

void rc_log(const char *fmt, ...)
{
    va_list args;

    va_start(args, fmt);
    vprintf(fmt, args);
    va_end(args);

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
