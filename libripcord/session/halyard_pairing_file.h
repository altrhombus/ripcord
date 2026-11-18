/*
 * libripcord - the pairing-record file: the paired consoles and the stream settings.
 *
 * Reads and writes "pairing.txt" beside the running program (rc_program_dir of argv0). A plain
 * key=value text file, written by registration (halyard_pairing_file_save) or by hand from
 * `ProtocolLab -- register`'s output. Shared settings come first; each console is a `[console]` line
 * followed by its own keys. Console keys met before any `[console]` line open an implicit first
 * console. Per console:
 *
 *   host=192.168.1.42          (required - the console's LAN address)
 *   registkey=1a2b3c4d5e6f0011 (required - hex, at most 8 bytes)
 *   companion=...32 hex chars  (required - hex, exactly 16 bytes)
 *   platform=ps5               (optional, "ps5" or "ps4"; default ps5)
 *   deviceid=...hex            (optional - up to 16 bytes; defaults to all-zero)
 *   name=...                   (optional - what discovery called it, for a menu)
 *   consoleid=...              (optional - the console's own id; see halyard_pairing_file_readdress)
 *   pin=...                    (optional - the sign-in passcode; see login_pin below)
 *
 * Shared by every console, with the defaults shown:
 *
 *   selected=0                 (index of the console a session uses)
 *   osmajor=10, osminor=0, bitrate=10000, streamingtype=0
 *   streambitrate=10000        (the launch spec's bwKbpsSent, in kbps)
 *   skipuntilkeyframe=0        (1 drops every frame after a loss until the next IDR)
 *   fps=30                     (30 or 60)
 *   holdseconds=0              (how long to hold the stream; 0 keeps the port default.
 *                               Minutes are what a thermal comparison needs; seconds do for frame rate)
 *   hardwarescale=0            (1 scales on the GPU instead of the CPU, where the port has
 *                               one; `bilinear` then costs nothing, since the filter is wired)
 *   bilinear=0                 (0 nearest, 1 along the row, 2 both directions; see bilinear_upscale)
 *   decoderrgb=1               (ask the decoder for packed RGB where it can; see decoder_rgb)
 *   connquality=0              (1 sends CONNECTION_QUALITY reports; see connection_quality)
 *   restondisconnect=0         (0 ask, 1 rest the console, 2 leave it awake)
 *   diagnostics=0              (1 draws the diagnostics overlay over the stream)
 *   systemfont=0               (1 tries the platform's own font for the overlay)
 *   accountid=...              (the PSN account id, remembered from pairing so that a
 *                               nineteen-digit number need only be typed once)
 *   videoformat=bgr565         ("bgr565" or "rgb565"; see below)
 *   widescreen=1               (800x240 top screen; see below)
 *   smoothing=1                (average the two source rows the vertical squeeze straddles)
 *   scalethread=0              (DO NOT ENABLE on the 3DS; see below)
 *   dumpvideo=0                (1 writes the H.264 elementary stream to video.264, 2 MB cap,
 *                               to decode on a PC with ffmpeg; diagnostic only, costs SD writes
 *                               on the receive thread)
 *   streamwidth=960            (what to ASK the console to encode; ladder values only:
 *   streamheight=540            640x360, 960x540, 1280x720, 1920x1080)
 *   proberesolutions=0         (1 asks the console for a series of resolutions and reports what it
 *                               accepts, instead of streaming; see
 *                               ports/ripcord-3ds/source/connect/main.c)
 *
 * This loader stores the values and applies no range check; the port that reads a field decides what
 * it does with it. Several keys (videoformat, widescreen, smoothing, scalethread, dumpvideo) are read
 * by the 3DS port only.
 *
 * `bitrate` and `streambitrate` are NOT the same field. `bitrate` is the /sess/ctrl RP-StartBitrate
 * header; `streambitrate` is what the launch spec asks the console to actually send.
 *
 * [X] WHETHER `streambitrate` MOVES THE ENCODER is unconfirmed on the 3DS: measured throughput sat near
 * 0.9 Mbps whether 2000, 3500 or 6000 was asked for. (Loss figures taken there before the stream socket
 * set SO_RCVBUF measure the port's own buffer overrun, not the link: with SO_RCVBUF set the same link
 * loses one unit in 5,489.) The connect probe prints what arrived, in Mbps and bits/pixel; run it once
 * high and once low and compare those.
 *
 * `widescreen` and `smoothing` cost nothing on the wire. Every ladder rung is wider than a 400-column
 * screen (640 columns at the smallest), so 400x240 mode point-samples columns away, which makes
 * on-screen text unreadable; 800x240 mode keeps every column of a 640-wide stream. `smoothing` addresses
 * the vertical axis, at real CPU cost; watch the profile and the loss figure together, because the 3DS
 * port shares one core with the receive loop.
 *
 * `scalethread` IS OFF BECAUSE IT RACES THE DECODER. Moving the frame scale to core 2 took core 0 from
 * 77% to 27%, but the scale reads MVD's output buffer while core 0 keeps feeding MVD, which decodes the
 * NEXT picture into that same buffer. A buffer read mid-decode shows as a uniform mid-grey field with
 * only the macroblocks written so far carrying real content.
 *
 * `streamwidth`/`streamheight` default to 960x540. The console's UI is authored at 1080p, so a 360p
 * encode destroys small text before it reaches the wire. 540p exercises a decoder's multi-slice path
 * harder than 360p: the console sends one slice per MTU.
 *
 * On the 3DS it costs roughly 2.25x the decode and scale. At 640x360 and 60 fps the receive core runs at
 * 84%, so 540p is a 30 fps proposition there. Drop fps to 30 if loss appears. Halving bits/pixel is not
 * the limit: a PC decode of a real capture shows the PS5 home screen fully legible at 0.10 bits/pixel.
 * What limits text is the 240-row screen, and more source rows is the only lever.
 *
 * `videoformat` exists because MVD can emit either byte order and the 3DS top screen is configured
 * RGB565. The devkitPro example pairs MVD_OUTPUT_BGR565 with an RGB565 screen, which is the default
 * here - if reds and blues look swapped, this is the setting, not the blit.
 *
 * Lives in the core rather than inside a program's main.c because every program that opens a session
 * reads the same file and must agree on what a valid record is.
 *
 * ON THE LENGTH RULES: rc_hex_decode returns "bad" for malformed AND for over-long input, and this
 * loader reports the three required fields in one message, so an over-long registkey reports as an
 * incomplete console. The message names lengths explicitly for that reason - see
 * ports/ripcord-3ds/HARDWARE-PROBES.md.
 */
