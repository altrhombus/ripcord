/* See halyard_input.h for the wire format and where every field came from. */

#include "halyard_input.h"

#include <string.h>

#define STATE_LEAD_BYTE 0xa0u   /* [0x00], 100% of captured state packets */
#define STATE_TAIL_BYTE 0xcau   /* [0x1b], 98%; meaning unknown - see below */
#define SENSOR_REST 0x7fffu     /* the three gyro axes rest exactly here on a stationary pad */
#define RESTING_ORIENTATION 0u  /* packing is [I]; the spec permits a fixed value */

/*
 * Button code map, spec 6.3, table derived from cap48's scripted sequence. Codes 0x80-0x8b use the
 * 3-byte form with a trailing 0xff/0x00 state byte; 0x8c-0x90 use the 2-byte form with the press folded
 * into the code as +0x20. The whole observed code space is 0x80-0x90 and nothing else appeared.
 *
 * D-pad up/down are assigned BY ELIMINATION in the original derivation - both were pressed during an
 * earlier period of misbehaving input, so their first-press times cannot order them. Left/right are
 * directly timed. Carried across as-is rather than re-guessed: if it turns out inverted on hardware, the
 * fix belongs in the spec and in the .NET writer too, not quietly here.
 */
#define PRESSED_CODE_BIAS 0x20u

static const struct { uint32_t flag; uint8_t code; int state_in_code; } kButtonMap[] = {
    { HALYARD_PAD_CROSS,      0x88u, 0 },   /* t=77.1s */
    { HALYARD_PAD_CIRCLE,     0x89u, 0 },   /* t=78.9s */
    { HALYARD_PAD_SQUARE,     0x8au, 0 },   /* t=80.9s */
    { HALYARD_PAD_TRIANGLE,   0x8bu, 0 },   /* t=82.4s */
    { HALYARD_PAD_DPAD_UP,    0x80u, 0 },   /* by elimination */
    { HALYARD_PAD_DPAD_DOWN,  0x81u, 0 },   /* by elimination */
    { HALYARD_PAD_DPAD_LEFT,  0x82u, 0 },   /* t=87.8s */
    { HALYARD_PAD_DPAD_RIGHT, 0x83u, 0 },   /* t=89.1s */
    { HALYARD_PAD_L1,         0x84u, 0 },   /* t=91.4s */
    { HALYARD_PAD_R1,         0x85u, 0 },   /* t=92.5s */
    { HALYARD_PAD_L2,         0x86u, 0 },   /* analog on a real pad; digital here */
    { HALYARD_PAD_R2,         0x87u, 0 },
    { HALYARD_PAD_OPTIONS,    0x8cu, 1 },   /* t=119.6s */
    { HALYARD_PAD_CREATE,     0x8du, 1 },   /* t=124.0s */
    { HALYARD_PAD_PS,         0x8eu, 1 },   /* t=0.0s - opened the session */
    { HALYARD_PAD_L3,         0x8fu, 1 },   /* t=114.5s */
    { HALYARD_PAD_R3,         0x90u, 1 },   /* t=115.8s */
};
#define BUTTON_MAP_COUNT (sizeof(kButtonMap) / sizeof(kButtonMap[0]))

void halyard_input_writer_init(halyard_input_writer *w)
{
    if (w != NULL)
        memset(w, 0, sizeof(*w));
}

static void write_u16_le(uint8_t *p, uint16_t v)
{
    p[0] = (uint8_t)v;
    p[1] = (uint8_t)(v >> 8);
}

static void write_s16_be(uint8_t *p, int16_t v)
{
    p[0] = (uint8_t)((uint16_t)v >> 8);
    p[1] = (uint8_t)v;
}

