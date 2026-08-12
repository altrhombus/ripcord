/*
 * ripcord-3ds - Phase 2 UDP link test.
 *
 * The `ftpd` measurement in README.md (~10 Mbps sustained, TCP, upload direction, idle CPU) was the
 * encouraging ceiling, not the answer: it says nothing about UDP receive - the direction and protocol the
 * actual A/V path uses - and nothing about what happens once this core is also busy decoding. This is the
 * test that settles both questions.
 *
 * A host runs tools/udp_link_test_sender.py, which ramps a fixed-size UDP payload from 1 to 12 Mbps in
 * per-rate stages; this receives it, and for each stage reports goodput, sequence-gap loss, and p99
 * inter-arrival jitter. Press Y to toggle a simulated CPU load between stages, to see whether the answer
 * changes once this core is not the only thing running - the console's bottom rung wants roughly 1.5-3
 * Mbps, so the interesting stages are the ones either side of that line.
 *
 * WIRE FORMAT (this test's own, not a PlayStation protocol - see tools/udp_link_test_sender.py):
 *   offset  0, 8 bytes : sequence number, little-endian, resets to 0 at the start of each stage
 *   offset  8, 2 bytes : target rate for this stage in Mbps, little-endian; 0xFFFF marks end-of-run
 *   offset 10, 2 bytes : reserved, currently 0
 *   offset 12, 4 bytes : sender's send timestamp in microseconds, truncated to 32 bits; unused here,
 *                        kept for a host-side capture to cross-check one-way delay later if that ever
 *                        becomes the interesting question instead of jitter
 *   offset 16..1425    : filler, uninspected
 *
 * NO PER-PACKET printf(). Console output alone caps throughput well below the Wi-Fi link - SETUP.md
 * names this explicitly. Everything below is counters, sorted and printed only once per stage.
 */
#include "../net/rc_soc.h"

#include <3ds.h>

#include <arpa/inet.h>
#include <errno.h>
#include <fcntl.h>
#include <netinet/in.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/types.h>
#include <unistd.h>

#define LINKTEST_PORT 9000
#define PACKET_SIZE 1426
#define HEADER_SIZE 16
#define END_MARKER_STAGE 0xFFFFu
#define MAX_DELTA_SAMPLES 8192

/* Ticks-per-microsecond, measured at startup rather than assumed - see calibrate_tick_rate(). Doing this
 * empirically means the jitter numbers below stay correct whether or not the system tick counter's rate
 * is itself tied to osSetSpeedupEnable's clock change, without this file having to assert which. */
static double s_ticks_per_us = 1.0;

typedef struct {
    unsigned stage_mbps;
    int cpu_busy;
    int have_first;
    uint64_t count;
    uint64_t bytes;
    uint64_t min_seq;
    uint64_t max_seq;
    uint64_t first_tick;
    uint64_t last_tick;
    double deltas_us[MAX_DELTA_SAMPLES];
    size_t delta_count;
} stage_stats;

static void calibrate_tick_rate(void)
{
    uint64_t start_ms = osGetTime();
    uint64_t start_tick;
    uint64_t target_ms;

    /* osGetTime() is millisecond-resolution; wait for it to roll over first so the calibration window
     * does not start mid-millisecond, then hold a fixed window long enough that the leftover rounding
     * error from that resolution is negligible. */
    while (osGetTime() == start_ms) { }
    start_ms = osGetTime();
    start_tick = svcGetSystemTick();

    target_ms = start_ms + 200;
    while (osGetTime() < target_ms) { }

    {
        uint64_t elapsed_ticks = svcGetSystemTick() - start_tick;
        uint64_t elapsed_ms = osGetTime() - start_ms;
        if (elapsed_ms > 0)
            s_ticks_per_us = (double)elapsed_ticks / ((double)elapsed_ms * 1000.0);
    }
}

static int compare_double(const void *a, const void *b)
{
    double da = *(const double *)a;
    double db = *(const double *)b;
    return (da > db) - (da < db);
}

static void stage_begin(stage_stats *s, unsigned stage_mbps, int cpu_busy)
{
    memset(s, 0, sizeof(*s));
    s->stage_mbps = stage_mbps;
    s->cpu_busy = cpu_busy;
}

static void stage_record(stage_stats *s, uint64_t seq, size_t packet_len)
{
    uint64_t now = svcGetSystemTick();

    if (!s->have_first) {
        s->have_first = 1;
        s->min_seq = seq;
        s->max_seq = seq;
        s->first_tick = now;
    } else {
        double delta_us;

        if (seq < s->min_seq)
            s->min_seq = seq;
        if (seq > s->max_seq)
            s->max_seq = seq;

        delta_us = (double)(now - s->last_tick) / s_ticks_per_us;
        if (s->delta_count < MAX_DELTA_SAMPLES)
            s->deltas_us[s->delta_count++] = delta_us;
    }

    s->last_tick = now;
    s->count++;
    s->bytes += (uint64_t)packet_len;
}

