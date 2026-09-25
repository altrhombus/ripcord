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

/* Declared, not included, as rc_stun.h does: only the STUN server list takes one, by pointer. */
struct sockaddr_in;

/* How the session reaches the console. The value is RP-ConPath on the wire. LOCAL is TCP 9295 and the
 * console's own UDP ports; RENDEZVOUS is internet play, driven with the cloud tier as described under
 * "THE RENDEZVOUS ROUTE" below. */
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
    HALYARD_CLIENT_END_REFUSED,           /* the console refused the session (SESSION_REPLY, or /sess) */
    HALYARD_CLIENT_END_NO_MEDIA           /* RENDEZVOUS: poll_media gave up, or no media peer in time */
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
     * LAN console streams without it is unconfirmed [X]. Default 1. Like the budgets, 0 takes the default
     * - which here is 1, so a zero-initialised config waits - and a NEGATIVE value is the explicit "do not
     * wait"; see halyard_client_config_resolve. */
    int require_session_ready;

    /*
     * ---- RENDEZVOUS only; ignored on LOCAL. Everything the cloud tier knows before a connect starts. ----
     *
     * stun_servers        IPv4 addresses in network order, as rc_stun_gather takes them - the core
     *                     resolves no names (StunClient.DefaultServers are DNS names; resolving them is the
     *                     host's). Asked in order until two answer, per leg, so a NAT that maps per
     *                     destination is detected; on different operators, for the reason rc_stun.h gives.
     *                     Copied at init, at most HALYARD_CLIENT_STUN_MAX. NULL or 0: no STUN, and each
     *                     leg offers only its LOCAL candidate, as .NET does when discovery fails.
     * stun_attempts       per server, 0: 3            (StunClient's defaults)
     * stun_timeout_ms     per attempt, 0: 500
     * bind_address        4 bytes, network order, for both legs' sockets. NULL: INADDR_ANY. Copied.
     * control_local_port  the control leg's local port, 0: any. The OFFER advertises whatever was bound.
     * media_local_port    the A/V leg's, 0: any.
     * media_offer_timeout_ms   how long poll_media may answer "not yet" before the session ends with
     *                     END_NO_MEDIA. 0: 30000, .NET's OfferTimeout for the console's next OFFER.
     * dgram_stage_timeout_ms   each 9303 stage (prelude, connection, response), 0: 30000   (.NET's
     * dgram_receive_timeout_ms the quiet window before a re-send, 0: 5000    HalyardDatagramControlOptions)
     *
     * The signaling ids, the console's candidates and its media offer are NOT here: they are learned
     * during the rendezvous, and arrive through halyard_client_rendezvous_begin and poll_media.
     *
     * signin_prompt_window_ms defaults to 1000 on this route, not 20000: .NET's LoginPromptWindow. On the
     * LAN the wait ends early at SESSION_ID; on this route SESSION_ID comes only after the A/V prelude, so
     * the window is always spent in full, and 20 s of it would be added to every unlocked connect.
     * require_session_ready is not consulted here: SESSION_ID is waited for after the A/V prelude, for up
     * to 8 s and non-fatally, as .NET does - except after a passcode, when it must follow the sign-in.
     */
    const struct sockaddr_in *stun_servers;
    size_t stun_server_count;
    unsigned stun_attempts;
    unsigned stun_timeout_ms;
    const uint8_t *bind_address;
    uint16_t control_local_port;
    uint16_t media_local_port;
    unsigned media_offer_timeout_ms;
    unsigned dgram_stage_timeout_ms;
    unsigned dgram_receive_timeout_ms;
} halyard_client_config;

/* The most STUN servers a config may name; more are ignored. */
#define HALYARD_CLIENT_STUN_MAX 4

/* A localHashedId: the 20 bytes each side publishes in its signaling OFFER (HALYARD_DGRAM_HASHED_ID_LENGTH). */
#define HALYARD_CLIENT_HASHED_ID_LENGTH 20

/*
 * One of our two rendezvous legs, as the cloud tier needs it for an OFFER: the port the leg's socket is
 * bound to, and what STUN said the NAT maps it to. Feed these to halyard_wan_our_candidates along with this
 * host's own address toward the console (the LOCAL candidate) - the ports must be these, because a NAT
 * maps per source port.
 */
typedef struct {
    uint16_t local_port;                  /* host order */
    int has_reflexive;                    /* STUN answered with an IPv4 mapping */
    uint8_t reflexive_address[4];         /* network order */
    uint16_t reflexive_port;              /* host order */
    int endpoint_independent;             /* 1 consistent; 0 per-destination (symmetric: a console on
                                           * another network will NOT reach this); -1 only one answer */
} halyard_client_leg;

