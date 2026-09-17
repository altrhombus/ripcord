/* See rc_osk_ps3.h. */
#include "rc_osk_ps3.h"

#include "rc_log.h"

#include <ppu-asm.h>

#include "rc_platform.h"
#include "rc_video_ps3.h"

#include <sys/memory.h>
#include <sysutil/osk.h>
#include <sysutil/sysutil.h>

#include <string.h>


/*
 * How long to wait for the user. Generous: someone typing an eight-digit PIN off another screen, or an
 * address they have to go and look up, is not in a hurry and should not be timed out mid-word. This
 * only exists so a dialog that never reports back cannot hang the program forever.
 */
#define RC_OSK_TIMEOUT_MS 45000u

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

static unsigned s_container_bytes;

/*
 * What to draw behind the keyboard, each frame it is up. See the pump loop: the dialog is composited
 * into the application's own presentation, so something has to keep presenting.
 */
static void (*s_present)(void);

void rc_osk_set_present_hook(void (*present)(void))
{
    s_present = present;
}
static volatile int s_done;
static volatile int s_finished;   /* the user closed it; the text has not been collected yet */

/* The unload's own return structure, and the buffer it may fill - see where it is passed. Static
 * because the unload is asynchronous and may write to it after the call returns. */
static oskCallbackReturnParam s_unload_result;
static u16 s_unload_text[RC_OSK_MAX_CHARS + 1];
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

/*
 * EVERY EVENT THIS RECEIVES IS RECORDED, and none of them is logged from in here.
 *
 * b311 timed out with "the callback is not being delivered", which is one of two very different
 * things: the callback is never called at all, or it is called with statuses this does not recognise.
 * Those need opposite fixes and the timeout cannot tell them apart.
 *
 * Recorded into an array rather than logged directly because this runs from inside a system callback,
 * and rc_log opens and writes a file. The pump loop prints the list afterwards, where that is safe.
 */
#define RC_OSK_EVENT_LOG 16
static volatile unsigned s_events[RC_OSK_EVENT_LOG];
static volatile unsigned s_event_count;

static void handle_event(u64 status);

/* The one callback. Events are recorded and flags set; nothing here touches the dialog - see
 * handle_event for what doing so cost. */
static void osk_event(u64 status, u64 param, void *user)
{
    (void)param;
    (void)user;
    handle_event(status);
}

