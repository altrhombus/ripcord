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
    /*
     * 10,000 matches RipcordSettings.BitrateKbps, which is the .NET client's own default and the number
     * this project has actually settled on. 8,000 was a guess made when ports/common targeted smaller
     * consoles. It goes into the launch spec as bwKbpsSent - a claim the console sizes the stream
     * against - and on the PS3 that claim also decides how finely the console slices each picture, which
     * its decoder has a limit on. 15,000 is measured good there and 30,000 is not; 10,000 is inside both.
     */
    rec->stream_bitrate_kbps = 10000;
    rec->connection_quality = 0;
    /*
     * ON by default now that it has run. The decoder's own colour conversion removes a third of the
     * SPE's arithmetic - 16,444 us a frame to 10,876 - for no measured cost, and the picture was checked
     * on hardware. `decoderrgb=0` turns it off, which is the escape hatch for a decoder that will not
     * produce ARGB32; a port whose decoder cannot is unaffected, since only the vdec backend reads it.
     */
    rec->decoder_rgb = 1;
    rec->bilinear_upscale = 0;
    rec->skip_until_keyframe = 0;
    rec->widescreen = 1;
    rec->smoothing = 1;
    rec->scale_thread = 0;
    rec->dump_video = 0;
    rec->stream_width = 960;
    rec->stream_height = 540;
    rec->fps = 30;
    rec->video_rgb565 = 0;
    rec->probe_resolutions = 0;
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

        if (strcmp(line, "pin") == 0) {
            /* Never logged, never echoed - see the field's note in the header. */
            strncpy(rec->login_pin, value, sizeof(rec->login_pin) - 1);
        } else if (strcmp(line, "host") == 0) {
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
        } else if (strcmp(line, "bilinear") == 0) {
            rec->bilinear_upscale = atoi(value);
        } else if (strcmp(line, "decoderrgb") == 0) {
            rec->decoder_rgb = atoi(value);
        } else if (strcmp(line, "connquality") == 0) {
            rec->connection_quality = atoi(value);
        } else if (strcmp(line, "streambitrate") == 0) {
            rec->stream_bitrate_kbps = atoi(value);
        } else if (strcmp(line, "proberesolutions") == 0) {
            rec->probe_resolutions = atoi(value);
        } else if (strcmp(line, "fps") == 0) {
            rec->fps = atoi(value);
        } else if (strcmp(line, "holdseconds") == 0) {
            rec->hold_seconds = atoi(value);
        } else if (strcmp(line, "hardwarescale") == 0) {
            rec->hardware_scale = atoi(value);
        } else if (strcmp(line, "diagnostics") == 0) {
            rec->diagnostics = atoi(value);
        } else if (strcmp(line, "systemfont") == 0) {
            rec->system_font = atoi(value);
        } else if (strcmp(line, "accountid") == 0) {
            strncpy(rec->account_id, value, sizeof(rec->account_id) - 1);
        } else if (strcmp(line, "videoformat") == 0) {
            rec->video_rgb565 = (strcmp(value, "rgb565") == 0);
        } else if (strcmp(line, "skipuntilkeyframe") == 0) {
            rec->skip_until_keyframe = atoi(value);
        } else if (strcmp(line, "widescreen") == 0) {
            rec->widescreen = atoi(value);
        } else if (strcmp(line, "dumpvideo") == 0) {
            rec->dump_video = atoi(value);
        } else if (strcmp(line, "scalethread") == 0) {
            rec->scale_thread = atoi(value);
        } else if (strcmp(line, "smoothing") == 0) {
            rec->smoothing = atoi(value);
        } else if (strcmp(line, "streamwidth") == 0) {
            rec->stream_width = atoi(value);
        } else if (strcmp(line, "streamheight") == 0) {
            rec->stream_height = atoi(value);
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

/* Hex, lower case, no separators - the form the loader's rc_hex_decode expects back. */
static void write_hex(FILE *f, const char *name, const uint8_t *bytes, size_t length)
{
    size_t i;

    if (length == 0u)
        return;
    fprintf(f, "%s=", name);
    for (i = 0; i < length; i++)
        fprintf(f, "%02x", bytes[i]);
    fputc('\n', f);
}

int halyard_pairing_file_save(const char *argv0, const halyard_pairing_record *rec)
{
    char path[512];
    FILE *f;

    if (rec == NULL)
        return 0;

    rc_program_dir(argv0, path, sizeof(path));
    strncat(path, "pairing.txt", sizeof(path) - strlen(path) - 1);

    f = fopen(path, "w");
    if (f == NULL) {
        /*
         * The path is logged and the CONTENTS never are. That distinction is the whole discipline here:
         * knowing which file could not be written is what someone needs, and the thing inside it is
         * exactly what must not appear in a log that gets pasted into a bug report.
         */
        rc_log("\x1b[31mFAIL\x1b[0m could not write %s\n", path);
        return 0;
    }

    fprintf(f, "# Written by ripcord after pairing. Keep this file; it identifies this client to the\n"
               "# console and cannot be recovered without pairing again.\n");
    fprintf(f, "host=%s\n", rec->host);
    fprintf(f, "platform=%s\n", rec->is_ps5 ? "ps5" : "ps4");
    write_hex(f, "registkey", rec->registkey, rec->registkey_length);
    write_hex(f, "companion", rec->companion, sizeof(rec->companion));
    if (rec->device_id_length > 0u)
        write_hex(f, "deviceid", rec->device_id, rec->device_id_length);

    /*
     * The settings are written back so a pairing does not silently reset choices somebody made. They
     * are only meaningful if the caller LOADED the record first - see the header.
     */
    fprintf(f, "osmajor=%d\n", rec->os_major);
    fprintf(f, "osminor=%d\n", rec->os_minor);
    fprintf(f, "bitrate=%d\n", rec->start_bitrate);
    fprintf(f, "streambitrate=%d\n", rec->stream_bitrate_kbps);
    fprintf(f, "fps=%d\n", rec->fps);
    if (rec->stream_width > 0)
        fprintf(f, "streamwidth=%d\n", rec->stream_width);
    if (rec->stream_height > 0)
        fprintf(f, "streamheight=%d\n", rec->stream_height);
    fprintf(f, "decoderrgb=%d\n", rec->decoder_rgb);
    fprintf(f, "bilinear=%d\n", rec->bilinear_upscale);
    fprintf(f, "hardwarescale=%d\n", rec->hardware_scale);
    fprintf(f, "diagnostics=%d\n", rec->diagnostics);
    fprintf(f, "systemfont=%d\n", rec->system_font);
    if (rec->account_id[0] != '\0')
        fprintf(f, "accountid=%s\n", rec->account_id);
    if (rec->hold_seconds > 0)
        fprintf(f, "holdseconds=%d\n", rec->hold_seconds);
    if (rec->connection_quality)
        fprintf(f, "connquality=%d\n", rec->connection_quality);

    if (fclose(f) != 0) {
        rc_log("\x1b[31mFAIL\x1b[0m could not finish writing %s\n", path);
        return 0;
    }
    return 1;
}
