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
    int  credential_ok;      /* the wake credential derived from the registration key      */
    int  wake_source_bound;  /* the WAKEUP went out from the source port the spec names    */
    int  wakeups_sent;
    int  polls;              /* SRCH polls while waiting for is_awake to flip              */
    int  was_asleep;         /* it answered 620 Server Standby before any wake was sent    */
    unsigned woke_after_ms;  /* how long the flip took, for the record                     */
    int  session_error;      /* whatever the control session reported, if it failed        */
} rc_connect_result;

/*
 * Runs as far as it can and stops. `wake_timeout_ms` bounds the poll for is_awake. Returns the stage
 * reached; `out` is filled either way, because how far it got is the finding when it does not finish.
 */
rc_connect_stage rc_connect(unsigned wake_timeout_ms, rc_connect_result *out);

/* For the caller's report. Never includes anything from the pairing record. */
const char *rc_connect_stage_name(rc_connect_stage stage);

#ifdef __cplusplus
}
#endif

#endif /* RC_CONNECT_H */