static void stage_finish_and_print(const stage_stats *s)
{
    if (!s->have_first || s->count == 0) {
        printf("stage %2u Mbps (%s): no packets received\n",
            s->stage_mbps, s->cpu_busy ? "busy" : "idle");
        return;
    }

    {
        uint64_t expected = s->max_seq - s->min_seq + 1;
        uint64_t lost = (expected > s->count) ? expected - s->count : 0;
        double loss_pct = (expected > 0) ? (100.0 * (double)lost / (double)expected) : 0.0;

        double elapsed_us = (double)(s->last_tick - s->first_tick) / s_ticks_per_us;
        double elapsed_s = elapsed_us / 1000000.0;
        double goodput_mbps = (elapsed_s > 0.0)
            ? ((double)s->bytes * 8.0) / elapsed_s / 1000000.0
            : 0.0;

        double p99_us = 0.0;
        if (s->delta_count > 0) {
            static double sorted[MAX_DELTA_SAMPLES];
            size_t idx;

            memcpy(sorted, s->deltas_us, s->delta_count * sizeof(double));
            qsort(sorted, s->delta_count, sizeof(double), compare_double);

            idx = (size_t)((double)s->delta_count * 0.99);
            if (idx >= s->delta_count)
                idx = s->delta_count - 1;
            p99_us = sorted[idx];
        }

        printf("stage %2u Mbps (%s): %llu pkts, %.2f%% loss, %.2f Mbps goodput, p99 %.0f us%s\n",
            s->stage_mbps, s->cpu_busy ? "busy" : "idle",
            (unsigned long long)s->count, loss_pct, goodput_mbps, p99_us,
            s->delta_count >= MAX_DELTA_SAMPLES ? " (capped)" : "");
    }
}

/* Simulates the CPU contention a real decode pipeline would add, without trying to model its exact cost:
 * burns roughly half of one 60Hz frame's budget on this core between UDP drains. The question this
 * answers is whether the receive loop keeps up when it is not the only thing running - toggle with Y and
 * compare a stage's idle and busy numbers directly. */
static void spin_busy_work(void)
{
    uint64_t start = svcGetSystemTick();
    double budget_ticks = 8000.0 * s_ticks_per_us;
    volatile uint32_t sink = 0;

    while ((double)(svcGetSystemTick() - start) < budget_ticks)
        sink += 1;
    (void)sink;
}

static int parse_header(const uint8_t *buf, size_t len, uint64_t *out_seq, unsigned *out_stage)
{
    uint64_t seq;
    unsigned i;

    if (len < HEADER_SIZE)
        return 0;

    seq = 0;
    for (i = 0; i < 8; i++)
        seq |= (uint64_t)buf[i] << (8 * i);

    *out_seq = seq;
    *out_stage = (unsigned)buf[8] | ((unsigned)buf[9] << 8);
    return 1;
}

static int run_linktest(void)
{
    struct in_addr local;
    int sock;
    struct sockaddr_in bind_addr;
    static stage_stats stage;
    static uint8_t buf[PACKET_SIZE];
    int have_stage = 0;
    int cpu_busy = 0;
    int run_done = 0;

    if (rc_soc_init() != 0) {
        printf("\x1b[31mFAIL\x1b[0m SOC init failed\n");
        return 1;
    }

    local = rc_soc_local_address();
    printf("listening on %s:%d\n", inet_ntoa(local), LINKTEST_PORT);
    printf("point tools/udp_link_test_sender.py at this address.\n");
    printf("Y toggles simulated CPU load, START exits.\n\n");

    sock = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (sock < 0) {
        printf("\x1b[31mFAIL\x1b[0m socket() failed: %d\n", errno);
        rc_soc_exit();
        return 1;
    }

    memset(&bind_addr, 0, sizeof(bind_addr));
    bind_addr.sin_family = AF_INET;
    bind_addr.sin_addr.s_addr = INADDR_ANY;
    bind_addr.sin_port = htons(LINKTEST_PORT);
    if (bind(sock, (struct sockaddr *)&bind_addr, sizeof(bind_addr)) != 0) {
        printf("\x1b[31mFAIL\x1b[0m bind() failed: %d\n", errno);
        close(sock);
        rc_soc_exit();
        return 1;
    }
    fcntl(sock, F_SETFL, O_NONBLOCK);

    while (aptMainLoop()) {
        u32 kdown;

        hidScanInput();
        kdown = hidKeysDown();
        if (kdown & KEY_START)
            break;
        if (kdown & KEY_Y)
            cpu_busy = !cpu_busy;

        for (;;) {
            ssize_t n = recvfrom(sock, buf, sizeof(buf), 0, NULL, NULL);
            uint64_t seq;
            unsigned pkt_stage;

            if (n < 0)
                break; /* EAGAIN/EWOULDBLOCK - drained for this frame */

            if (!parse_header(buf, (size_t)n, &seq, &pkt_stage))
                continue;

            if (pkt_stage == END_MARKER_STAGE) {
                if (have_stage) {
                    stage_finish_and_print(&stage);
                    have_stage = 0;
                }
                run_done = 1;
                continue;
            }

            if (!have_stage || stage.stage_mbps != pkt_stage) {
                if (have_stage)
                    stage_finish_and_print(&stage);
                stage_begin(&stage, pkt_stage, cpu_busy);
                have_stage = 1;
            }
            stage_record(&stage, seq, (size_t)n);
        }

        if (cpu_busy)
            spin_busy_work();

        gfxFlushBuffers();
        gfxSwapBuffers();
        gspWaitForVBlank();
    }

    if (have_stage)
        stage_finish_and_print(&stage);
    if (run_done)
        printf("\nrun complete - see stage lines above\n");

    close(sock);
    rc_soc_exit();
    return 0;
}

int main(int argc, char **argv)
{
    (void)argc;
    (void)argv;

    /* Without this a New 3DS runs at the Old 3DS clock speed - any timing number taken without it
     * describes a machine we are not targeting. Named directly in SETUP.md's gotcha list. */
    osSetSpeedupEnable(true);

    gfxInitDefault();
    consoleInit(GFX_TOP, NULL);

    printf("ripcord-3ds UDP link test (Phase 2)\n");
    printf("------------------------------------\n");

    calibrate_tick_rate();
    run_linktest();

    printf("\nPress START to exit.\n");
    while (aptMainLoop()) {
        hidScanInput();
        if (hidKeysDown() & KEY_START)
            break;
        gfxFlushBuffers();
        gfxSwapBuffers();
        gspWaitForVBlank();
    }

    gfxExit();
    return 0;
}
