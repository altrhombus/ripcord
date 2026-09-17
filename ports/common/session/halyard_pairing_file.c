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

/*
 * THE SHARED FIELDS, COPIED FROM ONE ENTRY TO ANOTHER.
 *
 * Written out by name rather than by memcpy-with-holes, because the alternative was a struct copy
 * followed by restoring the per-console fields - and a field added later would then be shared or
 * per-console by accident of where it sat in the struct rather than by anybody deciding. The two lists
 * below ARE the decision, and a new field that appears in neither is a compile-clean mistake, so keep
 * them together with the header's note on what is shared.
 */
static void copy_shared_settings(halyard_pairing_record *dst, const halyard_pairing_record *src)
{
    dst->os_major = src->os_major;
    dst->os_minor = src->os_minor;
    dst->start_bitrate = src->start_bitrate;
    dst->stream_bitrate_kbps = src->stream_bitrate_kbps;
    dst->probe_resolutions = src->probe_resolutions;
    dst->fps = src->fps;
    dst->hold_seconds = src->hold_seconds;
    dst->hardware_scale = src->hardware_scale;
    dst->diagnostics = src->diagnostics;
    dst->system_font = src->system_font;
    dst->video_rgb565 = src->video_rgb565;
    dst->skip_until_keyframe = src->skip_until_keyframe;
    dst->widescreen = src->widescreen;
    dst->smoothing = src->smoothing;
    dst->scale_thread = src->scale_thread;
    dst->dump_video = src->dump_video;
    dst->connection_quality = src->connection_quality;
    dst->decoder_rgb = src->decoder_rgb;
    dst->bilinear_upscale = src->bilinear_upscale;
    dst->stream_width = src->stream_width;
    dst->stream_height = src->stream_height;
    dst->streaming_type = src->streaming_type;
    memcpy(dst->account_id, src->account_id, sizeof(dst->account_id));
}

/* Whether an entry carries the three things a session cannot be opened without. */
static int console_is_usable(const halyard_pairing_record *rec, int companion_ok)
{
    return rec->host[0] != '\0' && rec->registkey_length > 0u && companion_ok;
}

static void pairing_set_path(const char *argv0, char *path, size_t size)
{
    rc_program_dir(argv0, path, size);
    strncat(path, "pairing.txt", size - strlen(path) - 1);
}

