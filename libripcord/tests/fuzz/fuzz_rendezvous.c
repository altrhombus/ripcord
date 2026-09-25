/*
 * libripcord fuzzing - the control session over the 9303 byte pipe (halyard_dgram_session.h).
 *
 * On the rendezvous route the console's binary control frames arrive as datagram payload, not as a TCP
 * stream: the association accumulates them, halyard_dgram_control_pipe hands them to the control session
 * in whatever pieces they came, and the session's frame parser, resynchronisation and field decrypt run
 * on them. Every byte of that comes from whatever can reach the control leg's reflexive address.
 *
 * So each input starts from a CONNECTED association - driven there deterministically by the scripted
 * console the host tests use (fake_dgram_console.h) - with a control session attached as open() would
 * leave it, and then feeds the records: a record whose first byte is odd is delivered as the payload of a
 * well-formed DATA chunk (so the fuzzer steers the frame stream directly), an even one as a raw datagram
 * (so the association's own parsing and a Close are reachable too). The session is serviced after each,
 * which is also what sends the heartbeat replies back through the pipe.
 */
#include "../fake_dgram_console.h"
#include "../../halyard/halyard_v1.h"
#include "../../session/halyard_control_session.h"
#include "../../session/halyard_dgram_session.h"
#include "../../util/rc_log.h"
#include "fuzz_input.h"

#include <stddef.h>
#include <stdint.h>
#include <string.h>

int LLVMFuzzerTestOneInput(const uint8_t *data, size_t size);

/* The fuzz archive links no CSPRNG (rc_platform_host.c says why); a counter keeps a crash reproducible. */
static uint8_t s_next;

int rc_random_bytes(uint8_t *out, size_t length)
{
    size_t i;

    for (i = 0; i < length; i++)
        out[i] = s_next++;
    return 1;
}

#define QUEUE_MAX 32

typedef struct {
    size_t count, head;
    size_t length[QUEUE_MAX];
    uint8_t data[QUEUE_MAX][1300];
} queue;

static queue s_queue;
static fake_console s_console;
static halyard_dgram_channel s_channel;
static halyard_dgram_control_pipe s_pipe;
static halyard_control_session s_session;

static void enqueue(void *ctx, const uint8_t *datagram, size_t length)
{
    size_t slot = (s_queue.head + s_queue.count) % QUEUE_MAX;

    (void)ctx;
    if (s_queue.count < QUEUE_MAX && length <= sizeof(s_queue.data[0])) {
        memcpy(s_queue.data[slot], datagram, length);
        s_queue.length[slot] = length;
        s_queue.count++;
    }
}

static int t_send(void *ctx, const uint8_t *datagram, size_t length)
{
    (void)ctx;
    fake_console_on_datagram(&s_console, datagram, length);
    return 1;
}

static long t_receive(void *ctx, uint8_t *buffer, size_t capacity)
{
    size_t length;

    (void)ctx;
    if (s_queue.count == 0)
        return 0;
    length = s_queue.length[s_queue.head] < capacity ? s_queue.length[s_queue.head] : capacity;
    memcpy(buffer, s_queue.data[s_queue.head], length);
    s_queue.head = (s_queue.head + 1) % QUEUE_MAX;
    s_queue.count--;
    return (long)length;
}

static void quiet(void *user, const char *line)
{
    (void)user;
    (void)line;
}

static int connected_session(void)
{
    static const uint8_t address[4] = { 192, 0, 2, 7 };
    static const uint8_t nonce[16] = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
    static const uint8_t companion[16] = { 0xC0, 0xC1, 0xC2, 0xC3, 0xC4, 0xC5, 0xC6, 0xC7,
                                           0xC8, 0xC9, 0xCA, 0xCB, 0xCC, 0xCD, 0xCE, 0xCF };
    halyard_dgram_transport t;
    halyard_dgram_options o;

    memset(&s_queue, 0, sizeof(s_queue));
    s_next = 0x40;
    fake_console_init(&s_console, enqueue, NULL);
    t.send = t_send;
    t.receive = t_receive;
    t.ctx = NULL;
    halyard_dgram_options_default(&o);
    o.receive_timeout_ms = 1;
    o.stage_timeout_ms = 50;
    if (halyard_dgram_channel_init(&s_channel, &t, address, 9303, fake_client_id(), fake_console_id(), &o)
            != HALYARD_DGRAM_CHANNEL_OK
        || halyard_dgram_channel_establish(&s_channel) != HALYARD_DGRAM_CHANNEL_OK
        || halyard_dgram_channel_open_connection(&s_channel) != HALYARD_DGRAM_CHANNEL_OK)
        return 0;

    /* As open() leaves it: the pipe attached, the cipher up, both counters positioned. */
    halyard_dgram_control_pipe_init(&s_pipe, &s_channel, 5);
    memset(&s_session, 0, sizeof(s_session));
    s_session.pipe = &s_pipe.pipe;
    s_session.sock = 5;
    if (halyard_control_field_init(&s_session.ctrl, nonce, companion, 2, HALYARD_VERSION_SELECTOR_PS5) != 0)
        memset(&s_session.ctrl, 0, sizeof(s_session.ctrl));
    s_session.next_counter = 5;
    s_session.recv_counter = 1;
    s_console.ctrl_open = 1;
    return 1;
}

int LLVMFuzzerTestOneInput(const uint8_t *data, size_t size)
{
    rc_fuzz_input in;
    const uint8_t *record;
    size_t record_length;
    int alive = 1;

    rc_log_set_sink(quiet, NULL);
    if (!connected_session())
        return 0;

    rc_fuzz_input_init(&in, data, size);
    while (alive && rc_fuzz_next(&in, &record, &record_length)) {
        int steps, idle;

        if (record_length > 0u && (record[0] & 1u))
            fake_console_push(&s_console, record + 1, record_length - 1u);
        else
            enqueue(NULL, record, record_length);

        /* service() parses what is buffered OR reads more, one per call, so a record takes at least two
         * calls to surface; two quiet calls in a row mean it has all been seen. */
        for (steps = 0, idle = 0; steps < 16 && alive && idle < 2; steps++) {
            halyard_control_event ev;

            memset(&ev, 0, sizeof(ev));
            alive = halyard_control_session_service(&s_session, &ev);
            if (ev.kind == HALYARD_CONTROL_EVENT_NONE) {
                idle++;
                continue;
            }
            idle = 0;
            rc_fuzz_touch(ev.payload, ev.payload_length);
            rc_fuzz_touch(ev.plaintext, ev.plaintext_length);
        }
    }
    halyard_control_session_close(&s_session);
    return 0;
}
