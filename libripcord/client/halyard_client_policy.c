/* See halyard_client_policy.h. No sockets, no clock of its own: every "now" is passed in. */
#include "halyard_client_policy.h"

#include <string.h>

void halyard_client_config_resolve(const halyard_client_config *in, halyard_client_config *out)
{
    halyard_client_config c;

    if (in == NULL || out == NULL)
        return;
    c = *in;

    if (c.route == 0)
        c.route = HALYARD_ROUTE_LOCAL;

    if (c.width <= 0 || c.height <= 0) {
        c.width = HALYARD_CLIENT_DEFAULT_WIDTH;
        c.height = HALYARD_CLIENT_DEFAULT_HEIGHT;
    }
    if (c.fps == 0)
        c.fps = HALYARD_CLIENT_DEFAULT_FPS;
    c.fps = (c.fps > 30) ? 60 : 30;
    if (c.bitrate_kbps <= 0)
        c.bitrate_kbps = HALYARD_CLIENT_DEFAULT_BITRATE_KBPS;
    c.allow_hevc = (c.allow_hevc != 0);
    c.hdr = (c.hdr != 0);

    if (c.signin_prompt_window_ms == 0u)
        c.signin_prompt_window_ms = (c.route == HALYARD_ROUTE_RENDEZVOUS)
                                        ? HALYARD_CLIENT_DEFAULT_RENDEZVOUS_SIGNIN_WINDOW_MS
                                        : HALYARD_CLIENT_DEFAULT_SIGNIN_WINDOW_MS;
    if (c.senkusha_attempts == 0u)
        c.senkusha_attempts = HALYARD_CLIENT_DEFAULT_SENKUSHA_ATTEMPTS;
    if (c.stream_attempts == 0u)
        c.stream_attempts = HALYARD_CLIENT_DEFAULT_STREAM_ATTEMPTS;
    if (c.attempt_interval_ms == 0u)
        c.attempt_interval_ms = HALYARD_CLIENT_DEFAULT_ATTEMPT_INTERVAL_MS;
    if (c.rcvbuf_bytes == 0)
        c.rcvbuf_bytes = HALYARD_CLIENT_DEFAULT_RCVBUF;
    else if (c.rcvbuf_bytes < 0)
        c.rcvbuf_bytes = 0;

    c.require_session_ready = (c.require_session_ready >= 0) ? 1 : 0;

    if (c.stun_servers == NULL)
        c.stun_server_count = 0u;
    if (c.stun_server_count > HALYARD_CLIENT_STUN_MAX)
        c.stun_server_count = HALYARD_CLIENT_STUN_MAX;
    if (c.stun_attempts == 0u)
        c.stun_attempts = HALYARD_CLIENT_DEFAULT_STUN_ATTEMPTS;
    if (c.stun_timeout_ms == 0u)
        c.stun_timeout_ms = HALYARD_CLIENT_DEFAULT_STUN_TIMEOUT_MS;
    if (c.media_offer_timeout_ms == 0u)
        c.media_offer_timeout_ms = HALYARD_CLIENT_DEFAULT_MEDIA_OFFER_TIMEOUT_MS;
    if (c.dgram_stage_timeout_ms == 0u)
        c.dgram_stage_timeout_ms = HALYARD_CLIENT_DEFAULT_DGRAM_STAGE_TIMEOUT_MS;
    if (c.dgram_receive_timeout_ms == 0u)
        c.dgram_receive_timeout_ms = HALYARD_CLIENT_DEFAULT_DGRAM_RECEIVE_TIMEOUT_MS;

    *out = c;
}

void halyard_client_idr_reset(halyard_client_idr_latch *latch)
{
    if (latch != NULL)
        memset(latch, 0, sizeof(*latch));
}

void halyard_client_idr_arm(halyard_client_idr_latch *latch)
{
    if (latch != NULL)
        latch->awaiting = 1;
}

void halyard_client_idr_keyframe(halyard_client_idr_latch *latch)
{
    if (latch != NULL)
        latch->awaiting = 0;
}

