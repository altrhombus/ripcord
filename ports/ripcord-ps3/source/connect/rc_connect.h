/*
 * ripcord-ps3 - wake a console and open the control session.
 *
 * The next thing after discovery, and the first that needs a PAIRING RECORD: a registration key and a
 * companion key tied to one console and one account. Registration itself is not here and is not coming -
 * ports/ripcord-3ds/README.md settles that ("Pairing happens on a PC, not here"), and the record is
 * transcribed from `ProtocolLab -- register` into a key=value file. That is why halyard_wake lives in
 * ports/common while registration does not: wake is purely local, and cap49 proved it by working with
 * the console's internet blocked at the router.
 *
 * THE RECORD IS DIRTY-ROOM MATERIAL. registkey and companion are tied to a specific console and account,
 * which is the wrong side of CLAUDE.md's generic-versus-personal test. The file is copied to the console
 * by hand, is not embedded in any binary, and NOTHING HERE LOGS ITS CONTENTS - not the keys, not the
 * derived wake credential, not the host address. The report says whether a field was present and what
 * happened, never what it was.
 *
 * WAKE FIRST, BECAUSE STANDBY IS THE ORDINARY STATE. halyard_wake.h describes the exchange and the two
 * traps in it: nothing ever acknowledges the WAKEUP, so readiness is observed by polling SRCH until
 * is_awake flips; and the datagram may have to originate from a specific SOURCE port, which is a socket
 * concern and therefore this file's job rather than the core's.
 */
#ifndef RC_CONNECT_H
#define RC_CONNECT_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* How far stream_session_exchange got. Each value means the named step returned a usable result. */
#define RC_STREAM_STEP_NOTHING      0
#define RC_STREAM_STEP_RANDOM       1  /* the 16-byte handshake key was drawn      */
#define RC_STREAM_STEP_SPEC         2  /* the launch spec JSON was built           */
#define RC_STREAM_STEP_B64          3  /* it encrypted and base64-encoded          */
#define RC_STREAM_STEP_REQUEST      4  /* SESSION_REQUEST was built (ECDH ran)     */
#define RC_STREAM_STEP_SENT         5  /* ...and went out on the stream channel    */
#define RC_STREAM_STEP_REPLY        6  /* a SESSION_REPLY came back                */

typedef enum {
    RC_CONNECT_NO_RECORD = 0,   /* no pairing record on the console - skip, not a failure */
    RC_CONNECT_BAD_RECORD,      /* the file was there and unusable                        */
    RC_CONNECT_NO_CONSOLE,      /* nothing answered SRCH at the recorded address          */
    RC_CONNECT_WAKE_SENT,       /* asleep, WAKEUP sent, never woke within the deadline    */
    RC_CONNECT_AWAKE,           /* the console is awake - as far as this probe goes       */
    RC_CONNECT_SESSION_OPEN,    /* the control session opened                             */
    RC_CONNECT_SESSION_READY,   /* SESSION_ID seen - the console is willing to stream      */
    RC_CONNECT_SENKUSHA_UP,     /* the senkusha Takion channel completed its handshake     */
    RC_CONNECT_TAKION_UP,       /* the stream's own Takion channel is established          */
    RC_CONNECT_STREAM_KEYS,     /* SESSION_REPLY verified and the stream keys are derived  */
    RC_CONNECT_STREAM_READY     /* sealing on, STREAM_INFO received and acked              */
} rc_connect_stage;

