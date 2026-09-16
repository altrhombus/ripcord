/*
 * ripcord-3ds - the provisional pairing-record file.
 *
 * Reads "pairing.txt" from next to the running .3dsx. This is NOT the "import a pairing record from a
 * desktop Ripcord install" backlog item - that is a separate, still-unstarted piece of work with a
 * similar name. This is a plain key=value text file for exercising the on-device probes, and the values
 * are transcribed by hand from `ProtocolLab -- register`'s output:
 *
 *   host=192.168.1.42          (required - the console's LAN address)
 *   platform=ps5               (optional, "ps5" or "ps4"; default ps5)
 *   registkey=1a2b3c4d5e6f0011 (required - hex, at most 8 bytes)
 *   companion=...32 hex chars  (required - hex, exactly 16 bytes)
 *   deviceid=...hex            (optional - up to 16 bytes; defaults to all-zero)
 *   osmajor=10, osminor=0, bitrate=10000, streamingtype=0   (all optional, shown defaults)
 *   streambitrate=8000         (optional - the launch spec's bwKbpsSent, in kbps)
 *   skipuntilkeyframe=0        (optional - 1 restores the old drop-everything-after-loss behaviour)
 *   fps=30                     (optional - 30 or 60)
 *   holdseconds=30             (optional - how long to hold the stream; 0 keeps the port default.
 *                               Minutes are what a thermal comparison needs; seconds do for frame rate)
 *   hardwarescale=0            (optional - 1 scales on the GPU instead of the CPU, where the port has
 *                               one; `bilinear` then costs nothing, since the filter is wired)
 *   diagnostics=0              (optional - 1 draws the diagnostics overlay over the stream)
 *   systemfont=0               (optional - 1 tries the platform's own font for the overlay. Known to
 *                               fail on the PS3; see that port's DECODE.md before spending time on it)
 *   videoformat=bgr565         (optional - "bgr565" or "rgb565"; see below)
 *   widescreen=1               (optional - 800x240 top screen; on by default, see below)
 *   smoothing=1                (optional - average the two source rows the vertical squeeze straddles)
 *   scalethread=0              (optional - DO NOT ENABLE; hard-locks the console, see below)
 *   dumpvideo=1                (optional - write the H.264 elementary stream to video.264, 2 MB cap,
 *                               then decode it on a PC with ffmpeg; diagnostic only, costs SD writes
 *                               on the receive thread)
 *   streamwidth=960            (optional - what to ASK the console to encode; ladder values only:
 *   streamheight=540            640x360, 960x540, 1280x720, 1920x1080)
 *   proberesolutions=1         (optional - ask the console for a series of resolutions and report
 *                               what it accepts, instead of streaming; see source/connect/main.c)
 *
 * `bitrate` and `streambitrate` are NOT the same field and default differently on purpose. `bitrate` is
 * the /sess/ctrl RP-StartBitrate header; `streambitrate` is what the launch spec asks the console to
 * actually send.
 *
 * `streambitrate` HAS NEVER BEEN TESTED AGAINST A CLEAN LINK, which is why the default is now high.
 *
 * The earlier measurements looked decisive and were not:
 *
 *     2000 kbps ->  0.57% unit loss,   5 loss events, 27.5 fps
 *     6000 kbps ->  7.24% unit loss, 113 loss events,  0.8 fps
 *
 * That was read as "6000 saturates the radio". But the loss was this port's own: the stream socket had
 * no SO_RCVBUF, so keyframe bursts overran the default buffer and were dropped by the stack before any
 * draining could reach them. With the cushion in place the same link now loses ONE unit in 5,489.
 *
 * So every bitrate conclusion drawn before that fix is void, and the open question is whether this field
 * moves the encoder at all: measured throughput has sat near 0.9 Mbps whether 2000, 3500 or 6000 was
 * asked for. The connect probe now prints what actually arrived, in Mbps and bits/pixel. Run it once
 * high and once low and compare THOSE numbers - if they match, the request is not what governs the
 * encoder, and the senkusha measurement legs (spec 6.4, not implemented here) become the suspect.
 *
 * `widescreen` and `smoothing` are the two picture-quality levers that cost nothing on the wire. The
 * console only ever sends 640x360, so a 400-wide screen point-samples away 61% of the columns - which is
 * what makes on-screen text unreadable while flat colour looks fine. 800x240 mode turns that into an
 * upscale. `smoothing` then addresses the vertical axis, at real CPU cost; watch the profile and the
 * loss figure together, because this port shares one core with the receive loop.
 *
 * `scalethread` IS OFF BECAUSE IT RACES THE DECODER, and that cost several hardware runs to see.
 *
 * Moving the frame scale to core 2 was meant to unload the receive thread, and it did - core 0 went from
 * 77% to 27%. But the scale reads MVD's output buffer while core 0 keeps feeding MVD, which decodes the
 * NEXT picture into that same buffer. Run inline the two are strictly ordered; run on another core they
 * overlap, and a buffer read mid-decode is the decoder's initialised state - a uniform mid-grey field
 * with only the macroblocks written so far carrying real content.
 *
 * That is exactly the reported symptom, and it was misread for several rounds as a 960x540 problem
 * because 640x360 was never run with the thread enabled until long after. The proper fix is a second
 * output buffer so decode and scale never touch the same one (MVD's config takes outdata0 and outdata1,
 * which is likely what they are for) - until then, inline.
 *
 * `streamwidth`/`streamheight` are back at 960x540, and the history is worth knowing before changing it.
 *
 * The console's UI is authored at 1080p, so a 360p encode destroys small text before it reaches the
 * wire. 540p was tried for that reason, produced a flat grey field, and was reverted as broken. It was
 * not: every cause has since been found and fixed for other reasons - the sentinel writing into the
 * reference picture, rendering per-NAL when the console sends one slice per MTU, MVDSTD_SetConfig before
 * the parameter sets rather than after, the BUSY retry not re-applying config, and a cold decoder
 * needing the first access unit fed twice. 540p simply exercised the multi-slice path harder than 360p
 * did, so it failed first and looked like a resolution problem.
 *
 * It costs roughly 2.25x the decode and scale. At 640x360 and 60 fps the receive core runs at 84%, so
 * 540p is a 30 fps proposition unless the scale gets cheaper. Drop fps to 30 if loss appears.
 *
 * Also retired here: the claim that this was pointless because bits/pixel would halve. That rested on an
 * invented threshold ("legible text wants 0.2-0.5 bits/pixel") which a PC decode of a real capture
 * disproved - the PS5 home screen is fully legible at 0.10. What limits text is the 240-row screen, and
 * more source rows is the only lever that addresses it.
 *
 * `videoformat` exists because MVD can emit either byte order and the top screen is configured RGB565.
 * The devkitPro example pairs MVD_OUTPUT_BGR565 with an RGB565 screen, which is what this port copied -
 * if reds and blues look swapped, this is the setting, not the blit.
 *
 * Lives here rather than inside a program's main.c because two on-device probes now read the same file
 * (source/session/main.c and source/connect/main.c) and a second hand-rolled copy of this parser would
 * be the obvious way to end up with two subtly different ideas of what a valid record is.
 *
 * ON THE LENGTH RULES, which have already cost one debugging session: rc_hex_decode returns "bad" for
 * malformed AND for over-long input, and this loader reports its three required fields in one message.
 * So an over-long registkey reports as a *missing* field. The message below names lengths explicitly for
 * that reason - see HARDWARE-PROBES.md, where it is called out as the likeliest silent failure.
 */
