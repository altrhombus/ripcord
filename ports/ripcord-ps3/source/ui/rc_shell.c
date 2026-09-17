/* See rc_shell.h. */
#include "rc_shell.h"

#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <unistd.h>

#include <sysutil/sysutil.h>

#include "halyard_pairing_file.h"
#include "rc_build_id.h"
#include "rc_discover.h"
#include "rc_log.h"
#include "rc_menu.h"
#include "rc_overlay.h"
#include "rc_pad_ps3.h"
#include "rc_pair_ps3.h"
#include "rc_platform.h"
#include "rc_video_ps3.h"

/* ------------------------------------------------------------------------------------------------
 * LAYOUT, in the overlay's design pixels - a 1920x1080 screen, converted by rc_overlay_px for
 * whatever the television actually is. Nothing here is a real pixel count, which is the whole point:
 * b265 shipped a panel measured in real pixels and it was wider than a 480p screen.
 */
#define SH_PAD        44
#define SH_HEADER_H   80
#define SH_RULE_H     3
#define SH_SUBTITLE_Y 94
#define SH_ROW_TOP    150
#define SH_ROW_H      60
#define SH_ROWS_MAX   6
#define SH_FOOTER_Y   522                    /* the rule under the list */
#define SH_NOTE_Y     534                    /* what the selected row is for */
#define SH_HINT_Y     566                    /* which buttons do what */

/*
 * THOSE LAST FOUR ARE WRITTEN OUT RATHER THAN DERIVED, and checked against the surface once here.
 *
 * The first version computed the footer from SH_ROW_TOP + SH_ROWS_MAX * SH_ROW_H, which is the obvious
 * thing and put the button hints 22 design pixels BELOW the bottom of the surface - where they are not
 * drawn at all, silently, because every primitive in rc_overlay clips. A layout whose last row falls
 * off the end is invisible in the code and invisible on the screen; the compiler can see it, so let it.
 */
typedef char sh_layout_fits[(SH_ROW_TOP + SH_ROWS_MAX * SH_ROW_H <= SH_FOOTER_Y &&
                             SH_HINT_Y + 26 <= RC_OV_SURFACE_H) ? 1 : -1];

#define SH_SELECT     0xFF1E2B3Du            /* the row under the cursor */
#define SH_BACKDROP   0xFF080A0Eu            /* behind the panel, where there is no picture */

/*
 * HOW LONG A HELD DIRECTION WAITS, and then how fast it goes. 340 ms is long enough that a deliberate
 * single press never repeats and short enough that holding down feels like holding down; 90 ms is about
 * eleven rows a second, which on a list of six is fast without being uncontrollable.
 */
#define SH_REPEAT_DELAY_MS 340u
#define SH_REPEAT_RATE_MS   90u

/* How long a broadcast waits for consoles to answer. Short: this runs while somebody is looking at it. */
#define SH_DISCOVER_MS 1500u

/* ------------------------------------------------------------------------------------------------ */

/*
 * Row ids. Consoles get a block of their own, because how many there are is decided by the network
 * rather than by this file - see rc_menu.h on why rows are addressed by id and not by index.
 */
enum {
    SH_ID_CONNECT = 1,
    SH_ID_PAIR_NEW,
    SH_ID_SEARCH,
    SH_ID_SETTINGS,
    SH_ID_QUIT,
    SH_ID_BACK,

    SH_ID_RESOLUTION = 20,
    SH_ID_FPS,
    SH_ID_BITRATE,
    SH_ID_SCALER,
    SH_ID_SMOOTHING,
    SH_ID_DIAGNOSTICS,

    SH_ID_CONSOLE_BASE = 100   /* + the index into s_found */
};

static rc_menu s_menu;
static rc_discover_result s_found;
static int s_found_count;
static int s_searched;              /* a broadcast has been run at least once this session */

static halyard_pairing_record s_record;
static int s_have_record;
static const char *s_record_dir;    /* where it was loaded from, so it is saved back to the same place */