#ifndef HALYARD_PAIRING_FILE_H
#define HALYARD_PAIRING_FILE_H

#include <stddef.h>
#include <stdint.h>

#include "halyard_sess_fields.h"

#define HALYARD_PAIRING_HOST_MAX 64
#define HALYARD_PAIRING_NAME_MAX 32
#define HALYARD_PAIRING_CONSOLE_ID_MAX 32
#define HALYARD_PAIRING_REGISTKEY_MAX 8
#define HALYARD_PAIRING_COMPANION_LENGTH 16
#define HALYARD_PAIRING_DEVICE_ID_MAX 16

typedef struct {
    char host[HALYARD_PAIRING_HOST_MAX];

    /*
     * What discovery called it, kept so a list of paired consoles can be read by a human. Never goes on
     * the wire and is not identity - the console is addressed by `host` and proved by the keys below.
     * It is here because "192.168.1.42" and "192.168.1.43" is not a menu anybody can choose from.
     */
    char name[HALYARD_PAIRING_NAME_MAX];

    /*
     * The console's own id, as discovery returns it. `host` is a DHCP lease and can change; the id does
     * not, so a record that carries one can be re-addressed (halyard_pairing_file_readdress) instead of
     * re-paired.
     *
     * Empty for a record with no `consoleid` key, such as one paired by hand from a typed address. That
     * is a normal state: the console cannot be found again if it moves, until the id is learned.
     */
    char console_id[HALYARD_PAIRING_CONSOLE_ID_MAX];
    int is_ps5;
    uint8_t registkey[HALYARD_PAIRING_REGISTKEY_MAX];
    size_t registkey_length;
    uint8_t companion[HALYARD_PAIRING_COMPANION_LENGTH];
    uint8_t device_id[HALYARD_PAIRING_DEVICE_ID_MAX];
    size_t device_id_length;
    int os_major;
    int os_minor;
    int start_bitrate;
    int stream_bitrate_kbps;
    int probe_resolutions;   /* 1 = sweep resolutions and exit, instead of streaming */
    int fps;                 /* 30 or 60 */
    /*
     * How long to hold the stream open, in SECONDS. 0 keeps each port's own default.
     *
     * A setting rather than a constant because frame rate and loss settle in seconds but temperature
     * takes minutes, and a thermal comparison should not need a rebuild.
     */
    int hold_seconds;

    /*
     * WHAT TO DO WITH THE CONSOLE WHEN A SESSION ENDS: 0 ask, 1 rest it, 2 leave it awake.
     *
     * Three states rather than a bool because "ask" is a real answer, and the right default. Only
     * consulted when the person ended the session themselves; nothing here rests a console because an
     * application closed.
     */
    int rest_on_disconnect;

    /*
     * Scale on the GPU rather than on the CPU cores. 0 keeps the port's own scaler, 1 asks the platform
     * to use whatever fixed-function scaler it has.
     *
     * A setting rather than a replacement: a port that implements this keeps its CPU scaler and falls
     * back to it. `bilinear` still chooses the filter - on a hardware scaler the interpolation is wired
     * rather than executed, so mode 1 and mode 2 cost the same as mode 0.
     */
    int hardware_scale;

    /*
     * Show the diagnostics overlay on the stream. A setting rather than a build flag: the numbers are
     * most wanted at the moment something looks wrong, which is not a moment anyone can rebuild for.
     */
    int diagnostics;

    /*
     * Try the platform's own font for the overlay instead of the port's drawn one. OFF BY DEFAULT and
     * that is a finding rather than a preference - see the port's notes on what was tried. A setting,
     * so trying it costs one line in the file rather than a rebuild.
     */
    int system_font;

    /*
     * The PSN account id, remembered so it is typed once.
     *
     * Needed by REGISTRATION and by nothing else, so a paired client never reads it; kept so that a
     * re-pairing does not mean entering a nineteen-digit number again (the console cannot be asked for
     * it). Not a secret in the way the registkey is: it identifies an account without granting anything,
     * and the console is told it in a request this client already sends.
     */
    char account_id[64];
    int video_rgb565;        /* 1 = ask MVD for RGB565 instead of BGR565 */
    int skip_until_keyframe; /* 1 = drop every frame after a loss until the next IDR */
    int widescreen;          /* 1 = 800x240 top screen (default); 0 = 400x240 */
    int smoothing;           /* 1 = vertical pair-average in the scale (default on) */
    int scale_thread;        /* 1 = run the frame scale on a spare core - RACY, see above */
    int dump_video;          /* 1 = write the Annex-B stream to video.264 for host-side decoding */
    /*
     * Send CONNECTION_QUALITY (type 16) reports. Off by default, and deliberately: the targetBitrate
     * field's units are unconfirmed - see takion_control_build_connection_quality - and being wrong by
     * 1000x would have the console pick an absurd rate. The .NET side gates the same feature behind the
     * same kind of opt-in for the same reason. Key `connquality` in the pairing file.
     */
    int connection_quality;

    /*
     * Ask the decoder for packed RGB rather than YUV planes where it can produce it. A platform
     * question rather than a protocol one - it exists here because this is where a port's stream
     * settings live. Key `decoderrgb`.
     */
    int decoder_rgb;

    /*
     * Smooth the upscale to the display instead of repeating pixels: 0 nearest, 1 along the row only,
     * 2 in both directions. A viewer's preference rather than a correctness matter - interpolating
     * softens as well as smooths - and 2 costs more than a 60 fps frame allows on the PS3, so 1 is the
     * one that buys something at full rate. Key `bilinear`, 0 by default.
     */
    int bilinear_upscale;

    int stream_width;        /* resolution asked of the console - must be on the standard ladder */
    int stream_height;
    int streaming_type;

    /*
     * The sign-in passcode; optional, because most consoles never ask for one.
     *
     * A console whose user profile is locked answers the control session with LOGIN_PROMPT instead of
     * SESSION_ID, and the reference client (src/Ripcord.Protocol.Halyard/Session/HalyardStreamingSession.cs)
     * answers by submitting a passcode the user types. A headless port has no one to ask, so it reads one
     * here, in the same gitignored record as the registration key. Empty means "none supplied", a normal
     * state and not an error: the probe then reports the gate rather than answering it.
     *
     * Bounded by the field encoding (halyard_sess_fields.h, beside the builder that enforces it), not by
     * a guess about passcode length.
     */
    char login_pin[HALYARD_SESS_LOGIN_PIN_MAX];
} halyard_pairing_record;