int halyard_pairing_file_load_set(const char *argv0, halyard_pairing_set *set)
{
    char path[512];
    FILE *f;
    char line[256];
    halyard_pairing_record shared;
    int companion_ok[HALYARD_PAIRING_MAX_CONSOLES];
    int parsed = 0;          /* how many [console] sections (or the legacy implicit one) were opened */
    int current = -1;        /* the section being filled; -1 means we are in the shared settings */
    int overflowed = 0;
    int want_selected = 0;
    int i, kept;

    if (set == NULL)
        return 0;

    memset(set, 0, sizeof(*set));
    memset(companion_ok, 0, sizeof(companion_ok));
    set->selected = -1;
    pairing_record_defaults(&shared);
    for (i = 0; i < HALYARD_PAIRING_MAX_CONSOLES; i++)
        pairing_record_defaults(&set->console[i]);

    pairing_set_path(argv0, path, sizeof(path));

    f = fopen(path, "r");
    if (f == NULL) {
        rc_log("\x1b[31mFAIL\x1b[0m could not open %s\n", path);
        return 0;
    }

    while (fgets(line, sizeof(line), f) != NULL) {
        char *eq;
        char *value;
        char *trail;
        halyard_pairing_record *rec;

        trail = strpbrk(line, "\r\n");
        if (trail != NULL)
            *trail = '\0';

        /*
         * A NEW CONSOLE STARTS HERE. Anything after this line belongs to it until the next one; keys
         * before the FIRST one are the shared settings - except for console keys, which is what makes a
         * file written by the single-console version load unchanged.
         */
        if (strcmp(line, "[console]") == 0) {
            if (parsed < HALYARD_PAIRING_MAX_CONSOLES) {
                current = parsed++;
            } else {
                /* Counted and reported rather than silently dropped: a file with more consoles than
                 * this build can hold is a fact somebody needs to know, not one to discover. */
                overflowed++;
                current = -1;
            }
            continue;
        }

        eq = strchr(line, '=');
        if (eq == NULL)
            continue;
        *eq = '\0';
        value = eq + 1;

        /*
         * THE PER-CONSOLE KEYS, and the legacy rule in one place: meeting one outside any section opens
         * the implicit first console. That is the whole of the backward compatibility, and it is here
         * rather than in a migration step because a migration is a thing that can fail halfway.
         */
        if (strcmp(line, "host") == 0 || strcmp(line, "name") == 0 ||
            strcmp(line, "platform") == 0 || strcmp(line, "registkey") == 0 ||
            strcmp(line, "companion") == 0 || strcmp(line, "deviceid") == 0 ||
            strcmp(line, "consoleid") == 0 || strcmp(line, "pin") == 0) {
            if (current < 0) {
                if (parsed == 0)
                    current = parsed++;
                else
                    continue;   /* a stray console key after a section ended - not ours to guess at */
            }
            rec = &set->console[current];

            if (strcmp(line, "host") == 0) {
                strncpy(rec->host, value, sizeof(rec->host) - 1);
            } else if (strcmp(line, "name") == 0) {
                strncpy(rec->name, value, sizeof(rec->name) - 1);
            } else if (strcmp(line, "consoleid") == 0) {
                strncpy(rec->console_id, value, sizeof(rec->console_id) - 1);
            } else if (strcmp(line, "platform") == 0) {
                rec->is_ps5 = (strcmp(value, "ps4") != 0);
            } else if (strcmp(line, "registkey") == 0) {
                size_t n = rc_hex_decode(value, rec->registkey, sizeof(rec->registkey));
                if (n != (size_t)-1)
                    rec->registkey_length = n;
            } else if (strcmp(line, "companion") == 0) {
                size_t n = rc_hex_decode(value, rec->companion, sizeof(rec->companion));
                companion_ok[current] = (n == sizeof(rec->companion));
            } else if (strcmp(line, "deviceid") == 0) {
                size_t n = rc_hex_decode(value, rec->device_id, sizeof(rec->device_id));
                if (n != (size_t)-1)
                    rec->device_id_length = n;
            } else {
                /* Never logged, never echoed - see the field's note in the header. */
                strncpy(rec->login_pin, value, sizeof(rec->login_pin) - 1);
            }
            continue;
        }

        /* Everything else is shared, wherever in the file it appears. */
        if (strcmp(line, "selected") == 0) {
            want_selected = atoi(value);
        } else if (strcmp(line, "osmajor") == 0) {
            shared.os_major = atoi(value);
        } else if (strcmp(line, "osminor") == 0) {
            shared.os_minor = atoi(value);
        } else if (strcmp(line, "bitrate") == 0) {
            shared.start_bitrate = atoi(value);
        } else if (strcmp(line, "bilinear") == 0) {
            shared.bilinear_upscale = atoi(value);
        } else if (strcmp(line, "decoderrgb") == 0) {
            shared.decoder_rgb = atoi(value);
        } else if (strcmp(line, "connquality") == 0) {
            shared.connection_quality = atoi(value);
        } else if (strcmp(line, "streambitrate") == 0) {
            shared.stream_bitrate_kbps = atoi(value);
        } else if (strcmp(line, "proberesolutions") == 0) {
            shared.probe_resolutions = atoi(value);
        } else if (strcmp(line, "fps") == 0) {
            shared.fps = atoi(value);
        } else if (strcmp(line, "holdseconds") == 0) {
            shared.hold_seconds = atoi(value);
        } else if (strcmp(line, "hardwarescale") == 0) {
            shared.hardware_scale = atoi(value);
        } else if (strcmp(line, "diagnostics") == 0) {
            shared.diagnostics = atoi(value);
        } else if (strcmp(line, "systemfont") == 0) {
            shared.system_font = atoi(value);
        } else if (strcmp(line, "accountid") == 0) {
            strncpy(shared.account_id, value, sizeof(shared.account_id) - 1);
        } else if (strcmp(line, "videoformat") == 0) {
            shared.video_rgb565 = (strcmp(value, "rgb565") == 0);
        } else if (strcmp(line, "skipuntilkeyframe") == 0) {
            shared.skip_until_keyframe = atoi(value);
        } else if (strcmp(line, "widescreen") == 0) {
            shared.widescreen = atoi(value);
        } else if (strcmp(line, "dumpvideo") == 0) {
            shared.dump_video = atoi(value);
        } else if (strcmp(line, "scalethread") == 0) {
            shared.scale_thread = atoi(value);
        } else if (strcmp(line, "smoothing") == 0) {
            shared.smoothing = atoi(value);
        } else if (strcmp(line, "streamwidth") == 0) {
            shared.stream_width = atoi(value);
        } else if (strcmp(line, "streamheight") == 0) {
            shared.stream_height = atoi(value);
        } else if (strcmp(line, "streamingtype") == 0) {
            shared.streaming_type = atoi(value);
        }
    }
    fclose(f);

    /*
     * INCOMPLETE ENTRIES ARE DROPPED AND COUNTED. A section missing its companion key cannot open a
     * session, and keeping it would put a console on a menu that fails the moment it is chosen. The
     * count is reported because "the file had four and you have three" is the finding.
     */
    kept = 0;
    for (i = 0; i < parsed; i++) {
        if (!console_is_usable(&set->console[i], companion_ok[i]))
            continue;
        if (kept != i)
            set->console[kept] = set->console[i];
        if (want_selected == i)
            set->selected = kept;
        kept++;
    }
    for (i = kept; i < HALYARD_PAIRING_MAX_CONSOLES; i++)
        pairing_record_defaults(&set->console[i]);
    set->count = kept;

    if (kept > 0 && set->selected < 0)
        set->selected = 0;

    /* The shared settings land in every entry - see the header on why they are held many times. */
    for (i = 0; i < HALYARD_PAIRING_MAX_CONSOLES; i++)
        copy_shared_settings(&set->console[i], &shared);

    if (kept == 0) {
        rc_log("\x1b[31mFAIL\x1b[0m %s has no usable console - each needs host, registkey (hex, "
               "<=8 bytes) and companion (hex, exactly 16 bytes / 32 chars)\n", path);
        rc_log("        %d section(s) were read and none were complete\n", parsed);
        return 0;
    }
    if (kept != parsed)
        rc_log("pairing: %d of %d console(s) in %s were incomplete and were skipped\n",
               parsed - kept, parsed, path);
    if (overflowed > 0)
        rc_log("pairing: %s holds more than %d consoles; %d were not read\n", path,
               HALYARD_PAIRING_MAX_CONSOLES, overflowed);
    return kept;
}