int halyard_client_idr_due(halyard_client_idr_latch *latch, uint64_t now_ms)
{
    if (latch == NULL || !latch->awaiting)
        return 0;
    /*
     * The first request is never throttled. The PS3 keeps its last-request time at 0 and relies on the
     * clock being far from 0, which holds for its epoch and not for every clock rc_time_ms() may use.
     */
    if (latch->requested_once && now_ms - latch->last_request_ms < (uint64_t)HALYARD_CLIENT_IDR_MIN_MS)
        return 0;
    latch->requested_once = 1;
    latch->last_request_ms = now_ms;
    latch->requests++;
    return 1;
}

uint32_t halyard_client_idr_wait_ms(const halyard_client_idr_latch *latch, uint64_t now_ms)
{
    uint64_t elapsed;

    if (latch == NULL || !latch->awaiting)
        return UINT32_MAX;
    if (!latch->requested_once)
        return 0u;
    elapsed = now_ms - latch->last_request_ms;
    if (elapsed >= (uint64_t)HALYARD_CLIENT_IDR_MIN_MS)
        return 0u;
    return (uint32_t)((uint64_t)HALYARD_CLIENT_IDR_MIN_MS - elapsed);
}

int halyard_client_timer_due(uint64_t *next_ms, uint64_t now_ms, uint32_t interval_ms)
{
    if (next_ms == NULL || now_ms < *next_ms)
        return 0;
    *next_ms = now_ms + (uint64_t)interval_ms;
    return 1;
}

uint32_t halyard_client_timer_wait_ms(uint64_t next_ms, uint64_t now_ms)
{
    uint64_t wait;

    if (now_ms >= next_ms)
        return 0u;
    wait = next_ms - now_ms;
    return (wait > (uint64_t)UINT32_MAX) ? UINT32_MAX : (uint32_t)wait;
}

int halyard_client_input_equal(const halyard_input_state *a, const halyard_input_state *b)
{
    return a->buttons == b->buttons
        && a->left_x == b->left_x && a->left_y == b->left_y
        && a->right_x == b->right_x && a->right_y == b->right_y
        && a->left_trigger == b->left_trigger && a->right_trigger == b->right_trigger;
}

void halyard_client_input_step(halyard_client_input_cadence *cadence, halyard_input_writer *writer,
                               const halyard_input_state *state, uint64_t now_ms,
                               halyard_client_input_packets *out)
{
    size_t payload_length;
    int changed;

    if (out == NULL)
        return;
    out->history_length = 0u;
    out->state_length = 0u;
    if (cadence == NULL || writer == NULL || state == NULL)
        return;

    /* Decided against the previous frame BEFORE the history build touches the writer's event list. */
    changed = !writer->have_previous || !halyard_client_input_equal(state, &writer->previous);

    payload_length = halyard_input_build_history_payload(writer, state,
                                                         out->history + HALYARD_INPUT_HEADER_LENGTH,
                                                         sizeof(out->history) - HALYARD_INPUT_HEADER_LENGTH);
    if (payload_length > 0u) {
        (void)halyard_input_write_header(HALYARD_CLIENT_INPUT_TYPE_HISTORY, writer->history_seq++,
                                         out->history, sizeof(out->history));
        out->history_length = HALYARD_INPUT_HEADER_LENGTH + payload_length;
    }

    if (changed || !cadence->state_sent
        || now_ms - cadence->last_state_ms >= (uint64_t)HALYARD_CLIENT_INPUT_STATE_MS) {
        payload_length = halyard_input_build_state_payload(state, out->state + HALYARD_INPUT_HEADER_LENGTH,
                                                           sizeof(out->state) - HALYARD_INPUT_HEADER_LENGTH);
        if (payload_length > 0u) {
            (void)halyard_input_write_header(HALYARD_CLIENT_INPUT_TYPE_STATE, writer->state_seq++,
                                             out->state, sizeof(out->state));
            out->state_length = HALYARD_INPUT_HEADER_LENGTH + payload_length;
            cadence->last_state_ms = now_ms;
            cadence->state_sent = 1;
        }
    }

    writer->previous = *state;
    writer->have_previous = 1;
}

uint32_t halyard_client_kbps(uint64_t bytes, uint64_t window_ms)
{
    uint64_t kbps;

    if (window_ms == 0u)
        return 0u;
    if (bytes > UINT64_MAX / 8u)
        return UINT32_MAX;
    kbps = (bytes * 8u) / window_ms;
    return (kbps > (uint64_t)UINT32_MAX) ? UINT32_MAX : (uint32_t)kbps;
}
