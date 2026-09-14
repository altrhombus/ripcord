/* See rc_connect.h. Nothing in this file logs or returns anything from the pairing record. */
#include "rc_connect.h"

#include "halyard_discovery.h"
#include "halyard_wake.h"
#include "halyard_pairing_file.h"
#include "halyard_control_session.h"
#include "takion_reliable_channel.h"
#include "rc_udp.h"
#include "rc_ecdh.h"
#include "rc_stack_ps3.h"
#include "takion_control_sealer.h"
#include "takion_control_proto.h"
#include "takion_session_negotiator.h"
#include "takion_data_chunk.h"
#include "halyard_launch_spec.h"
#include "rc_base64.h"
#include "rc_random.h"
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
    case RC_CONNECT_SENKUSHA_UP:   return "senkusha channel up, stream channel did not follow";
    case RC_CONNECT_TAKION_UP:     return "Takion established on the stream channel";
    case RC_CONNECT_STREAM_KEYS:   return "stream keys derived - the session is negotiated";
    case RC_CONNECT_STREAM_READY:  return "sealed, and the console has described the stream";
    default:                       return "unknown";
    }
}

#define SAY(text) do { if (log != NULL) log(text); } while (0)

/*
 * TAKION, and the two UDP ports it needs, in the order the console requires them.
 *
 * Senkusha (9297) first, because the console gates on it: it will not answer the stream channel's
 * SESSION exchange unless a senkusha bring-up has happened. The stream's own Takion handshake is 9296 -
 * a different port, and confusing the two is the obvious mistake to make.
 */
#define RC_SENKUSHA_PORT      9297u
#define RC_STREAM_PORT        9296u
#define RC_SENKUSHA_ATTEMPTS  10u
#define RC_SENKUSHA_ATTEMPT_MS 300u
#define RC_STREAM_ATTEMPTS    20u
#define RC_STREAM_ATTEMPT_MS  300u

/*
 * The receive cushion, which is not about this probe at all - the handshake's datagrams are tiny. It is
 * set now because the socket this opens is the one the A/V stream will later arrive on, and a keyframe
 * arrives as a burst of ~20 back-to-back datagrams that the stack drops the tail of if the buffer is
 * smaller than the burst.
 */
#define RC_UDP_RCVBUF (256 * 1024)

/*
 * Static rather than automatic, and for the same reason rc_log's format buffer is: these are large, the
 * main thread's stack is finite, and rc_connect is already deep in the call graph by the time it gets
 * here. The 3DS port reached the same conclusion for the same structures.
 *
 * Single-threaded, so no lock. If a second thread ever drives Takion, this needs revisiting rather than
 * copying.
 */
static takion_reliable_channel g_senkusha_channel;
static takion_reliable_channel g_stream_channel;

/*
 * THE HEARTBEAT PUMP, and the reason this whole flow is shaped around a callback.
 *
 * The console resets the session 15-30 s after heartbeat replies stop, and the Takion handshakes below
 * wait far longer than that. Single-threaded, so nothing services the control channel unless this does -
 * takion_channel_connect_ticked calls it roughly every 20 ms while it waits.
 *
 * halyard_control_session_service answers HEARTBEAT_REQ internally, so this deliberately does nothing
 * with the event: the point is the call, not the result. It must not block, and it does not.
 */
static void service_control_tick(void *ctx)
{
    halyard_control_session *session = (halyard_control_session *)ctx;
    halyard_control_event ev;

    if (session == NULL)
        return;
    memset(&ev, 0, sizeof(ev));
    (void)halyard_control_session_service(session, &ev);
}

/* Brings up one Takion channel. Returns 1 if the four-way handshake completed. */
static int takion_bring_up(takion_reliable_channel *channel, const char *host, unsigned port,
                           unsigned attempts, unsigned attempt_ms,
                           halyard_control_session *session, int *out_sock)
{
    struct sockaddr_in peer;
    int sock = rc_udp_open(host, port, &peer, RC_UDP_RCVBUF);

    *out_sock = -1;
    if (sock < 0)
        return 0;

    memset(channel, 0, sizeof(*channel));
    if (!takion_channel_connect_ticked(channel, sock, peer, attempts, attempt_ms,
                                       service_control_tick, session)) {
        (void)close(sock);
        return 0;
    }
    *out_sock = sock;
    return 1;
}