/*
 * The console, for one leg, as its OFFER described it: the candidate chosen with
 * halyard_wan_choose_candidate (and its fallback to the paired host on 9303 when nothing parses), and the
 * localHashedId that OFFER carried.
 */
typedef struct {
    uint8_t address[4];                   /* network order */
    uint16_t port;                        /* host order */
    uint8_t console_hashed_id[HALYARD_CLIENT_HASHED_ID_LENGTH];
} halyard_client_peer;

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

    /* Additions. The window this report covers, measured rather than assumed (a late pump makes it
     * longer than 200 ms), and how many incoming control packets failed authentication and were dropped. */
    uint32_t window_ms;
    uint64_t verify_dropped;
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

    /* Additions - all protocol facts, none of them text a host would show as is. */
    int login_prompted;                   /* the console asked for a passcode */
    int login_verdict_byte;               /* the raw verdict byte, -1 if none arrived */
    unsigned stream_version;              /* the protocol version the stream channel agreed */
    int version_rtt_ms;                   /* senkusha's PROTOCOL_VERSION round trip, declared as rtt */
    int stream_info_parsed;
    uint32_t stream_width, stream_height; /* what STREAM_INFO said, 0 until then */
    int stream_is_hevc;
    uint64_t heartbeats_sent, congestion_sent, input_history_sent, input_state_sent;
    uint64_t stream_info_repeats;         /* STREAM_INFO re-sent by the console and re-acked */
    char console_disconnect_reason[64];   /* DISCONNECT's reason string, when the console hung up */

    /* RENDEZVOUS only. */
    uint16_t control_local_port, media_local_port;
    int media_prelude_ok;                 /* the A/V leg's 88-byte prelude completed */
    int session_ready_waited_ms;          /* how long after the A/V prelude SESSION_ID took, -1 never */
    int probe_report_sent;
    int stream_ready_seen;                /* STREAM_READY arrived within its window */
    int dgram_status;                     /* the last failing halyard_dgram_channel_status, 0 none */
    uint64_t stray_dropped;               /* datagrams on the media socket from anyone but the console */
} halyard_client_result;

/* Levels for the log callback. Lines the core itself logs (through rc_log) arrive at INFO. */
#define HALYARD_CLIENT_LOG_DEBUG 0
#define HALYARD_CLIENT_LOG_INFO  1
#define HALYARD_CLIENT_LOG_WARN  2
#define HALYARD_CLIENT_LOG_ERROR 3

/*
 * The callbacks. All are called on the host's thread, from inside connect() or pump(). A buffer passed to
 * a callback is borrowed for the duration of the call (the demuxer's contract, stream_demux.h), so a host
 * that keeps it must copy it. Any callback may be NULL except video_frame.
 */