void halyard_pairing_set_apply_settings(halyard_pairing_set *set, const halyard_pairing_record *from)
{
    int i;

    if (set == NULL || from == NULL)
        return;
    for (i = 0; i < HALYARD_PAIRING_MAX_CONSOLES; i++)
        copy_shared_settings(&set->console[i], from);
}

int halyard_pairing_set_find(const halyard_pairing_set *set, const char *host)
{
    int i;

    if (set == NULL || host == NULL || host[0] == '\0')
        return -1;
    for (i = 0; i < set->count; i++) {
        if (strcmp(set->console[i].host, host) == 0)
            return i;
    }
    return -1;
}

int halyard_pairing_set_find_id(const halyard_pairing_set *set, const char *console_id)
{
    int i;

    if (set == NULL || console_id == NULL || console_id[0] == '\0')
        return -1;
    for (i = 0; i < set->count; i++) {
        if (set->console[i].console_id[0] != '\0' &&
            strcmp(set->console[i].console_id, console_id) == 0)
            return i;
    }
    return -1;
}

int halyard_pairing_file_readdress(const char *argv0, const char *console_id, const char *new_host)
{
    halyard_pairing_set set;
    int at;

    if (console_id == NULL || new_host == NULL || new_host[0] == '\0')
        return 0;
    if (halyard_pairing_file_load_set(argv0, &set) <= 0)
        return 0;

    at = halyard_pairing_set_find_id(&set, console_id);
    if (at < 0)
        return 0;
    if (strcmp(set.console[at].host, new_host) == 0)
        return 0;   /* already right - not a failure, just nothing to do */

    /*
     * The ADDRESS changes and nothing else does. In particular the keys do not: they belong to the
     * console, not to where it happens to be sitting on the network this week.
     */
    strncpy(set.console[at].host, new_host, sizeof(set.console[at].host) - 1);
    set.console[at].host[sizeof(set.console[at].host) - 1] = '\0';
    rc_log("pairing: a paired console moved - its record now points at where it answered\n");
    return halyard_pairing_file_save_set(argv0, &set);
}