/*
 * MORE THAN ONE CONSOLE, AND WHAT IS SHARED BETWEEN THEM.
 *
 * The file holds a list of consoles, as `Ripcord.Core`'s IPairedConsoleStore does (Upsert and Remove).
 *
 * THE SPLIT IS THE SAME ONE .NET MAKES. Per console: its address, its name, its family, and the keys
 * that identify this client to it. Shared: everything else - the account id, the picture size, the frame
 * rate, every toggle.
 *
 * THE SETTINGS ARE STORED ONCE AND HELD MANY TIMES. Every entry below is a complete
 * halyard_pairing_record, and the shared fields are IDENTICAL IN ALL OF THEM - the loader copies them
 * into each entry and the writer takes them from the selected one. The duplication is in memory only and
 * is regenerated on every load; in exchange every single-record caller keeps taking the one struct.
 */
#define HALYARD_PAIRING_MAX_CONSOLES 8

typedef struct {
    halyard_pairing_record console[HALYARD_PAIRING_MAX_CONSOLES];
    int count;

    /*
     * Which one a session would use. -1 when there is none, which is the ordinary state before anything
     * has been paired and is not an error.
     */
    int selected;
} halyard_pairing_set;

/*
 * Loads every console from "pairing.txt" beside `argv0`. Returns the number of usable entries, which is
 * 0 for a missing or unusable file - `out` is filled with defaults either way, so a caller with nothing
 * paired still has somewhere sensible to start from.
 *
 * A FILE WITH NO [console] LINE LOADS. Console keys appearing before any [console] line are read as
 * the first console, which is the single-console layout; a test covers it.
 */