/*
 * How long to wait for each control reply on a Takion channel. The console answers these promptly when it
 * answers at all; the generous figure is for the reply that has to cross a fragmented request.
 */
#define RC_TAKION_REPLY_MS 5000u

/*
 * How long to wait for STREAM_INFO after sealing comes on. Longer than a control reply because this is
 * not an answer to anything we sent - the console produces it when its encoder is ready, and a figure
 * sized for a round trip would report a console that is merely still setting up as one that never spoke.
 */
#define RC_STREAM_INFO_WAIT_MS 10000u

/*
 * How long to hold the negotiated session open, watching. Long enough that the console's own cadence
 * shows through - it heartbeats and re-sends on its own timers - and short enough that a bring-up probe
 * still finishes. Not a streaming duration; a sampling one.
 */
#define RC_STREAM_HOLD_MS 6000u

/* PROTOCOL_VERSION_ACK's message type. Named because a bare 32 in a comparison says nothing. */
#define RC_TAKION_PROTOCOL_VERSION_ACK 32u

/*
 * The declared MTU, which is a protocol figure rather than the interface's. Senkusha's MTU legs can
 * confirm it; this port does not run them yet, so it declares the default the launch spec has always
 * used. Declaring an unverified figure is the status quo; declaring one a probe had just disproved
 * would be worse than not probing.
 */
#define RC_DECLARED_MTU 1454

/*
 * Static for the frame-size reason again, and here it is not a nicety: the launch spec is 2 KB, its
 * base64 form is another 2.7 KB, and the SESSION_REQUEST built from them is larger still. The port
 * compiles with -Wframe-larger-than=8192 and these three together would breach it on their own.
 */
static char g_launch_spec[HALYARD_LAUNCH_SPEC_MAX];
static char g_launch_spec_b64[HALYARD_LAUNCH_SPEC_B64_MAX];
static uint8_t g_session_request[HALYARD_LAUNCH_SPEC_B64_MAX + 512];
static takion_session_negotiator g_negotiator;
static takion_control_sealer g_sealer;
static takion_control_verifier g_verifier;

/*
 * The senkusha legs the console GATES ON, as opposed to the ones that merely tune the stream.
 *
 * A completed senkusha handshake is not enough by itself: the console wants the PROTOCOL_VERSION
 * exchange and a keyless SESSION exchange to have happened on that channel before it will answer the
 * stream channel's real SESSION_REQUEST. ports/ripcord-3ds established which legs those are, and this
 * follows it rather than re-deriving it.
 *
 * NOT DONE HERE: the echo and MTU measurement legs. Those produce the rtt and mtu the launch spec
 * declares, and the 3DS port's note is worth repeating - it omitted the echo leg for five phases on the
 * reasoning that it "tunes bitrate, it does not unlock anything", and both halves of that were true while
 * the conclusion still cost it, because rtt 0 is an input the console uses. This port declares the same
 * defaults it always has, which is the status quo rather than a regression, and the measurement is
 * unfinished business rather than a decision.  [X]
 */
