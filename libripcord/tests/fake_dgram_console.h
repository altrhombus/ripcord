/*
 * ripcord host tests - a console's side of the UDP 9303 association, scripted, for the runners that need
 * one: control_pipe_test (in-process, no sockets) and rendezvous_test (over loopback UDP, in a thread).
 *
 * The same shapes dgram_test.c's scripted console answers with - which reproduces the .NET suites'
 * (HalyardDatagramControlChannelTests, HalyardDatagramSessionControlChannelTests) - grown by what a
 * session needs: /sess/init is answered with an RP-Nonce and the connection closed, /sess/ctrl with a 200
 * and the connection KEPT for the binary frames, and every non-HTTP payload after that is recorded as one
 * control frame. It answers; it never originates, except through fake_console_push/close, so a client that
 * sent its steps out of order stalls rather than passing by accident.
 *
 * Header-only (static functions) because two runners use it and neither links the other.
 */
#ifndef FAKE_DGRAM_CONSOLE_H
#define FAKE_DGRAM_CONSOLE_H

#include "../session/halyard_dgram.h"

#include <stdio.h>
#include <string.h>

#define FAKE_MAX_REQUESTS 4
#define FAKE_MAX_FRAMES 16

typedef void (*fake_emit_fn)(void *ctx, const uint8_t *datagram, size_t length);

typedef struct {
    fake_emit_fn emit;
    void *emit_ctx;
    uint16_t sequence;

    const char *init_reply;             /* the whole HTTP reply to /sess/init */
    const char *other_reply;            /* to anything else that is not /sess/ctrl, e.g. rgst; NULL: 200 */
    int close_before_answering;         /* for a POST: tear down instead of replying */

    int inits, hellos, closes_received;
    int requests;
    char request[FAKE_MAX_REQUESTS][1024];
    size_t request_length[FAKE_MAX_REQUESTS];
    int ctrl_open;                      /* /sess/ctrl answered: payloads are frames from here */
    int frames;
    uint8_t frame[FAKE_MAX_FRAMES][300];
    size_t frame_length[FAKE_MAX_FRAMES];
} fake_console;

static const uint8_t *fake_console_id(void)
{
    static uint8_t id[20];
    int i;

    for (i = 0; i < 20; i++)
        id[i] = (uint8_t)(101 + i);
    return id;
}

static const uint8_t *fake_client_id(void)
{
    static uint8_t id[20];
    int i;

    for (i = 0; i < 20; i++)
        id[i] = (uint8_t)(i + 1);
    return id;
}

static void fake_console_init(fake_console *fc, fake_emit_fn emit, void *emit_ctx)
{
    memset(fc, 0, sizeof(*fc));
    fc->emit = emit;
    fc->emit_ctx = emit_ctx;
    fc->sequence = 0xA65E;
}

static void fake_prelude(fake_console *fc, uint32_t type, uint32_t tag_pair, uint32_t token)
{
    halyard_dgram_prelude p;
    uint8_t wire[HALYARD_DGRAM_PRELUDE_LENGTH];

    memset(&p, 0, sizeof(p));
    p.type = type;
    memcpy(p.sender_id, fake_console_id(), 20);
    memcpy(p.peer_id, fake_client_id(), 20);
    p.tag_pair = tag_pair;
    p.request_word = type == HALYARD_DGRAM_PRELUDE_INIT ? 0x19u : 0u;
    p.token = token;
    fc->emit(fc->emit_ctx, wire, halyard_dgram_prelude_write(&p, wire, sizeof(wire)));
}

/* A console-originated payload on the open connection. */
static void fake_console_push(fake_console *fc, const uint8_t *payload, size_t length)
{
    uint8_t body[1100];
    uint8_t wire[1200];
    size_t n;

    if (length + 2u > sizeof(body))
        return;
    fc->sequence++;
    body[0] = (uint8_t)(fc->sequence >> 8);
    body[1] = (uint8_t)fc->sequence;
    memcpy(body + 2, payload, length);
    n = halyard_dgram_chunk_write(wire, sizeof(wire), HALYARD_DGRAM_CHUNK_DATA, 0x30, body, length + 2u, 3);
    fc->emit(fc->emit_ctx, wire, n);
}

/* The console's teardown of the open connection. */
static void fake_console_close(fake_console *fc)
{
    static const uint8_t close_body[8];
    uint8_t wire[64];
    size_t n = halyard_dgram_chunk_write(wire, sizeof(wire), HALYARD_DGRAM_CHUNK_CLOSE, 0x00, close_body, 8, 3);

    fc->emit(fc->emit_ctx, wire, n);
}

