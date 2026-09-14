/* See rc_connect.h. Nothing in this file logs or returns anything from the pairing record. */
#include "rc_connect.h"

#include "halyard_discovery.h"
#include "halyard_wake.h"
#include "halyard_pairing_file.h"
#include "halyard_control_session.h"
#include "halyard_control_arm.h"
#include "halyard_ctrl_message.h"
#include "platform/rc_platform.h"

#include <fcntl.h>
#include <string.h>
#include <unistd.h>
#include <sys/select.h>
#include <sys/time.h>

#include <net/net.h>
#include <net/socket.h>
#include <netinet/in.h>
#include <arpa/inet.h>

/*
 * A DIRECTORY, NOT A FILENAME, and the distinction is the whole point of this comment.
 *
 * halyard_pairing_file_load takes a path, runs rc_program_dir over it to get the directory part, and
 * then appends the fixed name "pairing.txt" - the same shape rc_log_open has, because on the 3DS both
 * are handed argv[0] and asked to find something beside the executable. A path ending in '/' names
 * itself as its own directory, which is what makes this work.
 *
 * An earlier revision defined this as "/dev_hdd0/ripcord-pairing.txt" and called it a PATH. The loader
 * would have taken the directory part and looked for /dev_hdd0/pairing.txt, found nothing, and the
 * probe would have reported "no pairing record - skipping" with the record sitting on the console. The
 * constant's own name was the misleading part; it is a directory and now says so.
 */
#ifndef RC_CONNECT_PAIRING_DIR
#define RC_CONNECT_PAIRING_DIR "/dev_hdd0/"
#endif

/* sin_len is set because PSL1GHT's samples do. It is NOT required - rc_discover.c asked the console
 * directly and a zeroed field was accepted - so this is consistency, not necessity. */
static void fill_addr(struct sockaddr_in *a, const char *ip, unsigned short port)
{
    memset(a, 0, sizeof(*a));
    a->sin_len = (uint8_t)sizeof(*a);
    a->sin_family = AF_INET;
    a->sin_port = htons(port);
    if (ip == NULL)
        a->sin_addr.s_addr = INADDR_ANY;
    else
        (void)inet_pton(AF_INET, ip, &a->sin_addr);
}

/*
 * One SRCH to a known address, unicast. Discovery broadcasts to find anything; here the address is
 * already known from the record, so asking it directly avoids depending on broadcast reaching it and
 * avoids waking the question of which console replied.
 */
/*
 * Broadcast SRCH, used only to tell two failures apart: a console that is off, and a console that has
 * moved since the record was written. A DHCP lease outliving a pairing record is ordinary - this
 * project's own PS3 changed address twice in a week - and the symptom is identical to an absent console
 * unless something asks the wider question.
 *
 * `found_addr` receives the address that answered, so the caller can compare it with the record's
 * WITHOUT either of them being logged.
 */