#ifndef HALYARD_PAIRING_FILE_H
#define HALYARD_PAIRING_FILE_H

#include <stddef.h>
#include <stdint.h>

#include "halyard_sess_fields.h"

#define HALYARD_PAIRING_HOST_MAX 64
#define HALYARD_PAIRING_REGISTKEY_MAX 8
#define HALYARD_PAIRING_COMPANION_LENGTH 16
#define HALYARD_PAIRING_DEVICE_ID_MAX 16

typedef struct {
    char host[HALYARD_PAIRING_HOST_MAX];
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
     * A setting rather than a constant because the two things measured over a hold have very different
     * time constants. Frame rate and loss settle in seconds; TEMPERATURE does not - a console's fan
     * responds over minutes, so a 30-second hold reports the start of a curve and calls it a result.
     * Being able to ask for five minutes without a rebuild is the difference between comparing two
     * thermal numbers and guessing at them.
     */
    int hold_seconds;

    /*
     * Scale on the GPU rather than on the CPU cores. 0 keeps the port's own scaler, 1 asks the platform
     * to use whatever fixed-function scaler it has.
     *
     * A setting rather than a straight replacement because it is the video path and a picture is the one
     * thing a stream client cannot be without: the port that implements this keeps its old scaler and
     * falls back to it. `bilinear` still chooses the filter - on a hardware scaler the interpolation is
     * wired rather than executed, so mode 1 and mode 2 cost the same as mode 0 and the reason to prefer
     * nearest goes away.
     */
    int hardware_scale;

    /*
     * Show the diagnostics overlay on the stream. A setting because it is meant to become a toggle the
     * user reaches for mid-session rather than a build flag: the numbers are most wanted at the moment
     * something looks wrong, which is not a moment anyone can rebuild for.
     */
    int diagnostics;

    /*
     * Try the platform's own font for the overlay instead of the port's drawn one. OFF BY DEFAULT and
     * that is a finding rather than a preference - see the port's notes on what was tried. Kept as a
     * setting so a future attempt costs one line in this file rather than a rebuild.
     */
    int system_font;
    int video_rgb565;        /* 1 = ask MVD for RGB565 instead of BGR565 */
    int skip_until_keyframe; /* 1 = drop every frame after a loss until the next IDR */
    int widescreen;          /* 1 = 800x240 top screen (default); 0 = 400x240 */
    int smoothing;           /* 1 = vertical pair-average in the scale (default on) */
    int scale_thread;        /* 1 = run the frame scale on a spare core - RACY, see below */
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
     * THE SIGN-IN PASSCODE, and it is optional because most consoles never ask for one.
     *
     * A console whose user profile is locked answers the control session with LOGIN_PROMPT instead of
     * SESSION_ID, and the reference client (src/Ripcord.Protocol.Halyard/Session/HalyardStreamingSession.cs)
     * answers that by submitting a passcode the user types. A headless port has no one to ask, so it reads
     * one here - the same out-of-band route the registration key already travels, in the same gitignored
     * record. Empty means "none supplied", which is a normal state and not an error: the probe then reports
     * the gate rather than answering it.
     *
     * Bounded by the field encoding rather than by a guess about passcode length, so the one bound lives in
     * halyard_sess_fields.h beside the builder that enforces it.
     */
    char login_pin[HALYARD_SESS_LOGIN_PIN_MAX];
} halyard_pairing_record;

/*
 * Loads the record from "pairing.txt" beside `argv0`. Returns 1 if host, registkey and companion were
 * all present and well-formed; 0 otherwise, having logged why. Optional fields are defaulted whether or
 * not the load succeeds.
 */
int halyard_pairing_file_load(const char *argv0, halyard_pairing_record *out_record);

#endif /* HALYARD_PAIRING_FILE_H */
