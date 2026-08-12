/* See halyard_pairing_file.h for the file format and why this is shared rather than per-program. */

#include "halyard_pairing_file.h"

#include "../util/rc_hex.h"
#include "../util/rc_log.h"
#include "../util/rc_program_dir.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static void pairing_record_defaults(halyard_pairing_record *rec)
{
    memset(rec, 0, sizeof(*rec));
    rec->is_ps5 = 1;
    rec->os_major = 10;
    rec->os_minor = 0;
    rec->start_bitrate = 10000;
    rec->streaming_type = 0;
}

int halyard_pairing_file_load(const char *argv0, halyard_pairing_record *rec)
{
    char path[512];
    FILE *f;
    char line[256];
    int have_host = 0, have_registkey = 0, have_companion = 0;

    if (rec == NULL)
        return 0;

    pairing_record_defaults(rec);

    rc_program_dir(argv0, path, sizeof(path));
    strncat(path, "pairing.txt", sizeof(path) - strlen(path) - 1);

    f = fopen(path, "r");
    if (f == NULL) {
        rc_log("\x1b[31mFAIL\x1b[0m could not open %s\n", path);
        return 0;
    }

    while (fgets(line, sizeof(line), f) != NULL) {
        char *eq = strchr(line, '=');
        char *value;
        char *trail;

        if (eq == NULL)
            continue;
        *eq = '\0';
        value = eq + 1;
        trail = strpbrk(value, "\r\n");
        if (trail != NULL)
            *trail = '\0';

        if (strcmp(line, "host") == 0) {
            strncpy(rec->host, value, sizeof(rec->host) - 1);
            have_host = (rec->host[0] != '\0');
        } else if (strcmp(line, "platform") == 0) {
            rec->is_ps5 = (strcmp(value, "ps4") != 0);
        } else if (strcmp(line, "registkey") == 0) {
            size_t n = rc_hex_decode(value, rec->registkey, sizeof(rec->registkey));
            if (n != (size_t)-1) {
                rec->registkey_length = n;
                have_registkey = (n > 0);
            }
        } else if (strcmp(line, "companion") == 0) {
            size_t n = rc_hex_decode(value, rec->companion, sizeof(rec->companion));
            have_companion = (n == sizeof(rec->companion));
        } else if (strcmp(line, "deviceid") == 0) {
            size_t n = rc_hex_decode(value, rec->device_id, sizeof(rec->device_id));
            if (n != (size_t)-1)
                rec->device_id_length = n;
        } else if (strcmp(line, "osmajor") == 0) {
            rec->os_major = atoi(value);
        } else if (strcmp(line, "osminor") == 0) {
            rec->os_minor = atoi(value);
        } else if (strcmp(line, "bitrate") == 0) {
            rec->start_bitrate = atoi(value);
        } else if (strcmp(line, "streamingtype") == 0) {
            rec->streaming_type = atoi(value);
        }
    }
    fclose(f);

    if (!have_host || !have_registkey || !have_companion) {
        /* Naming the lengths matters more than it looks - an over-long value fails hex decoding and so
         * presents as a MISSING field, which has already sent one debugging session the wrong way. */
        rc_log("\x1b[31mFAIL\x1b[0m %s needs host, registkey (hex, <=8 bytes) and companion "
               "(hex, exactly 16 bytes / 32 chars)\n", path);
        rc_log("        got: host=%s registkey=%u bytes companion=%s\n",
            have_host ? "yes" : "no", (unsigned)rec->registkey_length,
            have_companion ? "ok" : "missing/wrong length");
        return 0;
    }
    return 1;
}