int halyard_pairing_file_load_set(const char *argv0, halyard_pairing_set *out);

/* Writes the whole set back. Returns 1 on success. Same rules as the single-record save below. */
int halyard_pairing_file_save_set(const char *argv0, const halyard_pairing_set *set);

/* The index of the console at `host`, or -1. Matching is by address, which is what the file records. */
int halyard_pairing_set_find(const halyard_pairing_set *set, const char *host);

/* The same, by the console's own id - which survives the address changing. -1 if unknown or absent. */
int halyard_pairing_set_find_id(const halyard_pairing_set *set, const char *console_id);

/*
 * Points a stored console at a new address, found by its own id. Returns 1 if a record was changed and
 * written. For a console whose lease moved, so that it need not be paired again.
 */
int halyard_pairing_file_readdress(const char *argv0, const char *console_id, const char *new_host);

/*
 * Adds `rec` or replaces the entry with the same host, and selects it. Returns its index, or -1 when
 * the set is full - which is refused rather than silently dropping somebody's oldest console.
 *
 * The record's SHARED fields become the set's, because a caller that loaded, changed a setting and
 * upserted means the change.
 */
int halyard_pairing_set_upsert(halyard_pairing_set *set, const halyard_pairing_record *rec);

/* Forgets one console. Returns 1 if it was there. The selection moves to a neighbour, or to -1. */
int halyard_pairing_set_remove(halyard_pairing_set *set, int index);

/*
 * Copies the shared settings out of `from` into every console in the set - what a settings screen does
 * when it is finished. Exposed because a front end with nothing paired yet still has to hold settings
 * somewhere, and that somewhere is a record of its own until there is a set to put them in.
 */
void halyard_pairing_set_apply_settings(halyard_pairing_set *set, const halyard_pairing_record *from);

/* Selects by index. Out of range selects nothing, which is a state rather than a failure. */
void halyard_pairing_set_select(halyard_pairing_set *set, int index);

/* The selected record, or NULL. */
const halyard_pairing_record *halyard_pairing_set_selected(const halyard_pairing_set *set);

/*
 * Loads the SELECTED console's record from "pairing.txt" beside `argv0`. Returns 1 if host, registkey
 * and companion were all present and well-formed; 0 otherwise, having logged why. Optional fields are
 * defaulted whether or not the load succeeds.
 *
 * It is halyard_pairing_file_load_set followed by taking the selected entry.
 */
int halyard_pairing_file_load(const char *argv0, halyard_pairing_record *out_record);

/*
 * Writes the record back to "pairing.txt" beside `argv0`. Returns 1 on success.
 *
 * Registration uses it to persist the pairing it produced.
 *
 * IT WRITES SECRETS AND IS THE ONLY THING HERE THAT MAY. registkey and companion are tied to one
 * console and one account - the wrong side of the generic-versus-personal line, and never logged,
 * never printed, never sent anywhere. They go in this file because that is what the file is for, and
 * nothing else in this core writes them anywhere at all.
 *
 * EXISTING SETTINGS SURVIVE because the record carries them: a caller loads, registers, and saves the
 * same struct. Saving a record that was never loaded writes the defaults, which is correct for a first
 * pairing and wrong for anything else - so load first.
 *
 * EXISTING CONSOLES SURVIVE TOO. This reads the file, adds or replaces the entry with the same host,
 * selects it, and writes the whole set back, so a caller pairing a second console keeps the first
 * without knowing a set exists.
 */
int halyard_pairing_file_save(const char *argv0, const halyard_pairing_record *record);

#endif /* HALYARD_PAIRING_FILE_H */
