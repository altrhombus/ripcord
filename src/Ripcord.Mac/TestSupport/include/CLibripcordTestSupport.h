/*
 * ripcord for Mac, tests only - a console's side of the 9303 association on a loopback UDP socket, for
 * RipcordKitTests to drive the C-backed rendezvous transports against.
 *
 * It is libripcord/tests/fake_dgram_console.h, the scripted console the C suites (control_pipe_test,
 * rendezvous_test) already use, reused as is and not copied, plus the one thing those suites do elsewhere:
 * answering /sess/rgst. account_pairing_test.c's fake does that as an exchange callback; here it has to be
 * a datagram, so the rgst request is intercepted before the scripted console sees it and answered with a
 * pairing record sealed under the seed, written with the core's own inverse functions exactly as
 * account_pairing_test.c writes it. Everything else (the prelude, connections, /sess/init, /sess/ctrl) is
 * the scripted console's.
 *
 * Header-only, as fake_dgram_console.h is. The socket is bound to 127.0.0.1 and answers only whoever wrote
 * to it, so nothing leaves loopback. One thread owns an instance: the test's console thread steps it, and
 * reads the counters after that thread has stopped.
 */
#ifndef CLIBRIPCORD_TEST_SUPPORT_H
#define CLIBRIPCORD_TEST_SUPPORT_H

#include "../../../../libripcord/tests/fake_dgram_console.h"
#include "../../../../libripcord/halyard/halyard_registration.h"
#include "../../../../libripcord/halyard/halyard_v1.h"
#include "../../../../libripcord/session/halyard_regist_message.h"

#include <arpa/inet.h>
#include <netinet/in.h>
#include <stdio.h>
#include <string.h>
#include <sys/socket.h>
#include <unistd.h>

/* The record the fake console issues: registration key "1a2b3c4d" (as the hex of its ASCII, as a console
 * sends it), a companion 00..0f. Synthetic, like every value in these tests. */
#define RC_TEST_RECORD \
    "PS5-RegistKey: 3161326233633464\r\nRP-Key: 000102030405060708090a0b0c0d0e0f\r\nRP-KeyType: 2\r\n"

typedef struct {
    int sock;
    uint16_t port;
    struct sockaddr_in client;
    int have_client;
    fake_console console;
    char init_reply[160];

    int is_ps5;
    uint8_t seed[16];
    int rgst_requests;
    int rgst_field_ok;            /* the request's field decrypted to Client-Type / Np-AccountId */
} rc_test_console;

static void rc_test_console_emit(void *ctx, const uint8_t *datagram, size_t length)
{
    rc_test_console *tc = (rc_test_console *)ctx;

    if (tc->have_client)
        (void)sendto(tc->sock, datagram, length, 0, (const struct sockaddr *)&tc->client,
                     (socklen_t)sizeof(tc->client));
}

/* Binds 127.0.0.1 on any port. `nonce_b64` is /sess/init's RP-Nonce. Returns 1, or 0 with nothing open. */
static int rc_test_console_open(rc_test_console *tc, int is_ps5, const uint8_t seed[16], const char *nonce_b64)
{
    struct sockaddr_in a;
    socklen_t len = (socklen_t)sizeof(a);

    memset(tc, 0, sizeof(*tc));
    tc->sock = socket(AF_INET, SOCK_DGRAM, 0);
    if (tc->sock < 0)
        return 0;
    memset(&a, 0, sizeof(a));
    a.sin_family = AF_INET;
    a.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    if (bind(tc->sock, (struct sockaddr *)&a, sizeof(a)) != 0 || getsockname(tc->sock, (struct sockaddr *)&a, &len) != 0) {
        close(tc->sock);
        tc->sock = -1;
        return 0;
    }
    tc->port = ntohs(a.sin_port);
    tc->is_ps5 = is_ps5;
    memcpy(tc->seed, seed, 16);
    fake_console_init(&tc->console, rc_test_console_emit, tc);
    snprintf(tc->init_reply, sizeof(tc->init_reply), "HTTP/1.1 200 OK\r\nRP-Nonce: %s\r\nContent-Length: 0\r\n\r\n",
             nonce_b64);
    tc->console.init_reply = tc->init_reply;
    return 1;
}

static void rc_test_console_close(rc_test_console *tc)
{
    if (tc->sock >= 0)
        close(tc->sock);
    tc->sock = -1;
}