static int s_first_row;             /* the top of the visible window - see ensure_visible */
static uint32_t s_prev_buttons;
static uint64_t s_repeat_at;

/* ------------------------------------------------------------------------------------------------
 * The record
 */

/*
 * LOADED THE WAY rc_connect LOADS IT, from the same ordered list, because a shell that edits one file
 * while the connect path reads another is worse than no shell: every setting would appear to be
 * ignored, and nothing on either side would look wrong.
 */
static void load_record(const char *const *dirs, int dir_count)
{
    int i;

    memset(&s_record, 0, sizeof(s_record));
    s_have_record = 0;
    s_record_dir = NULL;

    for (i = 0; i < dir_count && !s_have_record; i++) {
        if (halyard_pairing_file_load(dirs[i], &s_record)) {
            s_have_record = 1;
            s_record_dir = dirs[i];
        }
    }
    if (!s_have_record) {
        /*
         * The loader defaults every optional field whether or not it found a file - but to ports/common's
         * defaults, which are a 3DS's. Overwritten here with this port's measured ones so the settings
         * screen opens showing what an unpaired PS3 would actually stream at, rather than 960x540 at 30.
         */
        rc_pair_apply_port_defaults(&s_record);
        s_record_dir = rc_pair_record_dir();
        rc_log("shell: no pairing record yet\n");
    } else {
        rc_log("shell: pairing record loaded\n");
    }
}

static void save_record(void)
{
    const char *dir = (s_record_dir != NULL) ? s_record_dir : rc_pair_record_dir();

    if (halyard_pairing_file_save(dir, &s_record))
        rc_log("shell: settings saved\n");
    else
        rc_log("shell: settings could NOT be saved\n");
}

/* ------------------------------------------------------------------------------------------------
 * Drawing
 */

static void draw_row(const rc_menu_item *item, int y, int selected)
{
    int w = rc_overlay_surface_width();
    uint32_t label_colour = item->enabled ? RC_OV_TEXT : RC_OV_LABEL;
    int text_y;

    if (selected) {
        rc_overlay_rect(rc_overlay_px(SH_PAD - 12), y, w - rc_overlay_px((SH_PAD - 12) * 2),
                        rc_overlay_px(SH_ROW_H - 8), SH_SELECT);
        rc_overlay_rect(rc_overlay_px(SH_PAD - 12), y, rc_overlay_px(5),
                        rc_overlay_px(SH_ROW_H - 8), RC_OV_ACCENT);
    }

    /* Baselines rather than boxes, so the value beside the label sits on the same line as it. */
    text_y = y + rc_overlay_px(12);
    (void)rc_overlay_text(rc_overlay_px(SH_PAD + 6), text_y, 2, label_colour, "%s", item->label);

    if (item->value[0] != '\0') {
        /*
         * Right-aligned, and on an adjustable row wearing the arrows that say so. A row that changes
         * when you press left is indistinguishable from one that does not until you press left, which
         * is exactly the kind of thing nobody presses on a television.
         *
         * ONE RUN RATHER THAN THREE. The arrows are in the same string as the value because measuring
         * a proportional run to place something beside it means asking the font for a width, and the
         * two things this file could ask - the monospaced number width and the proportional advance -
         * disagree. A single right-aligned run cannot be misaligned by either.
         */
        int right = w - rc_overlay_px(SH_PAD);
        int baseline = text_y + rc_overlay_ascent(2) - rc_overlay_ascent(1);

        if (item->adjustable && selected)
            rc_overlay_text_right(right, baseline, 1, RC_OV_ACCENT, "< %s >", item->value);
        else
            rc_overlay_text_right(right, baseline, 1,
                                  item->enabled ? RC_OV_LABEL : RC_OV_TRACK, "%s", item->value);
    }
}

/*
 * Keeps the cursor inside the visible window. Six rows fit; a home screen with a paired console, three
 * that answered a broadcast and four commands does not.
 */
