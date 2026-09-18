/* See rc_pair_ps3.h. */
#include "rc_pair_ps3.h"

#include "halyard_pairing_file.h"
#include "rc_account_ps3.h"
#include "halyard_account_id.h"
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
#include <netinet/in.h>
#include <sys/socket.h>
#include <unistd.h>
#include <net/net.h>
#include <net/netctl.h>

static rc_session_state s_state;

/*
 * WHAT A FIRST PAIRING SHOULD ASK FOR ON THIS HARDWARE.
 *
 * ports/common's defaults are 960x540 at 30 fps with software scaling, which is right for the port
 * that set them - a 3DS, whose screen is 400x240 and whose decoder is a different thing entirely. They
 * are wrong here, and b324 showed exactly how: the first machine ever paired by this port streamed at
 * 29 fps through the SPE scaler, because a record written from scratch inherited a handheld's
 * settings.
 *
 * Every number below was measured on this console during bring-up and is recorded in DECODE.md:
 *
 *   1280x720 at 60    the level 4.2 decoder's ceiling; 1080p needs a DPB it cannot be opened with
 *   20,000 kbps       measured good - 64 to 68 slices a picture, where ~128 goes black and 25,000
 *                     saturates the decoder into keyframe storms
 *   RGB from vdec     removes the colour pass entirely
 *   RSX scaling       113 us of a 16,667 us frame, against 3,204 for the SPE scaler
 *   bilinear          free on the RSX, where it cost 21,038 us a frame on the SPEs
 *   the system font   Rodin, read from flash, with the drawn font still behind it
 *
 * Applied only when there was no record to load. A record that exists says what its owner chose, and
 * re-pairing is not the moment to overrule them.
 */
void rc_pair_apply_port_defaults(halyard_pairing_record *record)
{
    record->stream_width = 1280;
    record->stream_height = 720;
    record->fps = 60;
    record->stream_bitrate_kbps = 20000;
    record->decoder_rgb = 1;
    record->hardware_scale = 1;
    record->bilinear_upscale = 1;
    record->system_font = 1;
    rc_log("pair:  first pairing - applying this port's measured defaults (720p60, 20000 kbps)\n");
}

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
 * lives.
 *
 * ASKED OF A SOCKET THAT ROUTES TO THE CONSOLE, rather than of the network stack in general. Opening a
 * UDP socket towards the console and reading back its local address gives the address on the interface
 * that actually reaches it - which is the right answer for a machine with two interfaces, and is the
 * reason this is not simply "what is my IP". No packet is sent: connect on a datagram socket only
 * fixes the peer and picks a route.
 *
 * netCtlGetInfo is the fallback, and it needs netCtlInit first. b322 called it without that and got
 * nothing back, which then reported as "this console does not appear to be on a network" - about the
 * PS3, though it reads like it is about the PS5.
 */
static int local_address(const char *console_host, char *out, size_t out_size)
{
    int sock;
    struct sockaddr_in peer;
    struct sockaddr_in mine;
    socklen_t len = (socklen_t)sizeof(mine);

    out[0] = '\0';

    sock = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (sock >= 0) {
        memset(&peer, 0, sizeof(peer));
        peer.sin_family = AF_INET;
        peer.sin_port = htons(HALYARD_REGIST_PORT);
        if (inet_aton(console_host, &peer.sin_addr) != 0
            && connect(sock, (struct sockaddr *)&peer, sizeof(peer)) == 0
            && getsockname(sock, (struct sockaddr *)&mine, &len) == 0) {
            const char *text = inet_ntoa(mine.sin_addr);

            if (text != NULL && strlen(text) + 1u <= out_size && strcmp(text, "0.0.0.0") != 0)
                snprintf(out, out_size, "%s", text);
        }
        close(sock);
    }
    if (out[0] != '\0') {
        rc_log("pair:  our address on the route to the console is %s\n", out);
        return 1;
    }

    {
        union net_ctl_info info;

        if (netCtlInit() == 0 && netCtlGetInfo(NET_CTL_INFO_IP_ADDRESS, &info) == 0
            && info.ip_address[0] != '\0' && strlen(info.ip_address) + 1u <= out_size) {
            snprintf(out, out_size, "%s", info.ip_address);
            rc_log("pair:  our address from netCtl is %s\n", out);
            return 1;
        }
    }
    rc_log("pair:  could not determine this PS3's own address\n");
    return 0;
}

