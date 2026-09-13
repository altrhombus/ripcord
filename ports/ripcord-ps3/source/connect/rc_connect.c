/* See rc_connect.h. Nothing in this file logs or returns anything from the pairing record. */
#include "rc_connect.h"

#include "halyard_discovery.h"
#include "halyard_wake.h"
#include "halyard_pairing_file.h"
#include "halyard_control_session.h"
#include "platform/rc_platform.h"

#include <string.h>
#include <unistd.h>

#include <net/net.h>
#include <net/socket.h>
#include <netinet/in.h>
#include <arpa/inet.h>

#ifndef RC_CONNECT_PAIRING_PATH
#define RC_CONNECT_PAIRING_PATH "/dev_hdd0/ripcord-pairing.txt"
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

rc_connect_stage rc_connect(unsigned wake_timeout_ms, rc_connect_result *out)
{
    halyard_pairing_record rec;
    halyard_control_session session;
    int awake = 0;
    uint64_t started;

    memset(out, 0, sizeof(*out));
    memset(&rec, 0, sizeof(rec));

    /* The loader takes a path to something BESIDE the record, and derives the directory. Passing the
     * record's own path works because the directory part is what it uses. */
    if (!halyard_pairing_file_load(RC_CONNECT_PAIRING_PATH, &rec)) {
        out->stage = RC_CONNECT_NO_RECORD;
        return out->stage;
    }
    out->had_record = 1;

    if (rec.registkey_length == 0u) {
        out->stage = RC_CONNECT_BAD_RECORD;
        return out->stage;
    }

    if (!probe_once(rec.host, halyard_discovery_profile_ps5.wake_search_source_port,
                    &awake, 1500u)) {
        out->stage = RC_CONNECT_NO_CONSOLE;
        return out->stage;
    }

    out->was_asleep = !awake;

    if (!awake) {
        started = rc_time_ms();

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
     * The control plane: ARM, /sess/init, /sess/ctrl, then the persistent binary channel. All of it is
     * ports/common's, proven against a real PS5 from the 3DS - this contributes nothing but the call.
     */
    memset(&session, 0, sizeof(session));
    if (!halyard_control_session_open(&rec, &session))
        return out->stage;

    out->stage = RC_CONNECT_SESSION_OPEN;

    {
        uint64_t deadline = rc_time_ms() + 8000u;
        while (rc_time_ms() < deadline) {
            halyard_control_event ev;
            memset(&ev, 0, sizeof(ev));
            if (!halyard_control_session_service(&session, &ev)) {
                out->session_error = (int)ev.kind;
                break;
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