static void ensure_visible(void)
{
    if (s_menu.selected < 0) {
        s_first_row = 0;
        return;
    }
    if (s_menu.selected < s_first_row)
        s_first_row = s_menu.selected;
    else if (s_menu.selected >= s_first_row + SH_ROWS_MAX)
        s_first_row = s_menu.selected - SH_ROWS_MAX + 1;

    if (s_first_row > s_menu.count - SH_ROWS_MAX)
        s_first_row = s_menu.count - SH_ROWS_MAX;
    if (s_first_row < 0)
        s_first_row = 0;
}

/*
 * WHAT WAS ON THE SCREEN LAST TIME, so a menu nobody is touching costs nothing.
 *
 * The loop below runs at the flip rate whether or not anything happened, and rebuilding the surface
 * means clearing 960x600 pixels, rasterising every run on it and copying the result to video memory.
 * Doing that sixty times a second for a picture that is identical is the same mistake the diagnostics
 * panel already found once - see rc_overlay_end on why that copy is gated. The blit still happens every
 * frame, because the back buffer is cleared every frame; it is the REBUILD that is skipped.
 */
static unsigned s_drawn_revision;
static int s_drawn_first_row = -1;
static const char *s_drawn_hint = NULL;
static int s_drawn_valid;

static void draw(const char *hint)
{
    rc_video_info info;
    int w, h, x, y, i, shown;

    if (!rc_video_info_get(&info))
        return;

    w = rc_overlay_surface_width();
    h = rc_overlay_surface_height();
    x = (info.width - w) / 2;
    y = (info.height - h) / 2;
    ensure_visible();

    /*
     * WAIT FOR THE LAST FLIP BEFORE QUEUING ANOTHER, which the streaming path does not do and must
     * not: there, a flip that has not landed means skip a frame, because the thread is also draining a
     * socket and blocking it loses packets. Here there is no socket and nothing else to do, and a loop
     * that queues flips as fast as the PPE can issue them would run this menu at several thousand
     * frames a second to no visible effect whatsoever. Bounded, because b232 left the RSX stopped with
     * every flip pending forever - a menu that waits for a flip that will never complete is a hang.
     */
    {
        uint64_t give_up = rc_time_ms() + 100u;

        while (!rc_video_present_ready() && rc_time_ms() < give_up)
            usleep(2000);
    }

    if (s_drawn_valid && s_menu.revision == s_drawn_revision && s_first_row == s_drawn_first_row &&
        hint == s_drawn_hint) {
        rc_video_clear_back(SH_BACKDROP);
        rc_overlay_end_now(x, y, w, h);
        rc_video_flip();
        return;
    }

    if (!rc_overlay_begin_now())
        return;
    s_drawn_revision = s_menu.revision;
    s_drawn_first_row = s_first_row;
    s_drawn_hint = hint;
    s_drawn_valid = 1;

    rc_overlay_rect(0, 0, w, h, RC_OV_PANEL);
    rc_overlay_rect(0, 0, w, rc_overlay_px(SH_HEADER_H), RC_OV_HEADER);
    rc_overlay_rect(0, rc_overlay_px(SH_HEADER_H), w, rc_overlay_px(SH_RULE_H), RC_OV_ACCENT);
    rc_overlay_rect(0, 0, 1, h, RC_OV_EDGE);
    rc_overlay_rect(w - 1, 0, 1, h, RC_OV_EDGE);
    rc_overlay_rect(0, h - 1, w, 1, RC_OV_EDGE);

    (void)rc_overlay_text(rc_overlay_px(SH_PAD), rc_overlay_px(22), 2, RC_OV_TEXT, "%s",
                          s_menu.title);
    rc_overlay_text_right(w - rc_overlay_px(SH_PAD), rc_overlay_px(30), 1, RC_OV_LABEL, "%s",
                          RC_PS3_BUILD_ID);

    if (s_menu.subtitle[0] != '\0')
        (void)rc_overlay_text(rc_overlay_px(SH_PAD), rc_overlay_px(SH_SUBTITLE_Y), 1, RC_OV_LABEL,
                              "%s", s_menu.subtitle);

    shown = s_menu.count - s_first_row;
    if (shown > SH_ROWS_MAX)
        shown = SH_ROWS_MAX;

    for (i = 0; i < shown; i++) {
        int index = s_first_row + i;

        draw_row(&s_menu.item[index], rc_overlay_px(SH_ROW_TOP + i * SH_ROW_H),
                 index == s_menu.selected);
    }

    /*
     * A LIST THAT CONTINUES SAYS SO. Without this a seventh console is simply not on the screen, and
     * the only way to find out it is there is to press down and watch the rows move.
     */
    if (s_menu.count > SH_ROWS_MAX) {
        int track_x = w - rc_overlay_px(SH_PAD - 22);
        int track_y = rc_overlay_px(SH_ROW_TOP);
        int track_h = rc_overlay_px(SH_ROWS_MAX * SH_ROW_H - 8);
        int thumb_h = track_h * SH_ROWS_MAX / s_menu.count;
        int thumb_y = track_y + track_h * s_first_row / s_menu.count;

        rc_overlay_rect(track_x, track_y, rc_overlay_px(4), track_h, RC_OV_TRACK);
        rc_overlay_rect(track_x, thumb_y, rc_overlay_px(4), thumb_h, RC_OV_EDGE);
    }

    rc_overlay_rect(rc_overlay_px(SH_PAD), rc_overlay_px(SH_FOOTER_Y),
                    w - rc_overlay_px(SH_PAD * 2), 1, RC_OV_EDGE);

    {
        const rc_menu_item *item = rc_menu_selected(&s_menu);

        if (item != NULL && item->note[0] != '\0')
            (void)rc_overlay_text(rc_overlay_px(SH_PAD), rc_overlay_px(SH_NOTE_Y), 1, RC_OV_TEXT,
                                  "%s", item->note);
    }
    if (hint != NULL)
        (void)rc_overlay_text(rc_overlay_px(SH_PAD), rc_overlay_px(SH_HINT_Y), 1, RC_OV_LABEL,
                              "%s", hint);

    rc_video_clear_back(SH_BACKDROP);
    rc_overlay_end_now(x, y, w, h);
    rc_video_flip();
}