static int senkusha_legs(halyard_control_session *session, rc_connect_result *out)
{
    uint8_t payload[256];
    size_t payload_len;

    /*
     * PROTOCOL_VERSION_REQUEST{supportedVersions=[9]}, as a literal, because building two nested protobuf
     * fields to emit seven constant bytes is more code to be wrong in than the bytes are.
     *
     * The tag arithmetic is worth reading rather than trusting: field 31 length-delimited is
     * (31 << 3) | 2 = 250, which is >= 0x80 and therefore a TWO-byte varint tag - 0xFA 0x01, not the
     * single 0xFA that writing it out by hand produces.
     */
    {
        static const uint8_t kVersionRequest[] = {
            0x08, 0x1F,                   /* field 1 (type) varint = 31, PROTOCOL_VERSION_REQUEST */
            0xFA, 0x01, 0x02, 0x08, 0x09  /* field 31, length 2: { field 1 varint = 9 }           */
        };
        memcpy(payload, kVersionRequest, sizeof(kVersionRequest));
        payload_len = sizeof(kVersionRequest);
    }

    if (!takion_channel_send(&g_senkusha_channel, TAKION_CHANNEL_PROTOCOL_VERSION, payload, payload_len))
        return 0;
    if (takion_channel_await_control(&g_senkusha_channel, RC_TAKION_PROTOCOL_VERSION_ACK,
                                     RC_TAKION_REPLY_MS, service_control_tick, session, NULL, NULL))
        out->senkusha_version_ack = 1;

    /*
     * The KEYLESS SESSION_REQUEST: client version 9, empty session key and launch spec, four zero
     * encryptedKey bytes, and no ECDH fields at all. This exchange carries nothing anyone needs - its
     * entire purpose is to have happened.
     */
    {
        takion_session_request request;
        static const uint8_t kZeroKey[4] = { 0, 0, 0, 0 };

        memset(&request, 0, sizeof(request));
        request.client_version = 9;
        request.session_key = "";
        request.session_key_length = 0;
        request.launch_spec_json = "";
        request.launch_spec_json_length = 0;
        request.encrypted_key = kZeroKey;
        request.encrypted_key_length = sizeof(kZeroKey);

        payload_len = takion_control_build_session_request(&request, payload, sizeof(payload));
        if (payload_len == 0u)
            return 0;
        if (!takion_channel_send(&g_senkusha_channel, TAKION_CHANNEL_SESSION, payload, payload_len))
            return 0;
        if (!takion_channel_await_control(&g_senkusha_channel, TAKION_CONTROL_SESSION_REPLY,
                                          RC_TAKION_REPLY_MS, service_control_tick, session, NULL, NULL))
            return 0;
    }

    out->senkusha_complete = 1;
    return 1;
}

/*
 * SESSION_REQUEST -> SESSION_REPLY -> the four per-direction stream keys.
 *
 * The launch spec travels encrypted under the CONTROL session's field cipher at counter 0 and base64'd,
 * and the 16-byte handshake key embedded in its plaintext is the same key the console signs its ECDH
 * public point under. That signature check is the one that matters: without it, anything able to inject a
 * DATA chunk on this channel could substitute its own public key and read the whole session.
 * takion_session_negotiator_accept_reply performs it and refuses the reply if it fails.
 */
