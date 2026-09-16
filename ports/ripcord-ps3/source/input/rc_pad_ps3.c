/* See rc_pad_ps3.h - especially on what a DualShock 3 cannot send. */
#include "rc_pad_ps3.h"

#include <io/pad.h>
#include <string.h>

#define RC_PAD_PORTS 7

static int s_open;
static int s_connected;
static unsigned s_reads;
static unsigned s_changes;

int rc_pad_open(void)
{
    if (s_open)
        return 1;
    if (ioPadInit(RC_PAD_PORTS) != 0)
        return 0;
    s_open = 1;
    return 1;
}

/*
 * A pad axis is 0..255 with 128 at rest; the wire wants a signed 16-bit value with left and up
 * NEGATIVE. The PS3's axes already run that way - 0 is left, and 0 is UP - so this scales without
 * inverting, which the 3DS port does have to do. Getting that backwards is invisible in a log and
 * obvious the moment someone tries to walk forwards.
 *
 * The two halves are scaled separately because the range is not symmetric: 128 counts below centre and
 * 127 above. One factor leaves either full-left short of the rail or full-right past it.
 */
static int16_t axis(unsigned int v)
{
    int c = (int)(v & 0xffu) - 128;

    if (c > 0)
        return (int16_t)((c * 32767) / 127);
    return (int16_t)(c * 256);
}

/*
 * A SMALL DEADZONE, and it is about packets as much as about aim.
 *
 * A DualShock 3's sticks do not rest at exactly 128 and they wander by a count or two untouched.
 * Without this the state differs from the previous one on nearly every poll, so a controller sitting on
 * a table sends continuously - and the drift is a real analog reading the console will act on, not
 * noise it will ignore. Small enough to cost no usable range.
 */
#define RC_PAD_DEADZONE 2048

static int16_t deadzone(int16_t v)
{
    return (v > -RC_PAD_DEADZONE && v < RC_PAD_DEADZONE) ? 0 : v;
}

int rc_pad_read(halyard_input_state *out)
{
    padInfo info;
    padData data;
    u32 port;

    if (!s_open || out == NULL)
        return 0;
    if (ioPadGetInfo(&info) != 0)
        return 0;

    for (port = 0u; port < (u32)RC_PAD_PORTS; port++) {
        if (info.status[port] == 0)
            continue;
        if (ioPadGetData(port, &data) != 0 || data.len == 0)
            continue;

        if (!s_connected) {
            s_connected = 1;
            s_changes++;
        }
        s_reads++;

        memset(out, 0, sizeof(*out));
        out->buttons =
              (data.BTN_CROSS    ? HALYARD_PAD_CROSS      : 0u)
            | (data.BTN_CIRCLE   ? HALYARD_PAD_CIRCLE     : 0u)
            | (data.BTN_SQUARE   ? HALYARD_PAD_SQUARE     : 0u)
            | (data.BTN_TRIANGLE ? HALYARD_PAD_TRIANGLE   : 0u)
            | (data.BTN_UP       ? HALYARD_PAD_DPAD_UP    : 0u)
            | (data.BTN_DOWN     ? HALYARD_PAD_DPAD_DOWN  : 0u)
            | (data.BTN_LEFT     ? HALYARD_PAD_DPAD_LEFT  : 0u)
            | (data.BTN_RIGHT    ? HALYARD_PAD_DPAD_RIGHT : 0u)
            | (data.BTN_L1       ? HALYARD_PAD_L1         : 0u)
            | (data.BTN_R1       ? HALYARD_PAD_R1         : 0u)
            | (data.BTN_L2       ? HALYARD_PAD_L2         : 0u)
            | (data.BTN_R2       ? HALYARD_PAD_R2         : 0u)
            | (data.BTN_L3       ? HALYARD_PAD_L3         : 0u)
            | (data.BTN_R3       ? HALYARD_PAD_R3         : 0u)
            /*
             * START is Options and SELECT is Create. The PS5 renamed both, and the console is told the
             * new names' codes; a DualShock 3 has the old buttons in the same places, so this maps by
             * position rather than by name.
             */
            | (data.BTN_START    ? HALYARD_PAD_OPTIONS    : 0u)
            | (data.BTN_SELECT   ? HALYARD_PAD_CREATE     : 0u);

        out->left_x = deadzone(axis(data.ANA_L_H));
        out->left_y = deadzone(axis(data.ANA_L_V));
        out->right_x = deadzone(axis(data.ANA_R_H));
        out->right_y = deadzone(axis(data.ANA_R_V));
        return 1;
    }

    if (s_connected) {
        s_connected = 0;
        s_changes++;
    }
    return 0;
}

void rc_pad_stats(int *connected, unsigned *reads, unsigned *changes)
{
    if (connected != NULL)
        *connected = s_connected;
    if (reads != NULL)
        *reads = s_reads;
    if (changes != NULL)
        *changes = s_changes;
}

void rc_pad_close(void)
{
    if (!s_open)
        return;
    (void)ioPadEnd();
    s_open = 0;
}