typedef struct {
    rc_connect_stage stage;
    int  had_record;
    const char *record_dir;  /* which of the candidate directories it came from (a literal, not owned) */
    int  host_parsed;        /* the recorded address is a dotted quad we can send to               */
    int  unicast_replied;    /* the recorded address answered SRCH                                 */
    int  broadcast_found;    /* SOME console answered a broadcast, when the recorded one did not    */
    int  broadcast_matches;  /* ...and it is the address in the record. 0 here means a stale record */
    int  credential_ok;      /* the wake credential derived from the registration key      */
    int  wake_source_bound;  /* the WAKEUP went out from the source port the spec names    */
    int  wakeups_sent;
    int  polls;              /* SRCH polls while waiting for is_awake to flip              */
    int  was_asleep;         /* it answered 620 Server Standby before any wake was sent    */
    unsigned woke_after_ms;  /* how long the flip took, for the record                     */
    int  session_error;      /* whatever the control session reported, if it failed        */
    int  tcp_preflight_ok;   /* the control port accepted a bounded TCP connect            */

    /*
     * DOES inet_aton WORK ON THIS PLATFORM? The port has only ever used inet_pton; ports/common uses
     * inet_aton and nothing else - in rc_tcp_connect and in the ARM step. So the core's address parsing
     * has never been exercised here, and rc_tcp_connect's connect() is BLOCKING WITH NO TIMEOUT: an
     * address that parses to the wrong value there is not a failed connect, it is a hang, which is what
     * b31 and b34 did.
     *
     * Both are given the same string and the results compared as 32-bit values. Neither address is
     * logged; only whether they agree.
     */
    int  aton_ok;            /* inet_aton reported success                                  */
    int  pton_ok;            /* inet_pton reported success                                  */
    int  aton_matches_pton;  /* ...and they produced the same address                       */

    /*
     * Whether fcntl can make a PS3 socket non-blocking. Probed with calls that cannot block: set the
     * flag, read it back, and separately try SO_NBIO. If the readback does not carry O_NONBLOCK, every
     * poll loop in ports/common has been running against a blocking socket - which makes a bounded
     * deadline unreachable, because the recv guarding it never returns.
     */
    int  fcntl_set_rc;             /* what F_SETFL returned                                   */
    int  fcntl_readback_nonblock;  /* F_GETFL shows O_NONBLOCK afterwards                     */
    int  so_nbio_ok;               /* setsockopt(SO_NBIO) was accepted                        */

    /*
     * WHAT THE CONSOLE ACTUALLY SAID while we waited for SESSION_ID. b36 reached "control session open"
     * and then sat silent for eight seconds, which reported the absence of one message and nothing about
     * the presence of others. A console that has just woken from standby may be asking for a sign-in PIN
     * - the 3DS flow watches for exactly that before waiting for session-ready - and that is a different
     * problem from a console that is simply slow.
     */
    int  frames_seen;        /* control frames received while waiting                       */
    unsigned first_type;     /* the first frame's type, 0 if none                           */
    unsigned last_type;      /* and the last                                                */
    int  login_prompt;       /* the console asked for a sign-in PIN                          */
    int  login_submitted;    /* ...and a passcode from the pairing record went back to it   */

    /*
     * The console's verdict on that passcode, read from LOGIN (0x0005). -1 means it never arrived, which
     * is a different fact from "rejected" and is kept distinct on purpose: a console that goes quiet and
     * a console that says no want different things looked at next.
     */
    int  login_verdict;      /* -1 none, 0 accepted, 1 rejected, 2 something else entirely   */

    /*
     * The two Takion channels. Senkusha (9297) is brought up first because the console gates the
     * stream channel's SESSION exchange on it having happened; the stream channel is 9296.
     *
     * The tags are recorded because they are the cheapest proof the handshake was real: a four-way
     * exchange that completed has a peer tag the console chose, which no amount of local optimism
     * can produce.
     */
    int  senkusha_up;
    int  takion_up;
    unsigned senkusha_local_tag;
    unsigned senkusha_peer_tag;
    unsigned takion_local_tag;
    unsigned takion_peer_tag;

    /* The senkusha legs the console gates the stream's SESSION exchange on. */
    int  senkusha_version_ack;   /* PROTOCOL_VERSION_ACK came back                             */
    int  senkusha_complete;      /* ...and the keyless SESSION exchange completed too          */

    /*
     * The stream SESSION exchange. `stream_keys_derived` is the milestone: it means the console's
     * SESSION_REPLY arrived, its ECDH point verified under the handshake key, and all four
     * per-direction key/IV values exist. The byte counts are recorded because a reply that arrives
     * and fails to verify is a different problem from one that never arrives.
     */
    /*
     * HOW FAR THE REQUEST GOT BEFORE IT DID NOT GET BUILT.
     *
     * b48 reported "the request was never built - launch spec, base64 or the ECDH backend", which named
     * three possibilities and distinguished none of them, and the same sequence with the same parameters
     * and the same buffer sizes succeeds on the host. A message that lists the suspects is not a
     * diagnosis; this records which step actually returned 0.
     */
    int  stream_build_step;      /* see RC_STREAM_STEP_* below - the last step that SUCCEEDED */
    unsigned launch_spec_bytes;  /* plaintext JSON length, before encryption and base64       */
    unsigned launch_spec_b64_bytes;

    unsigned session_request_bytes;
    unsigned session_reply_bytes;
    int  curve_p521;
    int  stream_version_acked;  /* the stream channel answered PROTOCOL_VERSION_ACK        */
    unsigned stream_version;    /* the version actually used - the console's choice if it
                                 * named one, otherwise the highest we offered. This picks
                                 * the ECDH curve, which is why it is reported.            */
    int  reply_reject_reason;  /* TAKION_SESSION_REJECT_* - why accept_reply said no */
    unsigned peer_key_length;  /* the console's ECDH point as it arrived: 65 = P-256, */
    unsigned peer_key_prefix;  /* 133 = P-521; prefix 0x04 = uncompressed             */
    /*
     * The console's ECDH point itself, kept so it can be examined off the console.
     *
     * An EPHEMERAL PUBLIC key for one session: not a secret, not tied to an account, and worthless once
     * the session ends. It is logged because the alternative is another round trip per hypothesis, and
     * the four P-521 vectors on this console pass the very check this point fails - a contradiction that
     * cannot be settled by reading code. It must not be committed: the log it lands in is dirty-room
     * material like every other capture.
     */
    unsigned char peer_key[133];

    int  ecdh_step;            /* RC_ECDH_STEP_* when the derivation itself failed    */
    int  ecdh_code;            /* ...and the backend's own return value               */

    /* The control experiment - see rc_connect.c. A known-good P-521 point and the console's own,
     * put through one validator at one call depth in one run. */
    int  control_point_ok;
    int  control_step;
    int  control_code;
    int  peer_point_ok;
    /* Stack at the deepest point of the session exchange - see rc_stack_ps3.h for why. */
    unsigned long stack_size;
    unsigned long stack_used;
    unsigned long stack_headroom;

    int  precheck_ok;          /* the same check run inside accept_reply, just before derive */
    int  precheck_step;
    int  precheck_code;
    unsigned long derive_fingerprint;  /* of the bytes derive_shared actually read   */
    unsigned long copy_fingerprint;    /* of the copy that passes the isolated check */
    unsigned derive_private_length;
    unsigned derive_curve;
    int  stream_keys_derived;

    /*
     * Past the keys. `sealing_on` means every outgoing control packet is now GMAC-authenticated, SACKs
     * included. STREAM_INFO is the console's own answer about the stream: the resolution it CHOSE,
     * which need not be the one asked for, and the SPS/PPS without which the first IDR cannot be
     * decoded - they are not carried in the video stream.
     */
    int  sealing_on;

    /*
     * Incoming authentication, counted rather than enforced on this build. The useful reading is the
     * proportion: a handful of failures among many is a different finding from all of them, and only
     * the second means the receive key schedule is wrong.
     */
    unsigned long verify_checked;
    unsigned long verify_failed;
    unsigned long verify_dropped;

    /* What turned up while the session was held open - see RC_STREAM_HOLD_MS. */
    unsigned held_messages;
    unsigned held_last_type;
    unsigned held_stream_info_repeats;
    unsigned heartbeats_sent;   /* ours, on the stream channel - the console's need no reply */

    /*
     * A/V, which shares the stream channel's UDP socket with the control association. Counted here
     * before anything tries to decode it: whether the console is sending at all, and whether the
     * packets authenticate under the A/V rule, are two separate questions and both come first.
     */
    unsigned av_packets;
    unsigned av_video;
    unsigned av_audio;
    unsigned av_other;
    unsigned av_verified;
    unsigned long av_bytes;
    int      held_channel_error;
    unsigned stream_info_bytes;
    int  stream_info_parsed;
    int  stream_info_acked;
    int  given_width;
    int  given_height;
    unsigned video_header_bytes;
    unsigned audio_header_bytes;
    int  asked_width;
    int  asked_height;
    int  asked_fps;
    int  heartbeats;         /* HEARTBEAT_REQ answered - proof the channel is live           */
} rc_connect_result;

