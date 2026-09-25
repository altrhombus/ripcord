/*
 * libripcord - the connect sequence: from a pairing record and an address to a stream, and back.
 *
 * WHY THIS EXISTS. Every stage of a session already lived in the core (discovery, wake, the /sess control
 * session, Takion, key agreement, senkusha, the demuxer, input), but the sequence that strings them
 * together did not. Each port wrote its own, interleaved with its own UI: the PS3's rc_connect.c (4,330
 * lines) and the 3DS's connect/main.c. This is that sequence, once, ported from both authorities:
 *
 *   ports/ripcord-ps3/source/connect/rc_connect.c   the most complete C one, hardware-verified, and
 *                                                   where most of the timing and retry lessons were
 *                                                   learned (the b-numbers in comments are its builds)
 *   src/Ripcord.Protocol.Halyard/Session/HalyardStreamingSession.cs, and
 *   src/Ripcord.Protocol.Halyard.Takion/HalyardTakionStream.cs      the .NET reference
 *
 * Where the two disagree, each default below follows whichever side has the better hardware evidence,
 * and says which. The first consumer is the macOS client (src/Ripcord.Mac); the ports move onto it later,
 * each on its own hardware.
 *
 * THREADING: ONE HOST-OWNED THREAD, AND NOTHING ELSE. The core creates no threads, takes no locks and
 * allocates nothing. The host calls halyard_client_connect() on a thread of its choosing, and then
 * halyard_client_pump() on the same thread until it returns 0. Everything the host wants to tell the
 * session (input, a passcode, a command, cancellation) is PULLED through a callback on that thread, so
 * each host does its own synchronisation in its own terms, and C99, which has no atomics, never has to.
 * The PS3's three shutdown lockups (b129-b142) were about which work shares the receive thread; the
 * answer here is that a callback copies what it is given and returns, and decoding happens on the
 * host's own thread.
 *
 * THE HOT PATH DOES NOT ALLOCATE. The instance is placed in storage the caller provides, sized by
 * halyard_client_struct_size(). It is large (the demuxer's frame slots are most of it), so a host
 * allocates it once per session, on the heap.
 */
#ifndef HALYARD_CLIENT_H
#define HALYARD_CLIENT_H

#include "../session/halyard_pairing_file.h"
#include "../input/halyard_input.h"

#include <stddef.h>
#include <stdint.h>

/* How the session reaches the console. The value is RP-ConPath on the wire. Only LOCAL is implemented;
 * RENDEZVOUS arrives with internet play and needs the transport seams below. */
typedef enum {
    HALYARD_ROUTE_LOCAL = 1,
    HALYARD_ROUTE_RENDEZVOUS = 3
} halyard_route;

/*
 * The furthest point a session reached. The PS3's rc_connect_stage ladder, kept because it is how a
 * failure is located: a session that ends at SENKUSHA_UP failed bringing up the stream channel, not
 * anywhere earlier. STREAMING (the first keyframe delivered) and ENDED are additions.
 */
typedef enum {
    HALYARD_CLIENT_STAGE_IDLE = 0,
    HALYARD_CLIENT_STAGE_CONTROL_OPEN,    /* /sess/init and /sess/ctrl answered                    */
    HALYARD_CLIENT_STAGE_SIGNED_IN,       /* the console's user is signed in (or never asked)       */
    HALYARD_CLIENT_STAGE_SESSION_READY,   /* SESSION_ID seen: the console is willing to stream      */
    HALYARD_CLIENT_STAGE_SENKUSHA_UP,     /* the senkusha channel completed its handshake           */
    HALYARD_CLIENT_STAGE_TAKION_UP,       /* the stream's own Takion channel is established         */
    HALYARD_CLIENT_STAGE_STREAM_KEYS,     /* SESSION_REPLY verified, stream keys derived            */
    HALYARD_CLIENT_STAGE_STREAM_READY,    /* sealing on, STREAM_INFO received and acknowledged      */
    HALYARD_CLIENT_STAGE_STREAMING,       /* the first keyframe went to the host                    */
    HALYARD_CLIENT_STAGE_ENDED
} halyard_client_stage;