size_t halyard_input_build_state_payload(const halyard_input_state *state, uint8_t *buf, size_t buf_size)
{
    if (state == NULL || buf == NULL || buf_size < HALYARD_INPUT_STATE_PAYLOAD)
        return 0;

    memset(buf, 0, HALYARD_INPUT_STATE_PAYLOAD);
    buf[0x00] = STATE_LEAD_BYTE;

    /*
     * Six u16 LE motion fields: three gyro then three accelerometer, the split established in cap48 by
     * their resting medians (gyro rests at exactly 0x7fff, accelerometer off-centre under gravity). The
     * 3DS has no motion sensor to report, and the spec is explicit that a pad without one may send the
     * resting values - so all six go out neutral rather than zero, which would be a real reading.
     */
    write_u16_le(buf + 0x01, SENSOR_REST);
    write_u16_le(buf + 0x03, SENSOR_REST);
    write_u16_le(buf + 0x05, SENSOR_REST);
    write_u16_le(buf + 0x07, SENSOR_REST);
    write_u16_le(buf + 0x09, SENSOR_REST);
    write_u16_le(buf + 0x0b, SENSOR_REST);

    /* Orientation: a 30-bit packed quaternion whose exact layout cap48 could not settle. Fixed value. */
    buf[0x0d] = (uint8_t)RESTING_ORIENTATION;
    buf[0x0e] = (uint8_t)(RESTING_ORIENTATION >> 8);
    buf[0x0f] = (uint8_t)(RESTING_ORIENTATION >> 16);
    buf[0x10] = (uint8_t)(RESTING_ORIENTATION >> 24);

    /* Four s16 BE stick axes, left/up negative. */
    write_s16_be(buf + 0x11, state->left_x);
    write_s16_be(buf + 0x13, state->left_y);
    write_s16_be(buf + 0x15, state->right_x);
    write_s16_be(buf + 0x17, state->right_y);

    /* [0x19]/[0x1a] stay zero. [0x1b] is 0xca in 98% of captured packets and its meaning is unknown -
     * the spec warns explicitly against "correcting" it to 1 without a capture that says so. */
    buf[0x1b] = STATE_TAIL_BYTE;
    return HALYARD_INPUT_STATE_PAYLOAD;
}

/* Prepend one event, pushing the oldest out. Newest-first is the wire's ordering, not a convenience. */
static void push_event(halyard_input_writer *w, const uint8_t *event, size_t length)
{
    int i;

    for (i = HALYARD_INPUT_HISTORY_RESEND - 1; i > 0; i--) {
        memcpy(w->history[i], w->history[i - 1], HALYARD_INPUT_EVENT_MAX);
        w->history_len[i] = w->history_len[i - 1];
    }
    memcpy(w->history[0], event, length);
    w->history_len[0] = (uint8_t)length;
    if (w->history_count < HALYARD_INPUT_HISTORY_RESEND)
        w->history_count++;
}

size_t halyard_input_build_history_payload(halyard_input_writer *w,
                                           const halyard_input_state *state,
                                           uint8_t *buf, size_t buf_size)
{
    size_t n = 0;
    size_t i;
    uint32_t changed;
    int fresh = 0;

    if (w == NULL || state == NULL || buf == NULL)
        return 0;

    /* First frame ever: nothing to diff against, and reporting every unpressed button as a release would
     * be noise. The state packet carries the analog picture; buttons start reporting on their first
     * actual transition. */
    if (!w->have_previous)
        return 0;

    changed = w->previous.buttons ^ state->buttons;
    if (changed == 0u)
        return 0;

    /* Record this frame's transitions, newest last so the most recent ends up at the head. */
    for (i = 0; i < BUTTON_MAP_COUNT; i++) {
        uint8_t event[HALYARD_INPUT_EVENT_MAX];
        size_t length;
        int pressed;

        if ((changed & kButtonMap[i].flag) == 0u)
            continue;
        pressed = (state->buttons & kButtonMap[i].flag) != 0u;

        event[0] = 0x80u;
        if (kButtonMap[i].state_in_code) {
            event[1] = (uint8_t)(pressed ? kButtonMap[i].code + PRESSED_CODE_BIAS
                                         : kButtonMap[i].code);
            length = 2u;
        } else {
            event[1] = kButtonMap[i].code;
            event[2] = pressed ? 0xffu : 0x00u;
            length = 3u;
        }
        push_event(w, event, length);
        fresh = 1;
    }

    if (!fresh)
        return 0;

    /* Serialise the whole list, newest first - including events already sent, which is the point. */
    for (i = 0; i < (size_t)w->history_count; i++) {
        if (n + w->history_len[i] > buf_size)
            break;
        memcpy(buf + n, w->history[i], w->history_len[i]);
        n += w->history_len[i];
    }
    return n;
}

size_t halyard_input_write_header(uint8_t type, uint16_t sequence, uint8_t *buf, size_t buf_size)
{
    if (buf == NULL || buf_size < HALYARD_INPUT_HEADER_LENGTH)
        return 0;

    memset(buf, 0, HALYARD_INPUT_HEADER_LENGTH);
    buf[0] = type;
    /* Sequence is BIG-endian, unlike the little-endian CTR counter this same packet's crypto uses. */
    buf[1] = (uint8_t)(sequence >> 8);
    buf[2] = (uint8_t)sequence;
    /* [3] zero; [4..7] key position and [8..11] tag are the sealer's. */
    return HALYARD_INPUT_HEADER_LENGTH;
}
