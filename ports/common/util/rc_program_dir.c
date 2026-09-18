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
        /* No argv[0] to derive from - fall back to the platform's writable root.
         *
         * This default used to be "sdmc:/" unconditionally, which was correct and invisible for as long
         * as the 3DS was the only port: nothing on that platform ever reaches it with a path that
         * matters. On the Vita it is simply a nonexistent device, and the only symptom would have been
         * a log file that silently never appeared. Each port defines its own; the 3DS keeps the value
         * it always had. */
        strncpy(out, RC_PROGRAM_DIR_FALLBACK, out_size - 1);
        out[out_size - 1] = '\0';
    }
}