static void handle_event(u64 status)
{
    if (s_event_count < RC_OSK_EVENT_LOG)
        s_events[s_event_count] = (unsigned)status;
    s_event_count++;

    /*
     * THIS ONLY RECORDS. Nothing here calls back into the OSK, and b315 is why.
     *
     * The first version collected the text and tore the dialog down from inside this function, which
     * is a system callback. Two things went wrong with that. It ran twice, because two callbacks were
     * registered during the A/B and each did the teardown. And calling a dialog's own API from inside
     * its event delivery is asking the library to re-enter itself - which is the likeliest reason
     * UNLOADED never arrived and the pump waited out its whole timeout.
     *
     * So the flags are set here and the work happens in the loop, where calling into the SDK is
     * ordinary.
     */
    switch (status) {
    case SYSUTIL_OSK_INPUT_CANCELED:
        s_cancelled = 1;
        s_finished = 1;
        break;
    case SYSUTIL_OSK_DONE:
        s_finished = 1;
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
    uint64_t deadline;
    rc_osk_status status = RC_OSK_UNAVAILABLE;

    if (out == NULL || out_size == 0u)
        return RC_OSK_UNAVAILABLE;
    out[0] = '\0';

    widen(prompt, s_message, RC_OSK_MAX_CHARS + 1u);
    widen(initial, s_initial, RC_OSK_MAX_CHARS + 1u);
    memset(s_result, 0, sizeof(s_result));

    memset(&field, 0, sizeof(field));
    field.message = s_message;
    field.startText = s_initial;
    field.maxLength = (s32)((out_size - 1u < RC_OSK_MAX_CHARS) ? out_size - 1u : RC_OSK_MAX_CHARS);

    s_done = 0;
    s_finished = 0;
    s_cancelled = 0;
    s_container_bytes = 0u;
    s_event_count = 0u;

    {
        s32 rc = sysUtilRegisterCallback(SYSUTIL_EVENT_SLOT0, osk_event, NULL);

        if (rc != 0) {
            rc_log("osk:   sysUtilRegisterCallback refused (0x%08X)\n", (unsigned)rc);
            return RC_OSK_UNAVAILABLE;
        }
        /*
         * ONE REGISTRATION. b315 registered two - the plain wrapper and the Ex form with a 32-bit
         * descriptor - to settle whether this platform's usual descriptor trap applied here. It does
         * not: every event arrived on BOTH, so PSL1GHT's wrapper adapts the pointer correctly and the
         * A/B is finished with. It also did harm, which is the note worth keeping: two registrations
         * meant the DONE handler ran twice, and it was a handler that tore the dialog down.
         */
    }

    /*
     * THE CONTAINER SIZE IS PART OF THE LOAD SWEEP, and separating them is what made b306 useless.
     *
     * b304 swept container sizes DOWNWARD - a megabyte, then a half, then a quarter - on the reasoning
     * that a binary with 54 MB of BSS might not have a megabyte to spare. The first size succeeded, so
     * that sweep proved only that a container can be created. b306 then varied five parameter shapes
     * against that one container and all five were refused identically.
     *
     * The error is 0x8002B504, a PARAMETER error, and a container that is too SMALL is a bad parameter
     * just as surely as a missing flag is. Sweeping only downward could never have found it, and
     * choosing the size before the load sweep meant no shape tried afterwards could have helped.
     *
     * So size is the outer loop and it goes UP: eight megabytes down to one. The general form is worth
     * keeping - when a call rejects an argument and will not say which, an axis swept in one direction
     * is an axis half tested.
     */
    {
        static const unsigned kSizes[] = {
            8u * 1024u * 1024u, 4u * 1024u * 1024u, 2u * 1024u * 1024u, 1024u * 1024u
        };
        unsigned i;
        s32 rc = -1;

        for (i = 0u; i < sizeof(kSizes) / sizeof(kSizes[0]) && rc != 0; i++) {
            oskPoint point;

            if (sysMemContainerCreate(&container, kSizes[i]) != 0) {
                rc_log("osk:   no container of %u bytes\n", kSizes[i]);
                continue;
            }

            memset(&param, 0, sizeof(param));
            param.allowedPanels = (kind == RC_OSK_NUMBERS)
                ? OSK_PANEL_TYPE_NUMERAL
                : (OSK_PANEL_TYPE_ALPHABET | OSK_PANEL_TYPE_NUMERAL);
            param.firstViewPanel = (kind == RC_OSK_NUMBERS) ? OSK_PANEL_TYPE_NUMERAL
                                                            : OSK_PANEL_TYPE_ALPHABET;
            point.x = 0.0f;
            point.y = 0.0f;
            param.controlPoint = point;
            param.prohibitFlags = OSK_PROHIBIT_RETURN;

            oskSetKeyLayoutOption(OSK_10KEY_PANEL | OSK_FULLKEY_PANEL);
            oskSetInitialInputDevice(OSK_DEVICE_PAD);
            oskSetDeviceMask(OSK_DEVICE_MASK_PAD);

            rc = oskLoadAsync(container, &param, &field);
            if (rc == 0) {
                s_container_bytes = kSizes[i];
                rc_log("osk:   dialog raised with a %u byte container\n", kSizes[i]);
                break;
            }
            rc_log("osk:   refused (0x%08X) with a %u byte container\n", (unsigned)rc, kSizes[i]);
            sysMemContainerDestroy(container);
            container = 0;
        }

        if (rc != 0) {
            sysUtilUnregisterCallback(SYSUTIL_EVENT_SLOT0);
            return RC_OSK_UNAVAILABLE;
        }
    }

    /*
     * PUMP, AND KEEP PRESENTING. Two things, and b308 proved the second is not optional.
     *
     * sysUtilCheckCallback is what delivers the events above; without it the dialog accepts input and
     * never tells anyone. That much was there.
     *
     * WHAT WAS MISSING IS THE FLIP. A system dialog on this machine does not draw itself onto the
     * screen - it composites into the APPLICATION'S flip stream, so an application that stops
     * presenting stops the dialog appearing at all. b308 raised it successfully and then sat in a loop
     * that called nothing but the callback pump, so nothing was ever shown and the three-minute
     * timeout read as a hang.
     *
     * The hook is the caller's because this file has no idea what should be behind the keyboard. It
     * draws and flips; here that is one call a frame.
     */
    deadline = rc_time_ms() + RC_OSK_TIMEOUT_MS;
    {
        int collected = 0;

        while (!s_done && rc_time_ms() < deadline) {
            sysUtilCheckCallback();

            /*
             * COLLECTED AND TORN DOWN HERE, once, outside the callback. The text is read before the
             * unload because oskGetInputText reads the dialog's own buffer and the unload takes it
             * away.
             */
            if (s_finished && !collected) {
                oskCallbackReturnParam result;
                s32 got;

                collected = 1;
                memset(&result, 0, sizeof(result));
                result.str = s_result;
                /*
                 * `len` set BEFORE the call as well as read after it. Whether it is an out-parameter
                 * or an in-out capacity is not stated anywhere reachable, and setting it costs nothing
                 * if it is the former - where leaving it zero would be fatal if it is the latter.
                 */
                result.len = (s32)RC_OSK_MAX_CHARS;
                got = oskGetInputText(&result);

                /*
                 * THE TWO FAILURES ARE REPORTED SEPARATELY, because collapsing them is what made b318
                 * say "cancelled" about a dialog the user had accepted. A call that refused and a call
                 * that succeeded while reporting a result other than OK are different faults, and one
                 * flag for both says nothing about which.
                 *
                 * res is OSK_OK, OSK_CANCELED, OSK_ABORT or OSK_NO_TEXT - and the last of those is a
                 * dialog that came back empty, which is not the same as one that was dismissed.
                 */
                rc_log("osk:   getInputText -> 0x%08X, res %d, len %d\n",
                       (unsigned)got, (int)result.res, (int)result.len);
                if (got != 0 || result.res != OSK_OK)
                    s_cancelled = 1;

                /*
                 * THE UNLOAD'S RETURN PARAMETER MAY BE WHERE THE TEXT ACTUALLY IS.
                 *
                 * oskUnloadAsync takes an oskCallbackReturnParam, which is the same structure
                 * oskGetInputText fills - so the library may intend the result to come back from the
                 * teardown rather than from a separate read, and this port has no documentation
                 * saying which. Both are captured into separate structures and both are reported;
                 * whichever says OK is the one used.
                 */
                memset(&s_unload_result, 0, sizeof(s_unload_result));
                s_unload_result.str = s_unload_text;
                s_unload_result.len = (s32)RC_OSK_MAX_CHARS;
                oskUnloadAsync(&s_unload_result);
            }

            if (s_present != NULL)
                s_present();
            else
                rc_video_flip();
            rc_sleep_ms(16u);
        }
    }

    sysUtilUnregisterCallback(SYSUTIL_EVENT_SLOT0);
    if (s_container_bytes != 0u)
        sysMemContainerDestroy(container);

    /*
     * WHAT ARRIVED, whatever the outcome. The expected set is 0x0502 loaded, 0x0503 done, 0x0504
     * unloaded, and 0x0505/0x0506 for entered and cancelled - so a list that is empty means the
     * callback never ran, and a list of unfamiliar numbers means it ran and this code is deaf to it.
     */
    {
        unsigned i;
        unsigned shown = (s_event_count < RC_OSK_EVENT_LOG) ? s_event_count : RC_OSK_EVENT_LOG;

        if (s_event_count == 0u) {
            rc_log("osk:   NO events arrived at all - the callback was never invoked\n");
        } else {
            rc_log("osk:   %u event(s):", s_event_count);
            for (i = 0u; i < shown; i++)
                rc_log(" 0x%04X", s_events[i]);
            rc_log("\n");
        }
    }

    rc_log("osk:   unload -> res %d, len %d\n", (int)s_unload_result.res, (int)s_unload_result.len);

    /*
     * If the read refused but the teardown reported a result, take the teardown's. Recorded as a
     * finding rather than written as the only path, because which one the library intends is exactly
     * what is not known - and a run where both work is as informative as one where only one does.
     */
    if (s_cancelled && s_unload_result.res == OSK_OK && s_unload_result.len > 0) {
        rc_log("osk:   the TEARDOWN carried the text, not the read\n");
        memcpy(s_result, s_unload_text, sizeof(s_result));
        s_cancelled = 0;
    }

    if (!s_done) {
        rc_log("osk:   no completion event in %u ms\n", (unsigned)RC_OSK_TIMEOUT_MS);
        return RC_OSK_TIMED_OUT;
    }
    if (s_cancelled)
        return RC_OSK_CANCELLED;
    status = narrow(s_result, out, out_size) ? RC_OSK_OK : RC_OSK_TOO_LONG;
    return status;
}