/* Why a session ended, separately from where it got to: several endings share a stage, which is the
 * distinction rc_connect.h's own comments (the menu_disconnect / xmb_quit / login_blocked fields) kept
 * having to add after the fact. */
typedef enum {
    HALYARD_CLIENT_END_NONE = 0,          /* still running */
    HALYARD_CLIENT_END_USER_DISCONNECT,   /* halyard_client_disconnect() */
    HALYARD_CLIENT_END_HOST_CANCEL,       /* the cancel command, during connect */
    HALYARD_CLIENT_END_CONSOLE_CLOSED,    /* the console sent DISCONNECT, or closed the control session */
    HALYARD_CLIENT_END_CHANNEL_ERROR,     /* a socket failed */
    HALYARD_CLIENT_END_SIGNIN_CANCELLED,  /* the passcode callback returned -1 */
    HALYARD_CLIENT_END_SIGNIN_REJECTED,   /* every passcode attempt was refused */
    HALYARD_CLIENT_END_SIGNIN_NO_SESSION, /* signed in, but SESSION_ID never came */
    HALYARD_CLIENT_END_TIMEOUT,           /* a stage ran out of time; the stage says which */
    HALYARD_CLIENT_END_REFUSED            /* the console refused the session (SESSION_REPLY, or /sess) */
} halyard_client_end_reason;

/* Commands the host can ask for, pulled once per wait or pump. Flags, so one pull can carry several. */
#define HALYARD_CLIENT_CMD_CANCEL        (1u << 0) /* abandon connect, or end a running session */
#define HALYARD_CLIENT_CMD_KEYFRAME      (1u << 1) /* the host's decoder lost the chain */
#define HALYARD_CLIENT_CMD_DISCONNECT    (1u << 2) /* goodbye, then end: see halyard_client_disconnect */
#define HALYARD_CLIENT_CMD_REST_CONSOLE  (1u << 3) /* with DISCONNECT: ask the console to rest first */

typedef struct {
    const halyard_pairing_record *record; /* keys and host; copied at init */
    halyard_route route;                  /* HALYARD_ROUTE_LOCAL */

    /* What the host can show, not what the core imposes: the PS3's 720p cap was a cellVdec limit and
     * belongs to that port, not to the protocol. 0 means "the console's default". */
    int width, height, fps, bitrate_kbps;
    int allow_hevc;                       /* the host can decode HEVC */
    int hdr;                              /* request an HDR stream; the host must present it */

    /* Budgets, in milliseconds or counts. 0 takes the default, which is the evidenced one:
     *   signin_prompt_window_ms 20000  PS3; .NET waits 1000 on the strength of cap50's 60 ms prompt
     *   senkusha_attempts          20  .NET's 20 x 300 ms inside an 8 s box
     *   stream_attempts           100  .NET: the vendor client persists about 30 s under loss (cap55)
     *   attempt_interval_ms       300  both
     *   rcvbuf_bytes          4194304  .NET's ask; the granted size is reported, since the PS3's lv2
     *                                  capped it at 124,800 bytes (b128) */
    unsigned signin_prompt_window_ms;
    unsigned senkusha_attempts;
    unsigned stream_attempts;
    unsigned attempt_interval_ms;
    int rcvbuf_bytes;

    /* 1 = wait for SESSION_ID before Takion. PS3 and 3DS both require it on hardware; .NET's claim that a
     * LAN console streams without it is unconfirmed [X]. Default 1. */
    int require_session_ready;
} halyard_client_config;

/* What STREAM_INFO said, for the host to configure its decoder before the first frame. */
typedef struct {
    uint32_t width, height;
    int is_hevc;
    const uint8_t *video_header;          /* the parameter sets, borrowed for the call */
    size_t video_header_length;
} halyard_client_stream_info;

/* One statistics window, every 200 ms while streaming: the raw facts a host's own policy decides from
 * (CONNECTION_QUALITY, stall handling). The PS3 ends a session after 4 s without a picture; .NET instead
 * separates a paused, static scene from a dead console by the console's own activity. That choice is the
 * host's, so the core reports both clocks and decides neither. */
