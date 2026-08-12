#include "rc_program_dir.h"

#include <string.h>

void rc_program_dir(const char *argv0, char *out, size_t out_size)
{
    const char *slash = (argv0 != NULL) ? strrchr(argv0, '/') : NULL;

    if (slash != NULL) {
        size_t dirlen = (size_t)(slash - argv0) + 1; /* keep the trailing slash */
        if (dirlen >= out_size)
            dirlen = out_size - 1;
        memcpy(out, argv0, dirlen);
        out[dirlen] = '\0';
    } else {
        strncpy(out, "sdmc:/", out_size - 1);
        out[out_size - 1] = '\0';
    }
}