/*
 * WHERE THE RECORD LIVES, and it has to be the same place rc_connect looks or pairing succeeds and the
 * next launch says it is not paired. The port's own USRDIR, which is writable and travels with the
 * install; rc_connect searches its log-directory list for the same file.
 */
#define RC_PAIR_DIR "/dev_hdd0/game/" RC_PS3_APPID "/USRDIR/"

const char *rc_pair_record_dir(void)
{
    return RC_PAIR_DIR;
}

int rc_pair_run(const char *host, const char *name, const char *console_id)
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
    if (!halyard_pairing_file_load(RC_PAIR_DIR, &record))
        rc_pair_apply_port_defaults(&record);

    show(RC_PHASE_PAIRING, "Pairing", "Enter the console's address", NULL);
    if (!ask(RC_OSK_TEXT, "Console IP address",
             (host != NULL) ? host : (record.host[0] != '\0' ? record.host : NULL),
             params.host, sizeof(params.host), "the console's address is needed"))
        return 0;

    /*
     * PRE-FILLED, AND THE PS3 ITSELF IS ASKED FIRST WHEN THERE IS NOTHING REMEMBERED.
     *
     * A PSN account id is a nineteen-digit number entered with a controller, and it was the worst step
     * in the only flow a new user has to get through. This console is signed in to that very account
     * and keeps the number in /dev_hdd0/home/<user>/np_cache.dat - see rc_account_ps3.h for how that
     * was established, which was by searching for a number this port already had rather than by
     * guessing at a file format.
     *
     * THE RECORD STILL WINS. A value somebody has already paired with beats one derived from the
     * console, because it is confirmed and this is not.
     *
     * AND IT IS STILL SHOWN. The keyboard comes up with the number in it rather than being skipped, so
     * a wrong one is a wrong number on a screen instead of a registration the console refuses for
     * reasons it does not explain. One button instead of nineteen digits is the whole win; skipping the
     * step as well would trade a falsifiable flow for an unfalsifiable one.
     */
    {
        const char *prefill = (record.account_id[0] != '\0') ? record.account_id : NULL;
        char from_console[HALYARD_ACCOUNT_ID_TEXT_MAX];
        char typed[64];
        const char *hint = NULL;
        halyard_account_id_status parsed;

        if (prefill == NULL) {
            rc_account_status found = rc_account_read(from_console, sizeof(from_console));

            if (found == RC_ACCOUNT_OK) {
                prefill = from_console;
                hint = "This PS3's own account id is filled in - check it and press Start";
                rc_log("pair:  the account id came from this PS3 - check it before continuing\n");
            } else if (found == RC_ACCOUNT_NO_NP) {
                /*
                 * THE ONE FAILURE WITH AN EASY WAY OUT, and it is worth more than any other message on
                 * this screen. A person who does not know their account id and has no way to find it is
                 * stopped here for good - but this console will hand it over the moment it is signed in,
                 * and signing a PS3 in to PSN is something its owner already knows how to do. Saying so
                 * turns a dead end into a two-minute errand.
                 */
                hint = "Sign in to PSN on this PS3 and Ripcord will fill this in by itself";
            } else {
                hint = "Ripcord could not read it from this PS3 - enter it yourself";
            }
            rc_log("pair:  account id from the console: %s\n", rc_account_status_text(found));
        }

        show(RC_PHASE_PAIRING, "Pairing", "Enter your PSN account id", hint);
        if (!ask(RC_OSK_DIGITS_FIRST, "PSN account id", prefill, typed, sizeof(typed),
                 "the account id is needed"))
            return 0;

        /*
         * READ BEFORE IT TRAVELS. An account id is a 64-bit number and guides quote it in three bases;
         * halyard_regist_message encodes an all-decimal one as a number and anything else as its own
         * characters, so hex typed into this box goes out as the text "1a2b..." and comes back as a
         * refusal the console does not explain. See halyard_account_id.h.
         */
        parsed = halyard_account_id_normalise(typed, params.account_id, sizeof(params.account_id));
        if (parsed != HALYARD_ACCOUNT_ID_OK) {
            rc_log("pair:  the account id was not usable - %s\n",
                   halyard_account_id_status_text(parsed));
            show(RC_PHASE_FAILED, "That account id cannot be used",
                 halyard_account_id_status_text(parsed),
                 "Start pairing again and enter it as a plain number");
            return 0;
        }
    }

    show(RC_PHASE_PAIRING, "Pairing",
         "Enter the PIN from the console", "Settings, System, Remote Play, Link Device");
    if (!ask(RC_OSK_NUMBERS, "PIN from the console", NULL, pin_text, sizeof(pin_text),
             "the PIN is needed"))
        return 0;
    params.passcode = (uint32_t)strtoul(pin_text, NULL, 10);

    params.is_ps5 = 1;
    if (!local_address(params.host, params.client_ip, sizeof(params.client_ip))) {
        /*
         * Named as the PS3, because "this console" reads as the PS5 to anyone standing between the
         * two - which is exactly who sees this.
         */
        show(RC_PHASE_FAILED, "This PS3 has no network address",
             "It could not find its own address on the network",
             "Check the PS3's network settings, not the PlayStation 5's");
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
        /*
         * THE TWO FACTS THE SENTENCE ABOVE LEAVES OUT, and the first attempt at a PS4 needed both.
         *
         * "The console refused the registration" is HALYARD_REGIST_ERR_REFUSED, which means a non-2xx
         * status - and the status is the whole diagnosis: a 404 says the PATH was wrong, which is a
         * different fault from a 403 saying the key was. The probe matters just as much;
         * halyard_regist_message.h records that a console refuses a registration that was not preceded
         * by a matching search probe, so a probe that went unanswered explains the refusal by itself.
         *
         * Neither is worth a line on the television - they are for whoever reads the log afterwards -
         * but leaving them out of the log too meant a refusal that had four possible causes and no way
         * to tell them apart.
         */
        rc_log("       HTTP status %d; the search probe was %s\n",
               result.http_status,
               result.saw_search_reply ? "answered" : "NOT answered - the console ignores a"
                                                      " registration that did not follow one");
        show(RC_PHASE_FAILED, "Pairing failed", detail,
             result.status == HALYARD_REGIST_ERR_BAD_RECORD
                 ? "Check the PIN and that it has not expired, then try again"
                 : "Check the address and that the console is showing its PIN screen");
        return 0;
    }

    /* The record is the product of all of this, and the only place it exists. */
    snprintf(record.host, sizeof(record.host), "%s", params.host);
    /* What discovery called it, if discovery is how we got here. Never on the wire - see the header. */
    if (name != NULL && name[0] != '\0')
        snprintf(record.name, sizeof(record.name), "%s", name);
    else
        record.name[0] = '\0';
    /* The one stable thing about a console. See the header: without it a moved console has to be
     * paired again, with it the record can be re-pointed at wherever it turns up. */
    if (console_id != NULL && console_id[0] != '\0')
        snprintf(record.console_id, sizeof(record.console_id), "%s", console_id);
    else
        record.console_id[0] = '\0';
    /* Remembered so the next pairing does not ask for it again. */
    snprintf(record.account_id, sizeof(record.account_id), "%s", params.account_id);
    record.is_ps5 = result.record.is_ps5;
    memcpy(record.registkey, result.record.registration_key, result.record.registration_key_length);
    record.registkey_length = result.record.registration_key_length;
    memcpy(record.companion, result.record.companion, sizeof(record.companion));

    /*
     * SAVED WITHOUT LOSING THE OTHERS. halyard_pairing_file_save reads the file, adds or replaces the
     * entry at this address, selects it and writes the whole set back - so pairing a second console
     * keeps the first. The version that wrote this record straight out destroyed the first console's
     * keys and said nothing about it.
     */
    if (!halyard_pairing_file_save(RC_PAIR_DIR, &record)) {
        show(RC_PHASE_FAILED, "Paired, but could not save it",
             "The console accepted the pairing and the record could not be written",
             "Check there is space, and that no more than eight consoles are already paired");
        return 0;
    }

    rc_log("pair:  paired with %s\n", params.host);
    show(RC_PHASE_ENDED, "Paired", "This console is now linked", NULL);
    return 1;
}
