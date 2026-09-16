/* See rc_osk_ps3.h. */
#include "rc_osk_ps3.h"

#include "rc_platform.h"

#include <sys/memory.h>
#include <sysutil/osk.h>
#include <sysutil/sysutil.h>

#include <string.h>

/*
 * How long to wait for the user. Generous: someone typing an eight-digit PIN off another screen, or an
 * address they have to go and look up, is not in a hurry and should not be timed out mid-word. This
 * only exists so a dialog that never reports back cannot hang the program forever.
 */
#define RC_OSK_TIMEOUT_MS 180000u

/*
 * The dialog needs a memory container of its own. A megabyte is what the SDK's own samples use; sizing
 * it from first principles is not possible from here, and guessing smaller is how a keyboard fails to
 * appear on the one layout nobody tested.
 */
#define RC_OSK_CONTAINER_BYTES (1024u * 1024u)

#define RC_OSK_MAX_CHARS 64

static u16 s_message[RC_OSK_MAX_CHARS + 1];
static u16 s_initial[RC_OSK_MAX_CHARS + 1];
static u16 s_result[RC_OSK_MAX_CHARS + 1];

static volatile int s_done;
static volatile int s_cancelled;

const char *rc_osk_status_text(rc_osk_status status)
{
    switch (status) {
    case RC_OSK_OK:          return "ok";
    case RC_OSK_CANCELLED:   return "cancelled";
    case RC_OSK_UNAVAILABLE: return "the keyboard could not be shown";
    case RC_OSK_TOO_LONG:    return "that is too long, or has characters this cannot use";
    case RC_OSK_TIMED_OUT:   return "the keyboard was left open too long";
    default:                 return "?";
    }
}

/* ASCII to UTF-16 and back. Both refuse anything outside ASCII rather than truncating it - see the
 * header on why a mangled digit is worse than a refusal. */
static void widen(const char *in, u16 *out, size_t out_chars)
{
    size_t i = 0;

    if (in != NULL) {
        for (; in[i] != '\0' && i + 1u < out_chars; i++)
            out[i] = (u16)(unsigned char)in[i];
    }
    out[i] = 0u;
}

static int narrow(const u16 *in, char *out, size_t out_size)
{
    size_t i;

    for (i = 0; in[i] != 0u; i++) {
        if (i + 1u >= out_size)
            return 0;
        if (in[i] > 0x7fu)
            return 0;
        out[i] = (char)in[i];
    }
    out[i] = '\0';
    return 1;
}

static void osk_event(u64 status, u64 param, void *user)
{
    (void)param;
    (void)user;

    switch (status) {
    case SYSUTIL_OSK_INPUT_CANCELED:
        s_cancelled = 1;
        break;
    case SYSUTIL_OSK_DONE:
        /*
         * THE TEXT IS COLLECTED HERE, not after unloading. oskGetInputText reads the dialog's own
         * buffer, and that buffer belongs to a dialog which is about to be torn down.
         */
        {
            oskCallbackReturnParam result;

            memset(&result, 0, sizeof(result));
            result.str = s_result;
            if (oskGetInputText(&result) != 0 || result.res != OSK_OK)
                s_cancelled = 1;
        }
        oskUnloadAsync(NULL);
        break;
    case SYSUTIL_OSK_UNLOADED:
        s_done = 1;
        break;
    default:
        break;
    }
}

rc_osk_status rc_osk_ask(rc_osk_kind kind, const char *prompt, const char *initial,
                         char *out, size_t out_size)
{
    sys_mem_container_t container = 0;
    oskParam param;
    oskInputFieldInfo field;
    oskPoint point;
    uint64_t deadline;
    rc_osk_status status = RC_OSK_UNAVAILABLE;

    if (out == NULL || out_size == 0u)
        return RC_OSK_UNAVAILABLE;
    out[0] = '\0';

    if (sysMemContainerCreate(&container, RC_OSK_CONTAINER_BYTES) != 0)
        return RC_OSK_UNAVAILABLE;

    widen(prompt, s_message, RC_OSK_MAX_CHARS + 1u);
    widen(initial, s_initial, RC_OSK_MAX_CHARS + 1u);
    memset(s_result, 0, sizeof(s_result));

    memset(&param, 0, sizeof(param));
    /*
     * A KEYPAD FOR A PIN AND A FULL KEYBOARD FOR EVERYTHING ELSE. Not decoration: the PIN is eight
     * digits read off another screen and typed with a controller, and offering the letters as well
     * makes that several times slower for no gain.
     */
    param.allowedPanels = (kind == RC_OSK_NUMBERS) ? OSK_PANEL_TYPE_NUMERAL
                                                   : (OSK_PANEL_TYPE_ALPHABET | OSK_PANEL_TYPE_NUMERAL);
    param.firstViewPanel = (kind == RC_OSK_NUMBERS) ? OSK_PANEL_TYPE_NUMERAL : OSK_PANEL_TYPE_ALPHABET;
    point.x = 0.0f;
    point.y = 0.0f;
    param.controlPoint = point;
    param.prohibitFlags = OSK_PROHIBIT_RETURN;   /* single-line answers; a newline is never wanted */

    memset(&field, 0, sizeof(field));
    field.message = s_message;
    field.startText = s_initial;
    field.maxLength = (s32)((out_size - 1u < RC_OSK_MAX_CHARS) ? out_size - 1u : RC_OSK_MAX_CHARS);

    s_done = 0;
    s_cancelled = 0;

    if (sysUtilRegisterCallback(SYSUTIL_EVENT_SLOT0, osk_event, NULL) != 0) {
        sysMemContainerDestroy(container);
        return RC_OSK_UNAVAILABLE;
    }

    oskSetLayoutMode(OSK_LAYOUTMODE_HORIZONTAL_ALIGN_CENTER | OSK_LAYOUTMODE_VERTICAL_ALIGN_CENTER);
    oskSetInitialInputDevice(OSK_DEVICE_PAD);

    if (oskLoadAsync(container, &param, &field) != 0) {
        sysUtilUnregisterCallback(SYSUTIL_EVENT_SLOT0);
        sysMemContainerDestroy(container);
        return RC_OSK_UNAVAILABLE;
    }

    /*
     * PUMP UNTIL IT SAYS IT IS FINISHED. sysUtilCheckCallback is what actually delivers the events
     * above; without it the dialog appears, accepts input, and never tells anyone.
     */
    deadline = rc_time_ms() + RC_OSK_TIMEOUT_MS;
    while (!s_done && rc_time_ms() < deadline) {
        sysUtilCheckCallback();
        rc_sleep_ms(16u);
    }

    sysUtilUnregisterCallback(SYSUTIL_EVENT_SLOT0);
    sysMemContainerDestroy(container);

    if (!s_done)
        return RC_OSK_TIMED_OUT;
    if (s_cancelled)
        return RC_OSK_CANCELLED;
    status = narrow(s_result, out, out_size) ? RC_OSK_OK : RC_OSK_TOO_LONG;
    return status;
}