/* The console's half of /sess/rgst, account_pairing_test.c's fake_console over a datagram. */
static void rc_test_console_answer_rgst(rc_test_console *tc, const uint8_t *request, size_t request_length)
{
    static const char record[] = RC_TEST_RECORD;
    const uint8_t *body = NULL;
    size_t body_len, i;
    uint8_t wrapped[16], material[16];
    uint8_t field_plain[256];
    uint8_t reply[512];
    halyard_control_field field;
    int head;

    tc->rgst_requests++;
    if (tc->console.requests < FAKE_MAX_REQUESTS && request_length < sizeof(tc->console.request[0])) {
        memcpy(tc->console.request[tc->console.requests], request, request_length);
        tc->console.request[tc->console.requests][request_length] = '\0';
        tc->console.request_length[tc->console.requests] = request_length;
    }
    tc->console.requests++;

    for (i = 0; i + 3 < request_length; i++) {
        if (memcmp(request + i, "\r\n\r\n", 4) == 0) {
            body = request + i + 4;
            break;
        }
    }
    if (body == NULL)
        return;
    body_len = request_length - (size_t)(body - request);
    if (body_len <= HALYARD_REGISTRATION_CONTEXT_LENGTH
        || body_len - HALYARD_REGISTRATION_CONTEXT_LENGTH > sizeof(field_plain))
        return;
    if (!halyard_registration_gather(body, HALYARD_REGISTRATION_CONTEXT_LENGTH, wrapped)
        || !halyard_registration_unwrap_account_material(tc->is_ps5, wrapped, body, HALYARD_REGISTRATION_CONTEXT_LENGTH,
                                                         material)
        || !halyard_registration_account_field_init(&field, tc->is_ps5, body, HALYARD_REGISTRATION_CONTEXT_LENGTH,
                                                    tc->seed, material))
        return;
    halyard_control_field_decrypt(&field, HALYARD_REGISTRATION_FIELD_COUNTER, body + HALYARD_REGISTRATION_CONTEXT_LENGTH,
                                  field_plain, body_len - HALYARD_REGISTRATION_CONTEXT_LENGTH);
    tc->rgst_field_ok = memcmp(field_plain, "Client-Type: " HALYARD_REGIST_CLIENT_TYPE_HEX "\r\nNp-AccountId: ",
                               13 + 64 + 2 + 14) == 0;

    head = snprintf((char *)reply, sizeof(reply), "HTTP/1.1 200 OK\r\nContent-Length: %u\r\n\r\n",
                    (unsigned)(sizeof(record) - 1));
    if (head < 0 || (size_t)head + sizeof(record) - 1 > sizeof(reply))
        return;
    halyard_control_field_encrypt(&field, HALYARD_REGISTRATION_FIELD_COUNTER, (const uint8_t *)record,
                                  reply + head, sizeof(record) - 1);
    fake_console_push(&tc->console, reply, (size_t)head + sizeof(record) - 1);
    /* ...and tear the connection down, as the scripted console does after every request but ctrl. */
    fake_console_close(&tc->console);
}

/* Whether `datagram` carries the rgst request; if so it is answered here and 1 is returned. */
static int rc_test_console_intercept(rc_test_console *tc, const uint8_t *datagram, size_t length)
{
    halyard_dgram_chunk chunk;
    size_t offset = 0;

    while (halyard_dgram_chunk_next(datagram, length, &offset, &chunk)) {
        const uint8_t *payload;
        size_t payload_length, i;

        if (chunk.type != HALYARD_DGRAM_CHUNK_DATA || chunk.body_length <= 2u)
            continue;
        payload = chunk.body + 2;
        payload_length = chunk.body_length - 2u;
        if (payload_length < 5u || memcmp(payload, "POST ", 5) != 0)
            continue;
        for (i = 5; i + 10 <= payload_length && payload[i] != '\r'; i++) {
            if (memcmp(payload + i, "/sess/rgst", 10) == 0) {
                rc_test_console_answer_rgst(tc, payload, payload_length);
                return 1;
            }
        }
    }
    return 0;
}

/* One non-blocking receive and whatever the console does about it. Returns 1 if a datagram was handled. */
static int rc_test_console_step(rc_test_console *tc)
{
    uint8_t buf[2048];
    struct sockaddr_in from;
    socklen_t from_length = (socklen_t)sizeof(from);
    ssize_t n = recvfrom(tc->sock, buf, sizeof(buf), MSG_DONTWAIT, (struct sockaddr *)&from, &from_length);

    if (n <= 0)
        return 0;
    tc->client = from;
    tc->have_client = 1;
    if (!rc_test_console_intercept(tc, buf, (size_t)n))
        fake_console_on_datagram(&tc->console, buf, (size_t)n);
    return 1;
}

/* Whether request `index` (in arrival order) contains `needle`. */
static int rc_test_console_request_contains(const rc_test_console *tc, int index, const char *needle)
{
    if (index < 0 || index >= tc->console.requests || index >= FAKE_MAX_REQUESTS)
        return 0;
    return strstr(tc->console.request[index], needle) != NULL;
}

/* The ids the scripted console names itself and expects us by (fake_dgram_console.h). */
static void rc_test_console_ids(uint8_t console_id[20], uint8_t client_id[20])
{
    memcpy(console_id, fake_console_id(), 20);
    memcpy(client_id, fake_client_id(), 20);
}

#endif /* CLIBRIPCORD_TEST_SUPPORT_H */