typedef struct {
    uint64_t packets_received, packets_lost;
    uint64_t video_frames, audio_frames, keyframes;
    uint32_t kbps;                        /* measured over the window */
    uint32_t ms_since_console_activity;   /* any datagram at all from the console */
    uint32_t ms_since_video_frame;
    uint32_t idr_requests;
} halyard_client_stats;

/* Protocol facts about how a session went, for diagnosis. Text is the host's job, from these values. */
typedef struct {
    halyard_client_stage stage;
    halyard_client_end_reason end_reason;
    int control_error;                    /* whatever halyard_control_session reported */
    int login_attempts;
    int login_verdict;                    /* -1 unknown, 0 refused, 1 accepted */
    int curve;                            /* RC_ECDH_CURVE_* the session negotiated */
    int senkusha_ok;
    int rcvbuf_asked, rcvbuf_granted;
    int reply_reject_reason;              /* SESSION_REPLY's reason, when it refused */
    uint64_t verify_checked, verify_failed;
    int disconnect_sent, rest_requested;
} halyard_client_result;

/*
 * The callbacks. All are called on the host's thread, from inside connect() or pump(). A buffer passed to
 * a callback is borrowed for the duration of the call (the demuxer's contract, stream_demux.h), so a host
 * that keeps it must copy it. Any callback may be NULL except video_frame.
 */
typedef struct {
    void *user;

    void (*log)(void *user, int level, const char *line);
    /* Before each stage begins, so a host can show progress without polling. */
    void (*stage)(void *user, halyard_client_stage stage);
    void (*stream_info)(void *user, const halyard_client_stream_info *info);
    /* One Annex-B access unit; keyframes carry the parameter sets. */
    void (*video_frame)(void *user, const uint8_t *data, size_t length, int is_keyframe);
    /* One Opus packet, 10 ms. */
    void (*audio_frame)(void *user, const uint8_t *data, size_t length);
    /* The current pad state. Return 0 when no controller is attached; the core then sends nothing. */
    int (*poll_input)(void *user, halyard_input_state *out);
    /* The console asked for its user's passcode. `retry` counts refusals so far. Write the digits into
     * `out` and return 1; return 0 if not ready yet (the core keeps servicing the session and asks
     * again, so a person can take their time); return -1 to give up. */
    int (*poll_passcode)(void *user, int retry, char *out, size_t out_size);
    /* HALYARD_CLIENT_CMD_* flags, or 0. */
    unsigned (*poll_commands)(void *user);
    void (*stats)(void *user, const halyard_client_stats *stats);
} halyard_client_callbacks;

typedef struct halyard_client halyard_client;

size_t halyard_client_struct_size(void);

/* Places an instance in `storage` (at least halyard_client_struct_size() bytes, suitably aligned for any
 * type). Copies the config and the record. Returns NULL for a bad argument. Opens nothing. */
halyard_client *halyard_client_init(void *storage, size_t storage_size, const halyard_client_config *config,
                                    const halyard_client_callbacks *callbacks);

/* Runs the sequence to STREAM_READY. Blocking, bounded by the budgets above, and cancellable through
 * poll_commands. Returns the stage reached; STREAM_READY or later means pump() should follow. */
halyard_client_stage halyard_client_connect(halyard_client *client);

/* Services a running session without blocking: control, the A/V drain, input, the periodic sends.
 * Returns 1 while the session is alive, 0 once it has ended. `*next_deadline_ms`, if not NULL, is how
 * long the host may wait (on the sockets from halyard_client_fds, or simply sleep) before calling
 * again. */
int halyard_client_pump(halyard_client *client, uint32_t *next_deadline_ms);

/* The sockets a host may wait on between pumps. Returns how many were written. */
int halyard_client_fds(const halyard_client *client, int *fds, int max_fds);

/* Goodbye: REST_MODE first when `rest_console` (only when a person chose it), then the Takion DISCONNECT,
 * both best-effort in that order (cap52). The session then ends at the next pump. Pump-thread only. */
void halyard_client_disconnect(halyard_client *client, int rest_console);

const halyard_client_result *halyard_client_result_get(const halyard_client *client);

/* Wipes the keys and closes every socket. Safe at any point after init. */
void halyard_client_destroy(halyard_client *client);

#endif /* HALYARD_CLIENT_H */