void halyard_pairing_set_select(halyard_pairing_set *set, int index)
{
    if (set == NULL)
        return;
    set->selected = (index >= 0 && index < set->count) ? index : -1;
}

const halyard_pairing_record *halyard_pairing_set_selected(const halyard_pairing_set *set)
{
    if (set == NULL || set->selected < 0 || set->selected >= set->count)
        return NULL;
    return &set->console[set->selected];
}

int halyard_pairing_set_upsert(halyard_pairing_set *set, const halyard_pairing_record *rec)
{
    int at, i;

    if (set == NULL || rec == NULL || rec->host[0] == '\0')
        return -1;

    at = halyard_pairing_set_find(set, rec->host);
    if (at < 0) {
        if (set->count >= HALYARD_PAIRING_MAX_CONSOLES)
            return -1;
        at = set->count++;
    }
    set->console[at] = *rec;
    set->selected = at;

    /* The caller's settings become everyone's - see the header. */
    for (i = 0; i < HALYARD_PAIRING_MAX_CONSOLES; i++)
        copy_shared_settings(&set->console[i], rec);
    return at;
}

int halyard_pairing_set_remove(halyard_pairing_set *set, int index)
{
    int i;

    if (set == NULL || index < 0 || index >= set->count)
        return 0;

    for (i = index; i + 1 < set->count; i++)
        set->console[i] = set->console[i + 1];
    set->count--;
    pairing_record_defaults(&set->console[set->count]);
    if (set->count > 0)
        copy_shared_settings(&set->console[set->count], &set->console[0]);

    /*
     * The selection follows the list rather than the number. Removing the entry above the selected one
     * would otherwise leave the cursor pointing at a different console than the one it was on.
     */
    if (set->count == 0)
        set->selected = -1;
    else if (set->selected > index)
        set->selected--;
    else if (set->selected == index && set->selected >= set->count)
        set->selected = set->count - 1;
    return 1;
}