/* ------------------------------------------------------------------------------------------------
 * Input
 */

/*
 * Buttons that went down since the last poll, with auto-repeat on the directions.
 *
 * EDGES RATHER THAN STATE because the loop runs at whatever rate it runs at: a menu driven by "is Cross
 * held" opens six things per press. Repeat is on the d-pad only - a held Cross must never fire twice,
 * which is how a confirmation gets past somebody.
 */
static uint32_t read_edges(void)
{
    const uint32_t directions = HALYARD_PAD_DPAD_UP | HALYARD_PAD_DPAD_DOWN |
                                HALYARD_PAD_DPAD_LEFT | HALYARD_PAD_DPAD_RIGHT;
    halyard_input_state pad;
    uint32_t now = 0u, edges, held;
    uint64_t t = rc_time_ms();

    if (rc_pad_read(&pad))
        now = pad.buttons;

    edges = now & ~s_prev_buttons;
    held = now & directions;

    if ((edges & directions) != 0u) {
        s_repeat_at = t + SH_REPEAT_DELAY_MS;
    } else if (held == 0u) {
        s_repeat_at = 0u;
    } else if (s_repeat_at != 0u && t >= s_repeat_at) {
        edges |= held;
        s_repeat_at = t + SH_REPEAT_RATE_MS;
    }

    s_prev_buttons = now;
    return edges;
}

/* Drops whatever is currently held, so returning from a sub-screen does not act on the press that left
 * it. Without this, Circle out of the settings screen arrives at the home screen as a fresh Circle. */
static void forget_held(void)
{
    halyard_input_state pad;

    s_prev_buttons = rc_pad_read(&pad) ? pad.buttons : 0u;
    s_repeat_at = 0u;
}