typedef struct {
    void *user;

    /* HALYARD_CLIENT_LOG_*. The core's own rc_log lines are routed here too, through rc_log_set_sink,
     * which is process-wide: one client at a time owns it (the one initialised last). */
    void (*log)(void *user, int level, const char *line);
    /* Before each stage begins, so a host can show progress without polling: the stage named is the one
     * being worked towards. ENDED is delivered once, when the session is over. result.stage keeps the
     * furthest stage REACHED, so it still locates a failure after the ending. */
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

    /*
     * RENDEZVOUS only, and required there. The A/V leg's socket is bound and STUN has been asked about it:
     * `media` is what our media OFFER must advertise. The host negotiates the media connection with the
     * cloud and answers with the console's end of it. Return 1 with `out` filled; 0 if not ready yet (the
     * core keeps the control session alive and asks again, bounded by media_offer_timeout_ms); -1 to give
     * up (END_NO_MEDIA). See "THE RENDEZVOUS ROUTE", step 6, for what the host does in between.
     */
    int (*poll_media)(void *user, const halyard_client_leg *media, halyard_client_peer *out);
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

/*
 * ==== THE RENDEZVOUS ROUTE ===============================================================================
 *
 * Internet play, and any account-route connect. Ported from HalyardAccountConsoleSession.ConnectAsync,
 * HalyardAccountPairing.ConnectAsync/RunAsync and HalyardStreamingSession.ConnectAsync/StartStreamingAsync.
 *
 * THE SPLIT. The cloud tier (sign-in, the session, the push WebSocket, OFFER/ACCEPT/RESULT signaling) is
 * the host's, in Swift on the Mac; everything on UDP is here. The two interleave at exact points - the
 * console discards an Init it cannot tie to an OFFER, and races one sent too late - so the core exposes
 * those points as calls and one pull callback, and the host drives the signaling around them:
 *
 *   1. halyard_client_init            route = RENDEZVOUS, poll_media set, the STUN servers resolved.
 *   2. halyard_client_rendezvous_prepare
 *                                     binds the CONTROL leg's socket and asks STUN about it. Blocking, at
 *                                     most servers x attempts x timeout. Any time before our OFFER; .NET does
 *                                     it before creating the cloud session.
 *   3. (host) the cloud session up to the console's OFFER - create it, send `commands` (data1/data2, from
 *      halyard_account_regist_generate_key_material), wait for the console to join, recover the seed from
 *      customData1 if one is published, and receive the console's OFFER.
 *   4. (host) OUR OFFER, carrying the candidates halyard_wan_our_candidates builds from the leg step 2
 *      returned, and our localHashedId. Not before the console's OFFER: a sessionMessage 404s until the
 *      console is a member.
 *   5. halyard_client_rendezvous_begin
 *                                     AFTER OUR OFFER, BEFORE OUR ACCEPT. Aims the control leg at the
 *                                     console's chosen candidate and puts our Init on the wire, without
 *                                     waiting. Both neighbouring orderings are falsified on hardware
 *                                     (HalyardAccountPairing.RunAsync says how).
 *      (host) OUR ACCEPT, naming that same candidate (sid 1, reqId 2, peerSid = the console's sid), and
 *      from here RESULT-acknowledge every console message for the life of the session - a console that is
 *      not acked TERMINATEs. Keep the cloud session joined until the stream ends: leaving it ends the
 *      session the console joined.
 *   5a. optional - .NET does it on every connect that has a seed: registration on the same association,
 *      halyard_account_regist_run(&params, halyard_client_rendezvous_exchange, client, &result). The
 *      captured client runs rgst, init and ctrl as three connections over one association. **[X]** whether
 *      a connect needs it at all; .NET does not store the record it returns.
 *   6. halyard_client_connect         blocking, as on the LAN. /sess/init and /sess/ctrl over the control
 *                                     leg (no ARM probe, RP-ConPath 3), the sign-in gate, then the A/V leg:
 *                                     its socket is bound, STUN asked, and poll_media is called ON THIS
 *                                     THREAD, repeatedly, until it answers. While it says "not yet" the
 *                                     host, on its own task:
 *        - waits for the console's NEXT OFFER (the A/V leg arriving - a second OFFER is not a duplicate);
 *        - sends our media OFFER with the candidates built from `media` (sid 2, reqId 3);
 *        - sends our media ACCEPT naming the console's chosen media candidate (sid 2, reqId 4, peerSid =
 *          that OFFER's sid), chosen by the same rule as step 5;
 *        - then answers poll_media with that candidate and that OFFER's localHashedId.
 *      The core then preludes the A/V leg, waits up to 8 s for SESSION_ID (non-fatal), runs senkusha as a
 *      first Takion association on that socket, sends PROBE_REPORT, waits up to 10 s for STREAM_READY
 *      (non-fatal), opens the stream's Takion association on the same socket, and continues exactly as on
 *      the LAN. Takion on that socket reads only datagrams from the console's media endpoint.
 *   7. halyard_client_pump            as on the LAN. halyard_client_fds includes the control leg's socket.
 *   8. halyard_client_destroy         ends the control connection politely (on datagrams nothing says it
 *                                     implicitly), then closes both legs. THEN the host leaves the cloud
 *                                     session.
 *
 * Every call here is on the thread connect() and pump() run on. A console that is never reached ends in
 * connect() like any other failure; result.dgram_status says which 9303 stage gave up.
 *
 * Returns 1 on success and 0 on failure (the log says why) for prepare and begin; both refuse a client
 * that is not RENDEZVOUS, or one whose connect has started.
 */
int halyard_client_rendezvous_prepare(halyard_client *client, halyard_client_leg *out_control_leg);

int halyard_client_rendezvous_begin(halyard_client *client,
                                    const uint8_t local_hashed_id[HALYARD_CLIENT_HASHED_ID_LENGTH],
                                    const halyard_client_peer *console);

/*
 * A halyard_account_regist_exchange_fn over the control leg (`user` is the halyard_client): step 5a.
 * Only between begin and connect. Blocking, bounded by the 9303 stage deadline, cancellable through
 * poll_commands.
 */
int halyard_client_rendezvous_exchange(void *user, const uint8_t *request, size_t request_length,
                                       uint8_t *response, size_t response_size, size_t *out_response_length);

#endif /* HALYARD_CLIENT_H */