int halyard_pairing_file_load(const char *argv0, halyard_pairing_record *rec)
{
    halyard_pairing_set set;
    const halyard_pairing_record *chosen;

    if (rec == NULL)
        return 0;
    pairing_record_defaults(rec);

    if (halyard_pairing_file_load_set(argv0, &set) <= 0)
        return 0;
    chosen = halyard_pairing_set_selected(&set);
    if (chosen == NULL)
        return 0;
    *rec = *chosen;
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

int halyard_pairing_file_save_set(const char *argv0, const halyard_pairing_set *set)
{
    char path[512];
    FILE *f;
    const halyard_pairing_record *shared;
    int i;

    if (set == NULL || set->count <= 0)
        return 0;

    pairing_set_path(argv0, path, sizeof(path));

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

    /* Identical in every entry by construction, so any of them will do - see the header. */
    shared = &set->console[0];

    fprintf(f, "# Written by ripcord after pairing. Keep this file; it identifies this client to the\n"
               "# console and cannot be recovered without pairing again.\n");

    /*
     * The shared settings ONCE, at the top, before any [console] line. A reader that predates sections
     * treats every one of these as it always did, and stops at the first [console] having found the
     * settings it wanted - which is the other half of not needing a migration.
     */
    if (set->selected >= 0)
        fprintf(f, "selected=%d\n", set->selected);
    fprintf(f, "osmajor=%d\n", shared->os_major);
    fprintf(f, "osminor=%d\n", shared->os_minor);
    fprintf(f, "bitrate=%d\n", shared->start_bitrate);
    fprintf(f, "streambitrate=%d\n", shared->stream_bitrate_kbps);
    fprintf(f, "fps=%d\n", shared->fps);
    if (shared->stream_width > 0)
        fprintf(f, "streamwidth=%d\n", shared->stream_width);
    if (shared->stream_height > 0)
        fprintf(f, "streamheight=%d\n", shared->stream_height);
    fprintf(f, "decoderrgb=%d\n", shared->decoder_rgb);
    fprintf(f, "bilinear=%d\n", shared->bilinear_upscale);
    fprintf(f, "hardwarescale=%d\n", shared->hardware_scale);
    fprintf(f, "diagnostics=%d\n", shared->diagnostics);
    fprintf(f, "systemfont=%d\n", shared->system_font);
    if (shared->account_id[0] != '\0')
        fprintf(f, "accountid=%s\n", shared->account_id);
    if (shared->hold_seconds > 0)
        fprintf(f, "holdseconds=%d\n", shared->hold_seconds);
    if (shared->connection_quality)
        fprintf(f, "connquality=%d\n", shared->connection_quality);

    for (i = 0; i < set->count; i++) {
        const halyard_pairing_record *rec = &set->console[i];

        fprintf(f, "\n[console]\n");
        fprintf(f, "host=%s\n", rec->host);
        if (rec->name[0] != '\0')
            fprintf(f, "name=%s\n", rec->name);
        if (rec->console_id[0] != '\0')
            fprintf(f, "consoleid=%s\n", rec->console_id);
        fprintf(f, "platform=%s\n", rec->is_ps5 ? "ps5" : "ps4");
        write_hex(f, "registkey", rec->registkey, rec->registkey_length);
        write_hex(f, "companion", rec->companion, sizeof(rec->companion));
        if (rec->device_id_length > 0u)
            write_hex(f, "deviceid", rec->device_id, rec->device_id_length);
    }

    if (fclose(f) != 0) {
        rc_log("\x1b[31mFAIL\x1b[0m could not finish writing %s\n", path);
        return 0;
    }
    return 1;
}

int halyard_pairing_file_save(const char *argv0, const halyard_pairing_record *rec)
{
    halyard_pairing_set set;

    if (rec == NULL)
        return 0;

    /*
     * READ FIRST, so the consoles already in the file survive. The version of this that wrote the record
     * straight out destroyed every other console's keys, silently, and getting them back means standing
     * in front of each one reading a PIN off it again.
     *
     * A failed load is the ordinary first-pairing case and not a reason to stop - it leaves an empty set
     * that the upsert below fills.
     */
    if (halyard_pairing_file_load_set(argv0, &set) <= 0) {
        memset(&set, 0, sizeof(set));
        set.selected = -1;
    }
    if (halyard_pairing_set_upsert(&set, rec) < 0) {
        rc_log("\x1b[31mFAIL\x1b[0m already holding %d console(s) - forget one before pairing another\n",
               HALYARD_PAIRING_MAX_CONSOLES);
        return 0;
    }
    return halyard_pairing_file_save_set(argv0, &set);
}
