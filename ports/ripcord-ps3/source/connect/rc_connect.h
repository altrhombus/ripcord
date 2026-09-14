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

typedef enum {
    RC_CONNECT_NO_RECORD = 0,   /* no pairing record on the console - skip, not a failure */
    RC_CONNECT_BAD_RECORD,      /* the file was there and unusable                        */
    RC_CONNECT_NO_CONSOLE,      /* nothing answered SRCH at the recorded address          */
    RC_CONNECT_WAKE_SENT,       /* asleep, WAKEUP sent, never woke within the deadline    */
    RC_CONNECT_AWAKE,           /* the console is awake - as far as this probe goes       */
    RC_CONNECT_SESSION_OPEN,    /* the control session opened                             */
    RC_CONNECT_SESSION_READY    /* SESSION_ID seen - the console is willing to stream      */
} rc_connect_stage;

typedef struct {
    rc_connect_stage stage;
    int  had_record;
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
rc_connect_stage rc_connect(unsigned wake_timeout_ms, rc_connect_log_fn log, rc_connect_result *out);

/* For the caller's report. Never includes anything from the pairing record. */
const char *rc_connect_stage_name(rc_connect_stage stage);

#ifdef __cplusplus
}
#endif

#endif /* RC_CONNECT_H */