/* ------------------------------------------------------------------------------------------------
 * The home screen
 */

static void search(void)
{
    int i;

    rc_menu_reset(&s_menu, "Ripcord", "Searching for consoles...");
    draw(NULL);

    s_found_count = rc_discover(SH_DISCOVER_MS, &s_found);
    s_searched = 1;
    rc_log("shell: %d console(s) answered\n", s_found_count);
    for (i = 0; i < s_found_count; i++) {
        /* The name and the state, never the address - a log leaves this console and that line is the
         * one that would carry somebody's network into it. */
        rc_log("shell:   \"%s\" %s\n", s_found.console[i].host_name,
               s_found.console[i].is_awake ? "ready" : "in standby");
    }
}

/* Whether a discovered console is the one the record is already paired with. */
static int is_paired_console(int index)
{
    return s_have_record && s_record.host[0] != '\0' &&
           strcmp(s_found.console[index].address, s_record.host) == 0;
}

static void build_home(void)
{
    char subtitle[RC_MENU_NOTE_MAX];
    char value[RC_MENU_VALUE_MAX];
    char label[RC_MENU_LABEL_MAX];
    int row, i;

    if (!s_searched)
        snprintf(subtitle, sizeof(subtitle), "%s",
                 s_have_record ? "One console is paired with this PS3"
                               : "No console is paired with this PS3 yet");
    else if (s_found_count == 1)
        snprintf(subtitle, sizeof(subtitle), "One console answered on this network");
    else
        snprintf(subtitle, sizeof(subtitle), "%d consoles answered on this network", s_found_count);

    rc_menu_reset(&s_menu, "Ripcord", subtitle);

    /*
     * THE PAIRED CONSOLE FIRST, and it is on the screen even when it did not answer - a console in
     * standby on a different switch is still the one this PS3 is paired with, and a home screen that
     * hides it while it is asleep is a home screen that looks empty most of the time.
     */
    if (s_have_record) {
        int awake = -1;

        for (i = 0; i < s_found_count; i++) {
            if (is_paired_console(i))
                awake = s_found.console[i].is_awake;
        }
        snprintf(label, sizeof(label), "%s", "Start streaming");
        snprintf(value, sizeof(value), "%s",
                 (awake < 0) ? (s_searched ? "not seen" : "paired") : (awake ? "ready" : "standby"));
        (void)rc_menu_add(&s_menu, SH_ID_CONNECT, label, value,
                          (awake == 0) ? "It is in standby - Ripcord will wake it first"
                                       : "Connect to the console this PS3 is paired with");
    } else {
        row = rc_menu_add(&s_menu, SH_ID_CONNECT, "Start streaming", "not paired",
                          "Pair with a console first - there is nothing to connect to yet");
        rc_menu_set_enabled(&s_menu, row, 0);
    }

    /* Anything that answered and is NOT the paired one. Cross pairs with it, with the address filled
     * in already, which is the one part of pairing a broadcast can do for you. */
    for (i = 0; i < s_found_count && i < RC_DISCOVER_MAX; i++) {
        if (is_paired_console(i))
            continue;
        snprintf(label, sizeof(label), "%s",
                 s_found.console[i].host_name[0] != '\0' ? s_found.console[i].host_name
                                                         : "A PlayStation");
        snprintf(value, sizeof(value), "%s", s_found.console[i].is_awake ? "ready" : "standby");
        (void)rc_menu_add(&s_menu, SH_ID_CONSOLE_BASE + i, label, value,
                          "Not paired yet - press X to link this PS3 to it");
    }

    (void)rc_menu_add(&s_menu, SH_ID_SEARCH, "Search the network", NULL,
                      "Ask every console on this network to answer");
    (void)rc_menu_add(&s_menu, SH_ID_PAIR_NEW, "Pair by address", NULL,
                      "Type the console's address yourself, if it did not answer");
    (void)rc_menu_add(&s_menu, SH_ID_SETTINGS, "Settings", NULL,
                      "Picture size, frame rate and how much bandwidth to ask for");
    (void)rc_menu_add(&s_menu, SH_ID_QUIT, "Quit to the XMB", NULL,
                      "Close Ripcord and go back to the menu");
}