static int stream_session_exchange(const halyard_pairing_record *rec,
                                   halyard_control_session *session, rc_connect_result *out)
{
    /*
     * The versions this client is willing to speak, taken from the .NET reference
     * (src/Ripcord.Protocol.Halyard.Takion/TakionSessionNegotiator.cs). 12 is absent there and absent
     * here; the gap is the reference's and is reproduced rather than tidied.
     */
    static const uint32_t kSupportedVersions[] = { 9u, 10u, 11u, 13u, 14u, 15u, 16u, 17u };

    uint8_t handshake_key[16];
    halyard_launch_spec_params params;
    uint8_t version_msg[64];
    size_t spec_len, b64_len, request_len, version_len;
    const uint8_t *reply = NULL;
    size_t reply_len = 0u;
    uint32_t version = TAKION_CLIENT_VERSION;
    int ok = 0;

    /*
     * NEGOTIATE THE VERSION ON THIS CHANNEL FIRST, and use what the console picks.
     *
     * b51 found this the expensive way. The ECDH CURVE is chosen from the negotiated version, not from
     * the one we would like - so assuming our own top version generates a key on a curve the console may
     * not have chosen. The failure surfaces nowhere near the cause: the console's reply arrives, its
     * ecdhSignature VERIFIES (an HMAC over whatever bytes it sent, which says nothing about the curve),
     * and only then is its public point rejected for being on a different curve. It reads like a crypto
     * fault and is a negotiation one.
     *
     * The senkusha channel's version exchange does not count for this. That one is part of senkusha's
     * own bring-up; this is the stream channel, and the reference asks again here.
     */
    version_len = takion_control_build_protocol_version_request(
        kSupportedVersions, sizeof(kSupportedVersions) / sizeof(kSupportedVersions[0]),
        version_msg, sizeof(version_msg));
    if (version_len > 0u
        && takion_channel_send(&g_stream_channel, TAKION_CHANNEL_PROTOCOL_VERSION,
                               version_msg, version_len)) {
        const uint8_t *ack = NULL;
        size_t ack_len = 0u;

        if (takion_channel_await_control(&g_stream_channel, TAKION_CONTROL_PROTOCOL_VERSION_ACK,
                                         RC_TAKION_REPLY_MS, service_control_tick, session,
                                         &ack, &ack_len)) {
            uint32_t agreed = 0u;

            out->stream_version_acked = 1;
            /*
             * A missing version field is not an error - fall back to what we asked for, which is what
             * the reference does. Deferring to a console that names one costs nothing and is the only
             * thing that keeps the curve choice honest if it ever picks lower.
             */
            if (takion_control_parse_protocol_version_ack(ack, ack_len, &agreed) && agreed != 0u)
                version = agreed;
        }
    }
    out->stream_version = (unsigned)version;

    if (!rc_random_bytes(handshake_key, sizeof(handshake_key)))
        return 0;
    out->stream_build_step = RC_STREAM_STEP_RANDOM;

    memset(&params, 0, sizeof(params));
    params.width = (rec->stream_width > 0) ? rec->stream_width : 640;
    params.height = (rec->stream_height > 0) ? rec->stream_height : 360;
    params.fps = (rec->fps == 60) ? 60 : 30;
    params.bitrate_kbps = (rec->stream_bitrate_kbps > 0) ? rec->stream_bitrate_kbps : 2000;
    params.mtu = RC_DECLARED_MTU;
    params.rtt_ms = 0;   /* unmeasured - see senkusha_legs' note on the echo leg  [X] */
    params.is_hevc = 0;  /* H.264 only: the decoder this port proved is openh264   */
    params.is_hdr = 0;

    out->asked_width = params.width;
    out->asked_height = params.height;
    out->asked_fps = params.fps;

    spec_len = halyard_launch_spec_build(&params, handshake_key, g_launch_spec, sizeof(g_launch_spec));
    if (spec_len == 0u)
        goto done;
    out->launch_spec_bytes = (unsigned)spec_len;
    out->stream_build_step = RC_STREAM_STEP_SPEC;

    /* AES-128-OFB under the control session's field context at counter 0. In place: OFB is symmetric and
     * the plaintext is not wanted again. */
    halyard_control_streaminfo_crypt(&session->ctrl, 0,
                                     (const uint8_t *)g_launch_spec, (uint8_t *)g_launch_spec, spec_len);

    b64_len = rc_base64_encode((const uint8_t *)g_launch_spec, spec_len,
                               g_launch_spec_b64, sizeof(g_launch_spec_b64));
    if (b64_len == 0u)
        goto done;
    out->launch_spec_b64_bytes = (unsigned)b64_len;
    out->stream_build_step = RC_STREAM_STEP_B64;

    request_len = takion_session_negotiator_begin(&g_negotiator, version, handshake_key,
                                                  g_launch_spec_b64, b64_len,
                                                  rc_random_rng_callback, NULL,
                                                  g_session_request, sizeof(g_session_request));
    if (request_len == 0u)
        goto done;

    out->stream_build_step = RC_STREAM_STEP_REQUEST;
    out->session_request_bytes = (unsigned)request_len;
    out->curve_p521 = (g_negotiator.curve == RC_ECDH_CURVE_P521);

    if (!takion_channel_send(&g_stream_channel, TAKION_CHANNEL_SESSION, g_session_request, request_len))
        goto done;
    out->stream_build_step = RC_STREAM_STEP_SENT;

    if (!takion_channel_await_control(&g_stream_channel, TAKION_CONTROL_SESSION_REPLY,
                                      RC_TAKION_REPLY_MS, service_control_tick, session,
                                      &reply, &reply_len))
        goto done;

    out->stream_build_step = RC_STREAM_STEP_REPLY;
    out->session_reply_bytes = (unsigned)reply_len;
    /* Taken immediately before the call that has been failing, which is the only place the number
     * means anything. */
    rc_stack_probe(&out->stack_size, &out->stack_used, &out->stack_headroom);

    if (!takion_session_negotiator_accept_reply(&g_negotiator, reply, reply_len,
                                                rc_random_rng_callback, NULL)) {
        out->reply_reject_reason = g_negotiator.last_reject_reason;
        out->peer_key_length = (unsigned)g_negotiator.last_peer_key_length;
        out->peer_key_prefix = (unsigned)g_negotiator.last_peer_key_prefix;
        /*
         * Taken from the negotiator, which copied the bytes while it still held them. Re-parsing the
         * reply here instead produced 133 zeros beside a recorded prefix of 0x04 - the pointer aimed
         * into channel storage that had moved on, and the dump disagreed with the field next to it.
         */
        if (g_negotiator.last_peer_key_length > 0u
            && g_negotiator.last_peer_key_length <= sizeof(out->peer_key)) {
            memcpy(out->peer_key, g_negotiator.last_peer_key, g_negotiator.last_peer_key_length);
        }
        out->ecdh_step = g_negotiator.last_ecdh_step;
        out->ecdh_code = g_negotiator.last_ecdh_code;

        /*
         * THE CONTROL. Run a KNOWN-GOOD P-521 point through the identical validator, right here, at the
         * same call depth, in the same run.
         *
         * The console's point has been checked off-console against the curve equation by integer
         * arithmetic and it IS on P-521; the same bytes are accepted by the same code built for the
         * host. Yet this machine refuses them, while four P-521 vectors sitting on its own disk pass -
         * two of which have the same high-byte shape as this point, so it is not the 521st bit.
         *
         * Those facts cannot all be about the point, so this asks whether they are about the PLACE. If
         * the control point fails here too, the environment at this call site is the subject and the
         * console's key is innocent. If the control passes and the console's does not, the reverse.
         *
         * The control is one of the vectors from ports/common/tests/vectors/session-crypto.kat, which
         * this project generates from its own .NET side - generic test material, tied to no console and
         * no account.
         */
        {
            static const unsigned char kControlPoint[133] = {
                0x04,0x01,0x0d,0x81,0xe0,0x7f,0xf3,0x60,0xff,0xb5,0x90,0x14,0xfc,0xed,0x66,0x84,
                0x93,0xaf,0x0f,0xbd,0x78,0xc2,0xb9,0xc1,0xb4,0xe4,0x7e,0xc6,0xc9,0x39,0x47,0xb1,
                0x09,0x34,0xaf,0xf3,0x12,0xc0,0xdf,0xb8,0x3c,0xbf,0xb1,0xf3,0xa5,0xa7,0x79,0xa7,
                0x68,0x09,0x89,0x44,0x4c,0x01,0x86,0x91,0xdf,0xfe,0x5b,0xd9,0x73,0xb9,0x22,0x68,
                0x1b,0x51,0x59,0x01,0xcd,0x23,0x66,0x79,0x46,0x86,0xd2,0x48,0x70,0x7b,0x65,0x6d,
                0xd8,0xc9,0x83,0xa3,0xc4,0xfc,0xff,0xf3,0xdd,0x83,0x72,0x93,0x4a,0x8a,0x95,0x3c,
                0xc1,0x87,0x59,0xb2,0x35,0x36,0x2b,0x2c,0x69,0x32,0x01,0x35,0xc3,0x91,0xf5,0x07,
                0xd3,0xde,0xba,0xf0,0x96,0xb7,0xe4,0x90,0x29,0xf9,0xc5,0xd7,0x3f,0xe9,0xbf,0x02,
                0x8f,0x26,0x80,0xfa,0xca
            };

            out->control_point_ok = rc_ecdh_check_peer_point(RC_ECDH_CURVE_P521, kControlPoint,
                                                             sizeof(kControlPoint));
            out->control_step = rc_ecdh_last_error_step();
            out->control_code = rc_ecdh_last_error_code();

            /* And the console's own point through the same entry point, so both answers come from one
             * function rather than one from derive_shared and one from here. */
            out->peer_point_ok = rc_ecdh_check_peer_point(RC_ECDH_CURVE_P521, out->peer_key,
                                                          (size_t)out->peer_key_length);

            out->precheck_ok = g_negotiator.last_precheck_ok;
            out->precheck_step = g_negotiator.last_precheck_step;
            out->precheck_code = g_negotiator.last_precheck_code;
            out->derive_fingerprint = g_negotiator.last_derive_fingerprint;
            out->copy_fingerprint = rc_ecdh_fingerprint(out->peer_key, (size_t)out->peer_key_length);
            out->derive_private_length = (unsigned)g_negotiator.last_derive_private_length;
            out->derive_curve = g_negotiator.last_derive_curve;
        }
        goto done;
    }

    out->stream_keys_derived = 1;

    /*
     * SEALING ON, IMMEDIATELY, AND BEFORE ANYTHING ELSE GOES OUT.
     *
     * From here the console authenticates every control packet it receives, so the first unsealed one is
     * the last one it listens to. That includes SACKs, which is the trap this is arranged to avoid: the
     * channel routes every outgoing control packet through one callback, so arming it here makes the
     * SACKs correct by construction rather than by anybody remembering them.
     *
     * The send key seals; the receive key is not needed yet, because GMAC authenticates and does not
     * encrypt - an incoming STREAM_INFO can be parsed as it stands. Verifying what the console sends is
     * a separate job and is not done here [X].
     */
    takion_control_sealer_init(&g_sealer, g_negotiator.send_aes_key, g_negotiator.send_base_iv);
    takion_channel_enable_sealing(&g_stream_channel, takion_control_sealer_seal, &g_sealer);
    out->sealing_on = 1;

    /*
     * AND THE RECEIVE HALF, in COUNTING mode for now.
     *
     * Sealing without verifying is half a mechanism - GMAC authenticates without encrypting, so a
     * control message from anyone parses perfectly well, and until now this port read whatever arrived
     * without asking who wrote it.
     *
     * Counting rather than enforcing on the first hardware run, deliberately. Enforcing a check that has
     * never been observed to pass turns any mistake in the receive key schedule into a session that goes
     * quiet, which is indistinguishable from the console losing interest and is the hardest failure
     * shape to diagnose - this port has already spent a dozen runs on one of those. The run reports the
     * proportion instead, and "0 of N failed" is what earns enforcement.
     */
    takion_control_verifier_init(&g_verifier, g_negotiator.receive_aes_key,
                                 g_negotiator.receive_base_iv);
    takion_channel_enable_verification(&g_stream_channel, takion_control_verifier_check, &g_verifier,
                                       0 /* count, do not drop - see above */);

    /*
     * STREAM_INFO is the console's answer to the whole negotiation: the resolution it actually chose,
     * and the SPS/PPS the first IDR will be undecodable without, since they are not carried in the video
     * stream itself. It arrives unprompted once sealing is live, and it wants an ack.
     */
    {
        const uint8_t *info_msg = NULL;
        size_t info_len = 0u;

        if (takion_channel_await_control(&g_stream_channel, TAKION_CONTROL_STREAM_INFO,
                                         RC_STREAM_INFO_WAIT_MS, service_control_tick, session,
                                         &info_msg, &info_len)) {
            takion_stream_info info;

            out->stream_info_bytes = (unsigned)info_len;
            memset(&info, 0, sizeof(info));
            if (takion_control_parse_stream_info(info_msg, info_len, &info) && info.has_resolution) {
                out->given_width = (int)info.width;
                out->given_height = (int)info.height;
                out->video_header_bytes = (unsigned)info.video_header_length;
                out->audio_header_bytes = (unsigned)info.audio_header_length;
                out->stream_info_parsed = 1;
            }

            /*
             * Acked whether or not the payload parsed. The ack says "received", and withholding it
             * because this build could not read a field would stall a console that did nothing wrong.
             */
            {
                uint8_t ack[8];
                size_t ack_len = takion_control_build_bare(TAKION_CONTROL_STREAM_INFO_ACK,
                                                           ack, sizeof(ack));
                if (ack_len > 0u
                    && takion_channel_send(&g_stream_channel, TAKION_CHANNEL_SESSION, ack, ack_len))
                    out->stream_info_acked = 1;
            }
        }
    }

    /*
     * HOLD THE SESSION OPEN AND WATCH, rather than ending the moment STREAM_INFO is acked.
     *
     * b69 armed incoming verification and came back with "1 checked, 0 failed". One passing 32-bit tag
     * is strong evidence about the KEY SCHEDULE - chance would be about one in four billion - but it is
     * a thin sample for the different question of whether everything the console sends is authenticated,
     * and the probe was ending before anything else could arrive. A sample of one cannot distinguish
     * "all console traffic is sealed" from "the one packet we happened to see was".
     *
     * So this keeps both channels serviced for a few seconds and tallies what turns up. It is also the
     * beginning of the streaming loop rather than scaffolding: holding a session open while pumping
     * heartbeats and draining the stream channel is exactly what the A/V leg has to do, and doing it
     * here first means the next step inherits something that has run on hardware.
     */
    {
        uint64_t deadline = rc_time_ms() + RC_STREAM_HOLD_MS;

        while (rc_time_ms() < deadline) {
            unsigned channel_id = 0u;
            const uint8_t *message = NULL;
            size_t message_length = 0u;
            int result;

            service_control_tick(session);

            result = takion_channel_poll(&g_stream_channel, &channel_id, &message, &message_length);
            if (result == 1) {
                uint32_t type = 0xffffffffu;

                out->held_messages++;
                if (takion_control_peek_type(message, message_length, &type)) {
                    out->held_last_type = (unsigned)type;
                    /* The console re-sends STREAM_INFO if it did not hear our ack. Answering again is
                     * cheap and silence here is expensive. */
                    if (type == TAKION_CONTROL_STREAM_INFO) {
                        uint8_t ack[8];
                        size_t ack_len = takion_control_build_bare(TAKION_CONTROL_STREAM_INFO_ACK,
                                                                   ack, sizeof(ack));
                        if (ack_len > 0u)
                            (void)takion_channel_send(&g_stream_channel, TAKION_CHANNEL_SESSION,
                                                      ack, ack_len);
                        out->held_stream_info_repeats++;
                    }
                }
            } else if (result == -1) {
                out->held_channel_error = 1;
                break;
            }
            rc_sleep_ms(5u);
        }
    }

    out->verify_checked = g_verifier.checked;
    out->verify_failed = g_verifier.failed;
    out->verify_dropped = g_stream_channel.verify_dropped;
    ok = 1;

done:
    /*
     * The handshake key does not outlive this function whatever happened. It is the key the console's
     * signature is verified under, and there is no reason for it to sit in .bss afterwards.
     */
    memset(handshake_key, 0, sizeof(handshake_key));
    return ok;
}

