/* See rc_pair_ps3.h. */
#include "rc_pair_ps3.h"

#include "halyard_pairing_file.h"
#include "halyard_regist_flow.h"
#include "rc_log.h"
#include "rc_osk_ps3.h"
#include "rc_platform.h"
#include "rc_session_state.h"
#include "rc_status_screen.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <arpa/inet.h>
#include <net/net.h>
#include <net/netctl.h>

static rc_session_state s_state;

/* Drawn behind the keyboard, once a frame, so the dialog has something to composite over and the
 * viewer can still see which question is being asked. See rc_osk_set_present_hook. */
static void present_behind_keyboard(void)
{
    rc_status_screen_draw(&s_state);
}

static void show(rc_phase phase, const char *headline, const char *detail, const char *hint)
{
    rc_session_set(&s_state, phase, headline, detail, hint);
    if (rc_status_screen_wants_draw(&s_state))
        rc_status_screen_draw(&s_state);
}

/*
 * Asks one question, and turns the ways it can go wrong into something on screen.
 *
 * Cancelling is NOT a failure and does not get an error card - somebody who backed out of a keyboard
 * knows why they did. It simply ends the flow.
 */
static int ask(rc_osk_kind kind, const char *prompt, const char *initial, char *out, size_t out_size,
               const char *what)
{
    rc_osk_status status = rc_osk_ask(kind, prompt, initial, out, out_size);

    if (status == RC_OSK_OK && out[0] != '\0')
        return 1;
    if (status == RC_OSK_CANCELLED)
        show(RC_PHASE_IDLE, "Pairing cancelled", NULL, NULL);
    else if (status == RC_OSK_OK)
        show(RC_PHASE_FAILED, "Nothing was entered", what, "Try again and fill the field in");
    else
        show(RC_PHASE_FAILED, "Could not ask for that", rc_osk_status_text(status), NULL);
    return 0;
}

/*
 * OUR OWN LAN ADDRESS, which the registration request has to carry in its HOST header.
 *
 * Not cosmetic and not the console's: the request tells the console where the client speaking to it
 * lives. Read from the network stack rather than assumed, because a console with two interfaces, or a
 * machine that moved networks since boot, would otherwise send an address that is no longer its own.
 */
static int local_address(char *out, size_t out_size)
{
    union net_ctl_info info;

    if (netCtlGetInfo(NET_CTL_INFO_IP_ADDRESS, &info) != 0)
        return 0;
    if (strlen(info.ip_address) + 1u > out_size)
        return 0;
    strcpy(out, info.ip_address);
    return out[0] != '\0';
}

/*
 * WHERE THE RECORD LIVES, and it has to be the same place rc_connect looks or pairing succeeds and the
 * next launch says it is not paired. The port's own USRDIR, which is writable and travels with the
 * install; rc_connect searches its log-directory list for the same file.
 */
#define RC_PAIR_DIR "/dev_hdd0/game/" RC_PS3_APPID "/USRDIR/"

int rc_pair_run(const char *host)
{
    halyard_pairing_record record;
    halyard_regist_params params;
    halyard_regist_result result;
    char pin_text[16];

    rc_session_state_reset(&s_state);
    rc_osk_set_present_hook(present_behind_keyboard);
    memset(&params, 0, sizeof(params));

    /*
     * LOADED FIRST, so settings somebody chose survive a re-pairing. A record that was never loaded
     * saves defaults, which is right for a first pairing and wrong for every later one.
     */
    (void)halyard_pairing_file_load(RC_PAIR_DIR, &record);

    show(RC_PHASE_PAIRING, "Pairing", "Enter the console's address", NULL);
    if (!ask(RC_OSK_TEXT, "Console IP address", host, params.host, sizeof(params.host),
             "the console's address is needed"))
        return 0;

    show(RC_PHASE_PAIRING, "Pairing", "Enter your PSN account id", NULL);
    if (!ask(RC_OSK_TEXT, "PSN account id", NULL,
             params.account_id, sizeof(params.account_id), "the account id is needed"))
        return 0;

    show(RC_PHASE_PAIRING, "Pairing",
         "Enter the PIN from the console", "Settings, System, Remote Play, Link Device");
    if (!ask(RC_OSK_NUMBERS, "PIN from the console", NULL, pin_text, sizeof(pin_text),
             "the PIN is needed"))
        return 0;
    params.passcode = (uint32_t)strtoul(pin_text, NULL, 10);

    params.is_ps5 = 1;
    if (!local_address(params.client_ip, sizeof(params.client_ip))) {
        show(RC_PHASE_FAILED, "No network address",
             "This console does not appear to be on a network",
             "Check its network settings and try again");
        return 0;
    }

    show(RC_PHASE_PAIRING, "Pairing", "Talking to the console", NULL);
    rc_log("pair:  registering with %s\n", params.host);

    if (!halyard_regist_run(&params, &result)) {
        char detail[96];

        /*
         * The console's own application reason is carried through when it gave one. It is a hex code
         * and means nothing to a viewer, but it is the difference between a bug report that can be
         * acted on and one that says "it didn't work".
         */
        if (result.console_reason[0] != '\0')
            snprintf(detail, sizeof(detail), "%s (console said %s)",
                     halyard_regist_status_text(result.status), result.console_reason);
        else
            snprintf(detail, sizeof(detail), "%s", halyard_regist_status_text(result.status));

        rc_log("pair:  failed - %s\n", detail);
        show(RC_PHASE_FAILED, "Pairing failed", detail,
             result.status == HALYARD_REGIST_ERR_BAD_RECORD
                 ? "Check the PIN and that it has not expired, then try again"
                 : "Check the address and that the console is showing its PIN screen");
        return 0;
    }

    /* The record is the product of all of this, and the only place it exists. */
    snprintf(record.host, sizeof(record.host), "%s", params.host);
    record.is_ps5 = result.record.is_ps5;
    memcpy(record.registkey, result.record.registration_key, result.record.registration_key_length);
    record.registkey_length = result.record.registration_key_length;
    memcpy(record.companion, result.record.companion, sizeof(record.companion));

    if (!halyard_pairing_file_save(RC_PAIR_DIR, &record)) {
        show(RC_PHASE_FAILED, "Paired, but could not save it",
             "The console accepted the pairing and the record could not be written",
             "Check there is space and the install is not read-only");
        return 0;
    }

    rc_log("pair:  paired with %s\n", params.host);
    show(RC_PHASE_ENDED, "Paired", "This console is now linked", NULL);
    return 1;
}