/* ------------------------------------------------------------------------------------------------
 * Settings
 *
 * EVERY CHOICE HERE IS ONE THIS CONSOLE WAS MEASURED AT. The lists are short on purpose: a setting
 * whose values are a free-form number is a setting somebody can put the console into a state nothing
 * was ever tested in, and the interesting range on this machine turned out to be narrow. DECODE.md
 * carries the measurements behind each one.
 */

typedef struct { int w, h; } sh_resolution;

/*
 * 1920x1080 IS DELIBERATELY ABSENT. cellVdec opens at level 4.2, whose decoded-picture buffer is not
 * large enough for 1080p at the reference counts the console encodes with - so asking for it produces
 * a decoder that opens and then fails on the first picture. An option that cannot work is worse than
 * no option, because the person who picks it concludes the client is broken.
 */
static const sh_resolution SH_RESOLUTIONS[] = { { 640, 360 }, { 960, 540 }, { 1280, 720 } };
static const int SH_BITRATES[] = { 2000, 4000, 6000, 8000, 10000, 15000, 20000, 25000, 30000 };
static const int SH_RATES[] = { 30, 60 };

#define SH_COUNT(a) ((int)(sizeof(a) / sizeof((a)[0])))

/* Steps through a list of ints and returns the new value. Wraps, like the cursor does. */
static int step_list(const int *values, int count, int current, int delta)
{
    int at = 0, i;

    for (i = 0; i < count; i++) {
        if (values[i] == current)
            at = i;
    }
    at += delta;
    if (at >= count)
        at = 0;
    else if (at < 0)
        at = count - 1;
    return values[at];
}

static void step_resolution(int delta)
{
    int at = SH_COUNT(SH_RESOLUTIONS) - 1, i;

    for (i = 0; i < SH_COUNT(SH_RESOLUTIONS); i++) {
        if (SH_RESOLUTIONS[i].w == s_record.stream_width &&
            SH_RESOLUTIONS[i].h == s_record.stream_height)
            at = i;
    }
    at += delta;
    if (at >= SH_COUNT(SH_RESOLUTIONS))
        at = 0;
    else if (at < 0)
        at = SH_COUNT(SH_RESOLUTIONS) - 1;
    s_record.stream_width = SH_RESOLUTIONS[at].w;
    s_record.stream_height = SH_RESOLUTIONS[at].h;
}

/* kbps as somebody would say it out loud. 20000 is "20 Mbps"; 2500 would be "2.5 Mbps". */
static void bitrate_text(int kbps, char *out, size_t size)
{
    if (kbps % 1000 == 0)
        snprintf(out, size, "%d Mbps", kbps / 1000);
    else
        snprintf(out, size, "%d.%d Mbps", kbps / 1000, (kbps % 1000) / 100);
}

static void refresh_settings_values(void)
{
    char text[RC_MENU_VALUE_MAX];
    int i;

    for (i = 0; i < s_menu.count; i++) {
        switch (s_menu.item[i].id) {
        case SH_ID_RESOLUTION:
            snprintf(text, sizeof(text), "%dx%d", s_record.stream_width, s_record.stream_height);
            break;
        case SH_ID_FPS:
            snprintf(text, sizeof(text), "%d fps", s_record.fps);
            break;
        case SH_ID_BITRATE:
            bitrate_text(s_record.stream_bitrate_kbps, text, sizeof(text));
            break;
        case SH_ID_SCALER:
            snprintf(text, sizeof(text), "%s", s_record.hardware_scale ? "RSX" : "SPE cores");
            break;
        case SH_ID_SMOOTHING:
            snprintf(text, sizeof(text), "%s",
                     (s_record.bilinear_upscale == 0) ? "Sharp"
                   : (s_record.bilinear_upscale == 1) ? "Smooth" : "Smooth both ways");
            break;
        case SH_ID_DIAGNOSTICS:
            snprintf(text, sizeof(text), "%s", s_record.diagnostics ? "On" : "Off");
            break;
        default:
            continue;
        }
        rc_menu_set_value(&s_menu, i, text);
    }
}