rc_connect_stage rc_connect(unsigned wake_timeout_ms, rc_connect_log_fn log,
                            const char *const *dirs, int dir_count, rc_connect_result *out)
{
    halyard_pairing_record rec;
    halyard_control_session session;
    int awake = 0;
    uint64_t started;

    memset(out, 0, sizeof(*out));
    memset(&rec, 0, sizeof(rec));
    out->login_verdict = -1;  /* "never arrived" is the honest default, and 0 already means accepted */

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
                 * THE CONSOLE'S VERDICT ON THE PASSCODE, which b41 could not read and so had to report
                 * a wrong passcode and a silent console as the same thing.
                 *
                 * ports/common now decrypts the console's direction, so the one byte is here. The .NET
                 * side reads plaintext[0] the same way, and established both values by controlled
                 * experiment - see HALYARD_CTRL_LOGIN_ACCEPTED.
                 */
                if (ev.type == HALYARD_CTRL_TYPE_LOGIN && ev.plaintext_length > 0u) {
                    if (ev.plaintext[0] == HALYARD_CTRL_LOGIN_ACCEPTED)
                        out->login_verdict = 0;
                    else if (ev.plaintext[0] == HALYARD_CTRL_LOGIN_REJECTED)
                        out->login_verdict = 1;
                    else
                        out->login_verdict = 2;  /* a third value nobody has seen - say so, don't round it */

                    /* A rejected passcode will not become accepted by waiting out the deadline. */
                    if (out->login_verdict != 0)
                        break;
                }

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
                     * The console's verdict arrives as LOGIN (0x0005) carrying one byte, and is handled
                     * above - ports/common decrypts the console's direction now, so a wrong passcode is
                     * no longer indistinguishable from silence.
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

    /*
     * TAKION, only if the console said it is willing to stream.
     *
     * Attempting it earlier is not merely premature, it is misleading: a console that has not answered
     * the sign-in gate SILENTLY DROPS every Takion INIT, so the handshake would time out and look like a
     * transport fault rather than the authorisation fault it is. The 3DS port records the same finding.
     *
     * The control session stays OPEN throughout and is serviced by the tick callback - see
     * service_control_tick. Closing it first would cost the session ~15-30 s later, in the middle of
     * exactly the wait this is trying to measure.
     */
    if (out->stage == RC_CONNECT_SESSION_READY) {
        int senkusha_sock = -1;
        int stream_sock = -1;

        SAY("senkusha bring-up on its own UDP port");
        if (takion_bring_up(&g_senkusha_channel, rec.host, RC_SENKUSHA_PORT,
                            RC_SENKUSHA_ATTEMPTS, RC_SENKUSHA_ATTEMPT_MS, &session, &senkusha_sock)) {
            out->senkusha_up = 1;
            out->senkusha_local_tag = (unsigned)g_senkusha_channel.local_tag;
            out->senkusha_peer_tag = (unsigned)g_senkusha_channel.peer_tag;
            out->stage = RC_CONNECT_SENKUSHA_UP;

            SAY("senkusha's gating legs (protocol version, keyless session)");
            (void)senkusha_legs(&session, out);

            SAY("Takion handshake for the stream channel");
            if (takion_bring_up(&g_stream_channel, rec.host, RC_STREAM_PORT,
                                RC_STREAM_ATTEMPTS, RC_STREAM_ATTEMPT_MS, &session, &stream_sock)) {
                out->takion_up = 1;
                out->takion_local_tag = (unsigned)g_stream_channel.local_tag;
                out->takion_peer_tag = (unsigned)g_stream_channel.peer_tag;
                out->stage = RC_CONNECT_TAKION_UP;

                if (stream_session_exchange(&rec, &session, out)) {
                    out->stage = RC_CONNECT_STREAM_KEYS;
                    if (out->stream_info_acked)
                        out->stage = RC_CONNECT_STREAM_READY;
                }
            }
        }

        /*
         * Both sockets close here. This probe stops once the transport is proven, and a channel left
         * open past the end of the run is a channel the console is still counting on.
         */
        /* The negotiator holds four live stream keys. This probe stops here, so they are wiped
         * rather than left in .bss for the remainder of the run. */
        takion_session_negotiator_reset(&g_negotiator);
        takion_control_sealer_reset(&g_sealer);
        takion_control_verifier_reset(&g_verifier);

        if (stream_sock >= 0)
            (void)close(stream_sock);
        if (senkusha_sock >= 0)
            (void)close(senkusha_sock);
    }

    halyard_control_session_close(&session);
    return out->stage;
}
