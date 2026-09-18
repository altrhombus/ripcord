/*
 * ripcord - what the client is doing, and what went wrong, in a form any front end can render.
 *
 * WHY THIS EXISTS. Until now a failure had nowhere to go. A dropped stream, a refused pairing, a wrong
 * PIN and a console that never answered all presented the same way on a television - a black screen -
 * while the actual reason went into a log file readable afterwards, over FTP, from another machine.
 * That is fine for bring-up and useless for someone using the thing.
 *
 * SHAPED AFTER Ripcord.Presentation, which keeps the same rule: no front end's types cross into here.
 * There is no colour, no font, no widget and no console API in this file - a phase and three strings,
 * which each front end renders however it can. A 3DS with two small screens and a PS3 with a television
 * disagree about everything except what they need to say.
 *
 * THE THIRD STRING IS THE ONE THAT MATTERS. `headline` says what is happening and `detail` says why,
 * which between them make a good log line and a poor screen. `hint` says what the person in front of it
 * can DO - and if a failure has no useful hint, that is worth noticing at the point it is written
 * rather than discovered by someone stuck.
 */
#ifndef RC_SESSION_STATE_H
#define RC_SESSION_STATE_H

#include <stdint.h>

typedef enum {
    RC_PHASE_IDLE = 0,
    RC_PHASE_DISCOVERING,
    RC_PHASE_WAKING,
    RC_PHASE_PAIRING,
    RC_PHASE_CONNECTING,
    RC_PHASE_STREAMING,
    RC_PHASE_ENDED,          /* the session finished the way it was asked to */
    RC_PHASE_FAILED
} rc_phase;

#define RC_SESSION_HEADLINE_MAX 48
#define RC_SESSION_DETAIL_MAX   96
#define RC_SESSION_HINT_MAX     96

typedef struct {
    rc_phase phase;
    char     headline[RC_SESSION_HEADLINE_MAX];
    char     detail[RC_SESSION_DETAIL_MAX];
    char     hint[RC_SESSION_HINT_MAX];

    /*
     * When the phase last changed, so a front end can tell a slow step from a stuck one without
     * keeping its own clock. A step that has not moved in thirty seconds is worth saying so about.
     */
    uint64_t changed_ms;

    /* Bumped on every change, so a renderer can redraw only when there is something new. */
    unsigned revision;
} rc_session_state;

void rc_session_state_reset(rc_session_state *state);

/*
 * Sets the phase and its three strings. `detail` and `hint` may be NULL, which clears them - a phase
 * that is simply progressing does not need to explain itself.
 *
 * Writing the same phase and strings again is NOT a change: the revision only moves when something a
 * viewer could notice moved. That is what lets a caller set the state unconditionally in a loop.
 */
void rc_session_set(rc_session_state *state, rc_phase phase,
                    const char *headline, const char *detail, const char *hint);

/* Convenience for the common "still going, nothing to explain" case. */
void rc_session_progress(rc_session_state *state, rc_phase phase, const char *headline);

/* A failure, which always takes a hint - see the note at the top on why that argument is not optional. */
void rc_session_fail(rc_session_state *state, const char *headline, const char *detail,
                     const char *hint);

int rc_session_is_error(const rc_session_state *state);
const char *rc_phase_name(rc_phase phase);

#endif /* RC_SESSION_STATE_H */