/* `text` is the request, NUL-terminated by the caller. */
static void fake_on_http(fake_console *fc, const char *text, size_t length)
{
    static const char ctrl_reply[] = "HTTP/1.1 200 OK\r\nRP-Version: 1.0\r\nContent-Length: 0\r\n\r\n";
    static const char plain_reply[] = "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n";
    int is_ctrl = strstr(text, "/sess/ctrl") != NULL;
    int is_init = !is_ctrl && strstr(text, "/sess/init") != NULL;

    if (fc->requests < FAKE_MAX_REQUESTS && length < sizeof(fc->request[0])) {
        memcpy(fc->request[fc->requests], text, length);
        fc->request[fc->requests][length] = '\0';
        fc->request_length[fc->requests] = length;
    }
    fc->requests++;

    if (is_ctrl) {
        fake_console_push(fc, (const uint8_t *)ctrl_reply, sizeof(ctrl_reply) - 1u);
        fc->ctrl_open = 1;
        return;
    }
    if (!is_init && fc->close_before_answering) {
        fake_console_close(fc);
        return;
    }
    if (is_init && fc->init_reply != NULL)
        fake_console_push(fc, (const uint8_t *)fc->init_reply, strlen(fc->init_reply));
    else if (!is_init && fc->other_reply != NULL)
        fake_console_push(fc, (const uint8_t *)fc->other_reply, strlen(fc->other_reply));
    else
        fake_console_push(fc, (const uint8_t *)plain_reply, sizeof(plain_reply) - 1u);
    /* ...and tear the connection down, which is what makes the next request need a new one. */
    fake_console_close(fc);
}

/* Everything the console does is a reaction to a datagram from the client. */
static void fake_console_on_datagram(fake_console *fc, const uint8_t *datagram, size_t length)
{
    halyard_dgram_prelude p;
    halyard_dgram_chunk chunk;
    size_t offset = 0;
    uint8_t wire[256];
    size_t n;

    if (halyard_dgram_prelude_parse(datagram, length, &p)) {
        if (p.type != HALYARD_DGRAM_PRELUDE_INIT)
            return;
        fc->inits++;
        /* Answer, then echo - the shape a real console uses. */
        fake_prelude(fc, HALYARD_DGRAM_PRELUDE_INIT, halyard_dgram_swap_halves(p.tag_pair), 0x01B9ACBEu);
        fake_prelude(fc, HALYARD_DGRAM_PRELUDE_COOKIE_ECHO, p.tag_pair, p.token);
        return;
    }

    while (halyard_dgram_chunk_next(datagram, length, &offset, &chunk)) {
        if (chunk.type == HALYARD_DGRAM_CHUNK_HELLO) {
            uint8_t cookie[42];
            int i;

            fc->hellos++;
            for (i = 0; i < 42; i++)
                cookie[i] = (uint8_t)(0xE0 + i);
            n = halyard_dgram_chunk_write(wire, sizeof(wire), HALYARD_DGRAM_CHUNK_COOKIE, 0x00, cookie, 42, 3);
            fc->emit(fc->emit_ctx, wire, n);
        } else if (chunk.type == HALYARD_DGRAM_CHUNK_HELLO_ECHO) {
            uint16_t hello_seq = (uint16_t)((chunk.body[0] << 8) | chunk.body[1]);
            uint8_t accept[12] = { 0 };

            accept[0] = (uint8_t)(fc->sequence >> 8);
            accept[1] = (uint8_t)fc->sequence;
            accept[2] = (uint8_t)((hello_seq + 1) >> 8);
            accept[3] = (uint8_t)(hello_seq + 1);
            fc->ctrl_open = 0;
            n = halyard_dgram_chunk_write(wire, sizeof(wire), HALYARD_DGRAM_CHUNK_ACCEPT, 0x30, accept, 12, 3);
            fc->emit(fc->emit_ctx, wire, n);
        } else if (chunk.type == HALYARD_DGRAM_CHUNK_CLOSE) {
            fc->closes_received++;
        } else if (chunk.type == HALYARD_DGRAM_CHUNK_DATA && chunk.body_length > 2u) {
            const uint8_t *payload = chunk.body + 2;
            size_t payload_length = chunk.body_length - 2u;
            int is_http = (payload_length >= 4u && memcmp(payload, "GET ", 4) == 0)
                          || (payload_length >= 5u && memcmp(payload, "POST ", 5) == 0);

            if (is_http && !fc->ctrl_open) {
                char text[1100];
                size_t take = payload_length < sizeof(text) - 1u ? payload_length : sizeof(text) - 1u;

                memcpy(text, payload, take);
                text[take] = '\0';
                fake_on_http(fc, text, take);
            } else if (fc->frames < FAKE_MAX_FRAMES && payload_length <= sizeof(fc->frame[0])) {
                memcpy(fc->frame[fc->frames], payload, payload_length);
                fc->frame_length[fc->frames] = payload_length;
                fc->frames++;
            }
        }
    }
}

#endif /* FAKE_DGRAM_CONSOLE_H */