/*
 * PROGRESS, REPORTED AS IT HAPPENS RATHER THAN AT THE END.
 *
 * The first version of this returned a result and let the caller log it afterwards, which is worthless
 * the moment something blocks: b31 hung on hardware and the log simply stopped after discovery, with
 * nothing at all from this module. That is the same failure the SPU probe had - a program that reports
 * only on completion has nothing to say about not completing - and the fix is the same shape. The
 * callback is invoked before each step that can block, so the last line in the log names the call that
 * did not return.
 */
typedef void (*rc_connect_log_fn)(const char *stage_text);

/*
 * Runs as far as it can and stops. `wake_timeout_ms` bounds the poll for is_awake. `log` may be NULL.
 * Returns the stage reached; `out` is filled either way, because how far it got is the finding when it
 * does not finish.
 */
/*
 * `dirs` is an ordered list of directories to look for "pairing.txt" in - pass the same list the log
 * probes, so the two cannot disagree about where this program keeps its files. NULL uses the
 * compile-time RC_CONNECT_PAIRING_DIR instead.
 */
rc_connect_stage rc_connect(unsigned wake_timeout_ms, rc_connect_log_fn log,
                            const char *const *dirs, int dir_count, rc_connect_result *out);

/* For the caller's report. Never includes anything from the pairing record. */
const char *rc_connect_stage_name(rc_connect_stage stage);

#ifdef __cplusplus
}
#endif

#endif /* RC_CONNECT_H */