static void build_settings(void)
{
    int row;

    rc_menu_reset(&s_menu, "Settings", "Left and right change a setting");

    row = rc_menu_add(&s_menu, SH_ID_RESOLUTION, "Picture size", NULL,
                      "What to ask the console to encode. 1280x720 is this decoder's ceiling");
    rc_menu_set_adjustable(&s_menu, row, 1);
    row = rc_menu_add(&s_menu, SH_ID_FPS, "Frame rate", NULL,
                      "60 is measured good here; 30 is the one to try if the picture breaks up");
    rc_menu_set_adjustable(&s_menu, row, 1);
    row = rc_menu_add(&s_menu, SH_ID_BITRATE, "Bandwidth", NULL,
                      "Asking for more than 20 Mbps makes this decoder drop whole pictures");
    rc_menu_set_adjustable(&s_menu, row, 1);
    row = rc_menu_add(&s_menu, SH_ID_SCALER, "Scaling", NULL,
                      "The RSX does it in 113 us a frame; the SPE cores take 3,204");
    rc_menu_set_adjustable(&s_menu, row, 1);
    row = rc_menu_add(&s_menu, SH_ID_SMOOTHING, "Smoothing", NULL,
                      "Costs nothing on the RSX. Smooth both ways is too slow for 60 fps");
    rc_menu_set_adjustable(&s_menu, row, 1);
    row = rc_menu_add(&s_menu, SH_ID_DIAGNOSTICS, "Diagnostics overlay", NULL,
                      "Frame rate and loss over the picture. Options and Create toggles it mid-stream");
    rc_menu_set_adjustable(&s_menu, row, 1);
    /*
     * NO TYPEFACE ROW, and that is a finding rather than an oversight. The atlas is built once, when
     * the overlay is prepared, which happens before this screen can be reached - so a control here
     * would move a field in the record and change nothing anyone could see until the next launch. A
     * setting that appears to do nothing is worse than an absent one.
     */
    (void)rc_menu_add(&s_menu, SH_ID_BACK, "Done", NULL, "Save these and go back");
    refresh_settings_values();
}

/* Left or right on the selected row. Returns 1 if something changed. */
static int adjust(int delta)
{
    const rc_menu_item *item = rc_menu_selected(&s_menu);

    if (item == NULL || !item->adjustable)
        return 0;

    switch (item->id) {
    case SH_ID_RESOLUTION:   step_resolution(delta); break;
    case SH_ID_FPS:          s_record.fps = step_list(SH_RATES, SH_COUNT(SH_RATES), s_record.fps,
                                                      delta); break;
    case SH_ID_BITRATE:      s_record.stream_bitrate_kbps =
                                 step_list(SH_BITRATES, SH_COUNT(SH_BITRATES),
                                           s_record.stream_bitrate_kbps, delta); break;
    case SH_ID_SCALER:       s_record.hardware_scale = !s_record.hardware_scale; break;
    case SH_ID_SMOOTHING:    s_record.bilinear_upscale =
                                 (s_record.bilinear_upscale + (delta > 0 ? 1 : 2)) % 3; break;
    case SH_ID_DIAGNOSTICS:  s_record.diagnostics = !s_record.diagnostics; break;
    default:                 return 0;
    }
    refresh_settings_values();
    return 1;
}

