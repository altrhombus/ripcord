/* See rc_session_state.h. */
#include "rc_session_state.h"

#include "rc_platform.h"

#include <string.h>

static void copy_into(char *dest, size_t size, const char *src)
{
    size_t n;

    if (src == NULL) {
        dest[0] = '\0';
        return;
    }
    n = strlen(src);
    if (n >= size)
        n = size - 1u;
    memcpy(dest, src, n);
    dest[n] = '\0';
}

void rc_session_state_reset(rc_session_state *state)
{
    if (state == NULL)
        return;
    memset(state, 0, sizeof(*state));
    state->changed_ms = rc_time_ms();
}

void rc_session_set(rc_session_state *state, rc_phase phase,
                    const char *headline, const char *detail, const char *hint)
{
    char new_headline[RC_SESSION_HEADLINE_MAX];
    char new_detail[RC_SESSION_DETAIL_MAX];
    char new_hint[RC_SESSION_HINT_MAX];

    if (state == NULL)
        return;

    copy_into(new_headline, sizeof(new_headline), headline);
    copy_into(new_detail, sizeof(new_detail), detail);
    copy_into(new_hint, sizeof(new_hint), hint);

    /*
     * NOTHING CHANGED IS NOT A CHANGE. Callers set this from inside loops - a connect step that runs
     * every tick sets "connecting" every tick - and a revision that moved anyway would make every
     * renderer redraw continuously to show the same words.
     */
    if (state->phase == phase
        && strcmp(state->headline, new_headline) == 0
        && strcmp(state->detail, new_detail) == 0
        && strcmp(state->hint, new_hint) == 0)
        return;

    state->phase = phase;
    memcpy(state->headline, new_headline, sizeof(new_headline));
    memcpy(state->detail, new_detail, sizeof(new_detail));
    memcpy(state->hint, new_hint, sizeof(new_hint));
    state->changed_ms = rc_time_ms();
    state->revision++;
}

void rc_session_progress(rc_session_state *state, rc_phase phase, const char *headline)
{
    rc_session_set(state, phase, headline, NULL, NULL);
}

void rc_session_fail(rc_session_state *state, const char *headline, const char *detail,
                     const char *hint)
{
    rc_session_set(state, RC_PHASE_FAILED, headline, detail, hint);
}

int rc_session_is_error(const rc_session_state *state)
{
    return state != NULL && state->phase == RC_PHASE_FAILED;
}

const char *rc_phase_name(rc_phase phase)
{
    switch (phase) {
    case RC_PHASE_IDLE:        return "idle";
    case RC_PHASE_DISCOVERING: return "discovering";
    case RC_PHASE_WAKING:      return "waking";
    case RC_PHASE_PAIRING:     return "pairing";
    case RC_PHASE_CONNECTING:  return "connecting";
    case RC_PHASE_STREAMING:   return "streaming";
    case RC_PHASE_ENDED:       return "ended";
    case RC_PHASE_FAILED:      return "failed";
    default:                   return "?";
    }
}