static int broadcast_find(char *found_addr, size_t addr_size, unsigned timeout_ms)
{
    const halyard_discovery_profile *profile = &halyard_discovery_profile_ps5;
    char probe[128];
    size_t probe_len;
    struct sockaddr_in local, bcast;
    int sock, on = 1, got = 0;
    uint64_t deadline;

    probe_len = halyard_discovery_build_probe(profile, probe, sizeof(probe));
    if (probe_len == 0u)
        return 0;

    sock = socket(PF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (sock < 0)
        return 0;
    if (setsockopt(sock, SOL_SOCKET, SO_BROADCAST, &on, (socklen_t)sizeof(on)) < 0) {
        (void)close(sock);
        return 0;
    }

    fill_addr(&local, NULL, 0);
    (void)bind(sock, (struct sockaddr *)&local, (socklen_t)sizeof(local));

    fill_addr(&bcast, "255.255.255.255", profile->port);
    if (sendto(sock, probe, probe_len, 0, (struct sockaddr *)&bcast, (socklen_t)sizeof(bcast)) < 0) {
        (void)close(sock);
        return 0;
    }

    deadline = rc_time_ms() + (uint64_t)timeout_ms;
    while (rc_time_ms() < deadline) {
        char buf[1024];
        struct sockaddr_in from;
        socklen_t from_len = (socklen_t)sizeof(from);
        halyard_discovered_console found;
        ssize_t n;

        memset(&from, 0, sizeof(from));
        n = recvfrom(sock, buf, sizeof(buf) - 1u, MSG_DONTWAIT, (struct sockaddr *)&from, &from_len);
        if (n <= 0) {
            rc_sleep_ms(20u);
            continue;
        }
        buf[n] = '\0';
        if (halyard_discovery_parse_response(buf, (size_t)n, NULL, &found)) {
            if (inet_ntop(AF_INET, &from.sin_addr, found_addr, (socklen_t)addr_size) != NULL)
                got = 1;
            break;
        }
    }

    (void)close(sock);
    return got;
}

static int probe_once(const char *host, unsigned short src_port, int *is_awake, unsigned timeout_ms)
{
    const halyard_discovery_profile *profile = &halyard_discovery_profile_ps5;
    char probe[128];
    size_t probe_len;
    struct sockaddr_in local, peer;
    int sock;
    uint64_t deadline;
    int got = 0;

    probe_len = halyard_discovery_build_probe(profile, probe, sizeof(probe));
    if (probe_len == 0u)
        return 0;

    sock = socket(PF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (sock < 0)
        return 0;

    /* The spec names a source port for the post-wake poll. Bind it where possible and fall back to
     * ephemeral rather than fail - halyard_discovery.h: "a wake from the wrong port is more likely to
     * work than no wake at all", and the same reasoning applies to the poll that follows it. */
    fill_addr(&local, NULL, src_port);
    if (bind(sock, (struct sockaddr *)&local, (socklen_t)sizeof(local)) < 0) {
        fill_addr(&local, NULL, 0);
        (void)bind(sock, (struct sockaddr *)&local, (socklen_t)sizeof(local));
    }

    fill_addr(&peer, host, profile->port);
    if (sendto(sock, probe, probe_len, 0, (struct sockaddr *)&peer, (socklen_t)sizeof(peer)) < 0) {
        (void)close(sock);
        return 0;
    }

    deadline = rc_time_ms() + (uint64_t)timeout_ms;
    while (rc_time_ms() < deadline) {
        char buf[1024];
        halyard_discovered_console found;
        ssize_t n = recvfrom(sock, buf, sizeof(buf) - 1u, MSG_DONTWAIT, NULL, NULL);

        if (n <= 0) {
            rc_sleep_ms(20u);
            continue;
        }
        buf[n] = '\0';
        if (halyard_discovery_parse_response(buf, (size_t)n, NULL, &found)) {
            *is_awake = found.is_awake;
            got = 1;
            break;
        }
    }

    (void)close(sock);
    return got;
}

static int send_wakeup(const halyard_pairing_record *rec, rc_connect_result *out)
{
    const halyard_discovery_profile *profile = &halyard_discovery_profile_ps5;
    char credential[HALYARD_WAKE_CREDENTIAL_MAX];
    char payload[256];
    size_t payload_len;
    struct sockaddr_in local, peer;
    int sock;
    int sent;

    if (!halyard_wake_credential(rec->registkey, rec->registkey_length,
                                 credential, sizeof(credential)))
        return 0;
    out->credential_ok = 1;

    payload_len = halyard_wake_build_payload(profile, credential, payload, sizeof(payload));
    if (payload_len == 0u)
        return 0;

    sock = socket(PF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (sock < 0)
        return 0;

    /* A sleeping console may only honour a wake that originates on the vendor's own source port. */
    fill_addr(&local, NULL, profile->wake_source_port);
    if (bind(sock, (struct sockaddr *)&local, (socklen_t)sizeof(local)) == 0) {
        out->wake_source_bound = 1;
    } else {
        fill_addr(&local, NULL, 0);
        (void)bind(sock, (struct sockaddr *)&local, (socklen_t)sizeof(local));
    }

    fill_addr(&peer, rec->host, profile->port);
    sent = (sendto(sock, payload, payload_len, 0,
                   (struct sockaddr *)&peer, (socklen_t)sizeof(peer)) >= 0);

    /* Nothing ever acknowledges a WAKEUP. Closing immediately is correct; waiting here would wait
     * forever, which halyard_wake.h warns about explicitly. */
    (void)close(sock);
    return sent;
}

/*
 * A TCP CONNECT WITH A DEADLINE, which ports/common's does not have.
 *
 * rc_tcp_connect calls connect() on a BLOCKING socket and only sets O_NONBLOCK afterwards, so a console
 * that does not accept on the control port leaves halyard_control_session_open stuck inside it with no
 * way out. That is what locked the console on b31. The core is otherwise careful - it polls everywhere
 * and uses timed windows - and this one call predates that discipline.
 *
 * Rather than change a file every port shares on the strength of one hardware run, this pre-flights the
 * same port from the port layer: non-blocking connect, select with a deadline, then close. If it does
 * not accept, open() is never entered and the probe reports why instead of hanging. Fixing rc_tcp.c
 * itself is the better long-term answer and belongs with evidence from more than one platform.
 */
static int tcp_port_accepts(const char *host, unsigned short port, unsigned timeout_ms)
{
    struct sockaddr_in addr;
    struct timeval tv;
    fd_set wr;
    int sock;
    int ok = 0;

    sock = socket(PF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (sock < 0)
        return 0;

    fill_addr(&addr, host, port);
    (void)fcntl(sock, F_SETFL, O_NONBLOCK);

    if (connect(sock, (struct sockaddr *)&addr, (socklen_t)sizeof(addr)) == 0) {
        ok = 1;
    } else {
        FD_ZERO(&wr);
        /* FD_SET's macros convert the descriptor through the fd_set mask type, which this port's
         * -Wconversion objects to inside the SDK's own header. The conversion is theirs. */
#pragma GCC diagnostic push
#pragma GCC diagnostic ignored "-Wsign-conversion"
#pragma GCC diagnostic ignored "-Wconversion"
        FD_SET(sock, &wr);
#pragma GCC diagnostic pop
        tv.tv_sec = (long)(timeout_ms / 1000u);
        tv.tv_usec = (long)((timeout_ms % 1000u) * 1000u);

        if (select(sock + 1, NULL, &wr, NULL, &tv) > 0) {
            int err = 0;
            socklen_t len = (socklen_t)sizeof(err);
            if (getsockopt(sock, SOL_SOCKET, SO_ERROR, &err, &len) == 0 && err == 0)
                ok = 1;
        }
    }

    (void)close(sock);
    return ok;
}

const char *rc_connect_stage_name(rc_connect_stage stage)
{
    switch (stage) {
    case RC_CONNECT_NO_RECORD:     return "no pairing record - skipped";
    case RC_CONNECT_BAD_RECORD:    return "the pairing record is unusable";
    case RC_CONNECT_NO_CONSOLE:    return "no console answered at the recorded address";
    case RC_CONNECT_WAKE_SENT:     return "WAKEUP sent, but it did not wake in time";
    case RC_CONNECT_AWAKE:         return "awake";
    case RC_CONNECT_SESSION_OPEN:  return "control session open";
    case RC_CONNECT_SESSION_READY: return "SESSION_ID seen - willing to stream";
    default:                       return "unknown";
    }
}

#define SAY(text) do { if (log != NULL) log(text); } while (0)

rc_connect_stage rc_connect(unsigned wake_timeout_ms, rc_connect_log_fn log,
                            const char *const *dirs, int dir_count, rc_connect_result *out)
{
    halyard_pairing_record rec;
    halyard_control_session session;
    int awake = 0;
    uint64_t started;

    memset(out, 0, sizeof(*out));
    memset(&rec, 0, sizeof(rec));

    SAY("loading the pairing record");

    /*
     * TRY THE SAME DIRECTORIES THE LOG TRIES, in the same order, rather than the single hard-coded one
     * this used to read.
     *
     * b40 failed here with the record sitting on the console: the log resolves a writable directory by
     * trying four and picking the first that works - which is the install directory - while this looked
     * only in /dev_hdd0/. Two different answers to "where does this program keep its files" in one
     * program, and the person putting the record on the console has no way to know which one applies.
     * Worse, the advice given at the time named the log's directory, so following it moved the record
     * out of the only place this would look.
     *
     * The caller passes the list so it cannot drift from the log's. A NULL list falls back to the
     * compile-time constant, which is what a caller with no opinion gets.
     */
    {
        int loaded = 0;
        int i;

        if (dirs == NULL || dir_count <= 0) {
            loaded = halyard_pairing_file_load(RC_CONNECT_PAIRING_DIR, &rec);
        } else {
            for (i = 0; i < dir_count && !loaded; i++) {
                /* The loader appends "pairing.txt" to the directory part of what it is given. */
                loaded = halyard_pairing_file_load(dirs[i], &rec);
                if (loaded)
                    out->record_dir = dirs[i];
            }
        }

        if (!loaded) {
            out->stage = RC_CONNECT_NO_RECORD;
            return out->stage;
        }
    }
    out->had_record = 1;

    if (rec.registkey_length == 0u) {
        out->stage = RC_CONNECT_BAD_RECORD;
        return out->stage;
    }

    /*
     * THE inet_aton QUESTION, asked before anything can hang on the answer. See rc_connect.h: the core
     * parses every address with inet_aton and this port never has, and the consequence of a wrong
     * result is not an error but a blocking connect to nowhere.
     */
    {
        struct in_addr by_pton, by_aton;

        memset(&by_pton, 0, sizeof(by_pton));
        memset(&by_aton, 0, sizeof(by_aton));

        out->pton_ok = (inet_pton(AF_INET, rec.host, &by_pton) == 1);
        out->aton_ok = (inet_aton(rec.host, &by_aton) != 0);
        out->aton_matches_pton =
            (out->pton_ok && out->aton_ok &&
             memcmp(&by_pton, &by_aton, sizeof(by_pton)) == 0);

        out->host_parsed = out->pton_ok;
    }

    /*
     * DOES fcntl ACTUALLY SET A PS3 SOCKET NON-BLOCKING? Tested with calls that cannot block, because
     * the way to find out by doing I/O is to hang the console, which this has now done twice.
     *
     * fcntl is asked to set O_NONBLOCK and then asked to read the flags back. If sockets are not newlib
     * file descriptors here - which is what SO_NBIO existing suggests - the readback will not carry the
     * flag, and every poll loop in ports/common that relies on it has been running against a blocking
     * socket.
     */
    {
        int probe = socket(PF_INET, SOCK_DGRAM, IPPROTO_UDP);
        if (probe >= 0) {
            int set_rc = fcntl(probe, F_SETFL, O_NONBLOCK);
            int flags = fcntl(probe, F_GETFL, 0);
            int on = 1;
            int nbio_rc = setsockopt(probe, SOL_SOCKET, SO_NBIO, &on, (socklen_t)sizeof(on));

            out->fcntl_set_rc = set_rc;
            out->fcntl_readback_nonblock = (flags != -1) && ((flags & O_NONBLOCK) != 0);
            out->so_nbio_ok = (nbio_rc == 0);
            (void)close(probe);
        }
    }

    SAY(out->aton_matches_pton
        ? "inet_aton agrees with inet_pton"
        : "inet_aton DISAGREES with inet_pton - the core parses addresses with inet_aton");

    SAY("unicast SRCH to the recorded address");

    if (probe_once(rec.host, halyard_discovery_profile_ps5.wake_search_source_port, &awake, 1500u)) {
        out->unicast_replied = 1;
    } else {
        /*
         * The recorded address said nothing. Ask the network at large before concluding the console is
         * absent: if something answers a broadcast and it is NOT the recorded address, the record is
         * stale rather than the console missing, and those need completely different fixes.
         */
        char seen[HALYARD_DISCOVERY_ADDRESS_MAX];

        if (broadcast_find(seen, sizeof(seen), 2500u)) {
            out->broadcast_found = 1;
            out->broadcast_matches = (strcmp(seen, rec.host) == 0);
        }
        out->stage = RC_CONNECT_NO_CONSOLE;
        return out->stage;
    }

    /* Only meaningful once something actually answered. */
    out->was_asleep = !awake;

    if (!awake) {
        started = rc_time_ms();

        SAY("sending WAKEUP");

        if (send_wakeup(&rec, out))
            out->wakeups_sent++;

        out->stage = RC_CONNECT_WAKE_SENT;

        /* Poll SRCH until is_awake flips. Readiness is observed, never acknowledged. */
        while (rc_time_ms() - started < (uint64_t)wake_timeout_ms) {
            rc_sleep_ms(500u);
            out->polls++;
            if (probe_once(rec.host, halyard_discovery_profile_ps5.wake_search_source_port,
                           &awake, 800u) && awake) {
                out->woke_after_ms = (unsigned)(rc_time_ms() - started);
                break;
            }
        }

        if (!awake)
            return out->stage;
    }

    out->stage = RC_CONNECT_AWAKE;

    /*
     * PRE-FLIGHT BEFORE THE BLOCKING CALL. See tcp_port_accepts: the core's connect has no deadline, so
     * entering open() against a port that will not accept is what hung b31.
     */
    SAY("pre-flighting the control port with a bounded TCP connect");

    if (!tcp_port_accepts(rec.host, HALYARD_CONTROL_ARM_PORT, 4000u)) {
        SAY("the control port did not accept - not entering the session open, which cannot time out");
        return out->stage;
    }
    out->tcp_preflight_ok = 1;

    /*
     * REFUSE TO ENTER open() IF THE CORE WOULD PARSE THE ADDRESS DIFFERENTLY. rc_tcp_connect's connect()
     * is blocking and unbounded, so a disagreement here is the difference between a report and a locked
     * console. Better to stop with a finding than to reproduce the hang for a third time.
     */
    if (!out->aton_matches_pton) {
        SAY("refusing to open the session: inet_aton would hand the core a different address,");
        SAY("and its connect() is blocking with no timeout. This is the hang, not a symptom of it.");
        return out->stage;
    }

    /*
     * The control plane: ARM, /sess/init, /sess/ctrl, then the persistent binary channel. All of it is
     * ports/common's, proven against a real PS5 from the 3DS - this contributes nothing but the call.
     */
    SAY("opening the control session (ARM, /sess/init, /sess/ctrl)");

    memset(&session, 0, sizeof(session));
    if (!halyard_control_session_open(&rec, &session))
        return out->stage;

    out->stage = RC_CONNECT_SESSION_OPEN;
    SAY("session open - waiting for SESSION_ID");

    {
        /*
         * Twenty seconds, not eight. b36 watched this console take 12.3 s merely to answer a SRCH after
         * waking, so a window that would have been generous for an already-awake console is not
         * necessarily generous here - and reporting "not willing to stream" about one that is still
         * waking up is a wrong answer rather than a slow one.
         */
        uint64_t deadline = rc_time_ms() + 20000u;

        while (rc_time_ms() < deadline) {
            halyard_control_event ev;
            memset(&ev, 0, sizeof(ev));

            if (!halyard_control_session_service(&session, &ev)) {
                out->session_error = (int)ev.kind;
                break;
            }

            if (ev.kind == HALYARD_CONTROL_EVENT_MESSAGE) {
                out->frames_seen++;
                if (out->first_type == 0u)
                    out->first_type = ev.type;
                out->last_type = ev.type;

                if (ev.type == HALYARD_CTRL_TYPE_HEARTBEAT_REQ)
                    out->heartbeats++;

                /*
                 * THE SIGN-IN GATE, and the reference implementation ANSWERS IT rather than giving up.
                 *
                 * src/Ripcord.Protocol.Halyard/Session/HalyardStreamingSession.cs is the source of truth
                 * here, and on TypeLoginPrompt it says "this user is locked, send the passcode" and
                 * completes a gate that another task is waiting on. ports/ripcord-3ds treats the same
                 * message as fatal - "this build cannot answer one" - which is that port's limitation
                 * rather than the protocol's, and copying it here would have carried a restriction the
                 * reference does not have.
                 *
                 * b39 confirmed this is the gate on hardware: one control frame, type 0x0004, no
                 * heartbeats, no SESSION_ID. So the probe now answers it when a passcode was supplied
                 * out of band, and still merely reports it when none was.
                 */
                if (ev.type == HALYARD_CTRL_TYPE_LOGIN_PROMPT) {
                    out->login_prompt = 1;

                    if (rec.login_pin[0] == '\0') {
                        /* Nothing to answer with - report the gate rather than sit out the deadline. */
                        break;
                    }

                    /*
                     * ONE attempt, where the reference allows five. Its retries exist because a person is
                     * typing and can correct a typo; this passcode came from a file, so re-sending the same
                     * digits would only burn a counter and ask the console the same question twice.
                     *
                     * The counter discipline still matters and lives in ports/common: the submit takes the
                     * next unused value (5 on a fresh session, matching the reference's "first attempt is
                     * 5") and advances it in the same breath, so no IV is ever reused under the session key.
                     */
                    if (!out->login_submitted) {
                        out->login_submitted =
                            halyard_control_session_submit_login(&session, rec.login_pin,
                                                                 strlen(rec.login_pin));
                        SAY(out->login_submitted ? "passcode submitted - waiting for SESSION_ID"
                                                 : "the passcode could not be encoded - not sent");
                        if (!out->login_submitted)
                            break;
                    }

                    /*
                     * Keep waiting on the SAME deadline rather than extending it. The reference measures
                     * the console's session-ready at ~2.3 s after a LAN submit, and the twenty seconds this
                     * loop already had was sized for a console still waking up - so there is room, and
                     * granting more would only slow down the "it never answered" case.
                     *
                     * What this port CANNOT do yet is read the console's verdict. That arrives as
                     * TypeLogin (0x0005) carrying one byte - 0x00 accepted, 0x01 rejected - and decrypting
                     * it needs the console's own receive-direction counter, which ports/common does not
                     * track yet (it has the send counter only). So a wrong passcode here looks exactly like
                     * silence, and the log must not claim otherwise.
                     */
                    continue;
                }
            }

            if (ev.kind == HALYARD_CONTROL_EVENT_SESSION_READY) {
                out->stage = RC_CONNECT_SESSION_READY;
                break;
            }
            rc_sleep_ms(10u);
        }
    }

    halyard_control_session_close(&session);
    return out->stage;
}