static void run_settings(void)
{
    int running = 1;

    build_settings();
    s_first_row = 0;
    forget_held();

    while (running) {
        uint32_t edges;

        sysUtilCheckCallback();
        edges = read_edges();

        if (edges & HALYARD_PAD_DPAD_UP)
            (void)rc_menu_move(&s_menu, -1);
        if (edges & HALYARD_PAD_DPAD_DOWN)
            (void)rc_menu_move(&s_menu, 1);
        if (edges & HALYARD_PAD_DPAD_LEFT)
            (void)adjust(-1);
        if (edges & HALYARD_PAD_DPAD_RIGHT)
            (void)adjust(1);
        if (edges & HALYARD_PAD_CROSS) {
            if (rc_menu_selected_id(&s_menu) == SH_ID_BACK)
                running = 0;
            else
                (void)adjust(1);
        }
        if (edges & HALYARD_PAD_CIRCLE)
            running = 0;

        draw("X  change      O  back");
    }

    /*
     * SAVED ON THE WAY OUT, both ways out. Circle is "go back", not "discard" - there is no
     * cancel here to discard to, and a settings screen that silently throws away what was just
     * changed is the more surprising of the two behaviours.
     */
    save_record();
}

/* ------------------------------------------------------------------------------------------------ */

rc_shell_action rc_shell_run(const char *const *dirs, int dir_count)
{
    rc_shell_action action = RC_SHELL_QUIT;
    int running = 1;

    rc_log("shell: opening\n");
    if (!rc_pad_open())
        rc_log("shell: no pad - the menu cannot be driven\n");

    /*
     * The overlay may already be prepared, in which case this is a no-op. Asked for anyway: the shell
     * is the first thing on the screen and has nothing to draw with until it exists.
     */
    rc_overlay_set(1);
    if (!rc_overlay_prepared()) {
        /*
         * NOTHING TO DRAW ON. Said rather than returned silently - a shell that cannot draw looks
         * exactly like a shell that was never called, and the difference is the whole diagnosis.
         */
        rc_log("shell: the overlay is not prepared - going straight to the connect path\n");
        return RC_SHELL_CONNECT;
    }

    load_record(dirs, dir_count);
    build_home();
    forget_held();

    while (running) {
        uint32_t edges;

        sysUtilCheckCallback();
        edges = read_edges();

        if (edges & HALYARD_PAD_DPAD_UP)
            (void)rc_menu_move(&s_menu, -1);
        if (edges & HALYARD_PAD_DPAD_DOWN)
            (void)rc_menu_move(&s_menu, 1);

        if (edges & HALYARD_PAD_CROSS) {
            int id = rc_menu_selected_id(&s_menu);

            switch (id) {
            case SH_ID_CONNECT:
                action = RC_SHELL_CONNECT;
                running = 0;
                break;

            case SH_ID_SEARCH:
                search();
                build_home();
                (void)rc_menu_select_id(&s_menu, SH_ID_SEARCH);
                forget_held();
                break;

            case SH_ID_PAIR_NEW:
                /*
                 * rc_pair_run owns the whole flow including its own screens, and leaves its outcome on
                 * the television. The record is reloaded afterwards because pairing wrote one.
                 */
                (void)rc_pair_run(NULL);
                load_record(dirs, dir_count);
                build_home();
                forget_held();
                break;

            case SH_ID_SETTINGS:
                run_settings();
                build_home();
                (void)rc_menu_select_id(&s_menu, SH_ID_SETTINGS);
                forget_held();
                break;

            case SH_ID_QUIT:
                running = 0;
                break;

            default:
                if (id >= SH_ID_CONSOLE_BASE && id < SH_ID_CONSOLE_BASE + RC_DISCOVER_MAX) {
                    /* Its address is already known, so the one question a broadcast can answer is not
                     * asked again. */
                    (void)rc_pair_run(s_found.console[id - SH_ID_CONSOLE_BASE].address);
                    load_record(dirs, dir_count);
                    build_home();
                    forget_held();
                }
                break;
            }
        }

        if (running)
            draw("X  select");
    }

    rc_log("shell: closing - %s\n", action == RC_SHELL_CONNECT ? "connecting" : "quitting");
    return action;
}
