/*
 * Known-answer tests for the feedback packet writer, checked against spec 6.3's byte map (itself derived
 * from cap48). Hand-encoded expectations, not round-trips: what is under test is agreement with the
 * capture, and a round-trip would agree with itself while both were wrong.
 */
#include "../source/input/halyard_input.h"

#include <stdio.h>
#include <string.h>

static int g_passed, g_failed;

static void check(int ok, const char *what)
{
    if (ok) {
        g_passed++;
    } else {
        g_failed++;
        printf("FAIL %s\n", what);
    }
}

static void test_state_payload(void)
{
    halyard_input_state s;
    uint8_t buf[64];
    size_t n;

    memset(&s, 0, sizeof(s));
    s.left_x = -32767;   /* the scripted "full left" excursion */
    s.left_y = 32767;
    s.right_x = 1;
    s.right_y = -2;

    n = halyard_input_build_state_payload(&s, buf, sizeof(buf));
    check(n == 0x1c, "state payload is 0x1c bytes");
    check(buf[0x00] == 0xa0, "state lead byte 0xa0");
    check(buf[0x1b] == 0xca, "state tail byte 0xca");
    check(buf[0x19] == 0x00 && buf[0x1a] == 0x00, "state [0x19]/[0x1a] zero");

    /* Motion fields are u16 LITTLE-endian; gyro rests at 0x7fff. */
    check(buf[0x01] == 0xff && buf[0x02] == 0x7f, "gyro X rests at 0x7fff little-endian");
    check(buf[0x0b] == 0xff && buf[0x0c] == 0x7f, "accel Z rests at 0x7fff little-endian");

    /* Sticks are s16 BIG-endian: -32767 is 0x8001, +32767 is 0x7fff. */
    check(buf[0x11] == 0x80 && buf[0x12] == 0x01, "left X -32767 as 8001 big-endian");
    check(buf[0x13] == 0x7f && buf[0x14] == 0xff, "left Y +32767 as 7fff big-endian");
    check(buf[0x15] == 0x00 && buf[0x16] == 0x01, "right X +1 big-endian");
    check(buf[0x17] == 0xff && buf[0x18] == 0xfe, "right Y -2 big-endian");

    check(halyard_input_build_state_payload(&s, buf, 0x1b) == 0, "short buffer refused");
}

static void test_history_events(void)
{
    halyard_input_writer w;
    halyard_input_state s;
    uint8_t buf[64];
    size_t n;

    halyard_input_writer_init(&w);
    memset(&s, 0, sizeof(s));

    /* No previous frame: nothing to diff, so no history packet at all. */
    check(halyard_input_build_history_payload(&w, &s, buf, sizeof(buf)) == 0,
          "first frame emits no history");

    w.have_previous = 1;

    /* 3-byte form: cross pressed -> 80 88 ff */
    s.buttons = HALYARD_PAD_CROSS;
    n = halyard_input_build_history_payload(&w, &s, buf, sizeof(buf));
    check(n == 3 && buf[0] == 0x80 && buf[1] == 0x88 && buf[2] == 0xff, "cross press 80 88 ff");

    /*
     * ...and released -> 80 88 00 at the HEAD. The payload is longer than one event because history is
     * cumulative; what this checks is the newest event's encoding and its position, not the total length.
     */
    w.previous.buttons = HALYARD_PAD_CROSS;
    s.buttons = 0;
    n = halyard_input_build_history_payload(&w, &s, buf, sizeof(buf));
    check(n >= 3 && buf[0] == 0x80 && buf[1] == 0x88 && buf[2] == 0x00, "cross release 80 88 00 leads");

    /*
     * 2-byte form: the press is folded into the code as +0x20, with no state byte. cap48 confirmed the
     * pair directly - 0x8e released and 0xae pressed appeared adjacent in one 4-byte packet.
     */
    halyard_input_writer_init(&w);
    w.have_previous = 1;
    s.buttons = HALYARD_PAD_PS;
    n = halyard_input_build_history_payload(&w, &s, buf, sizeof(buf));
    check(n == 2 && buf[0] == 0x80 && buf[1] == 0xae, "PS press folds to 0xae, no state byte");

    w.previous.buttons = HALYARD_PAD_PS;
    s.buttons = 0;
    n = halyard_input_build_history_payload(&w, &s, buf, sizeof(buf));
    check(n >= 2 && buf[0] == 0x80 && buf[1] == 0x8e, "PS release is the base code 0x8e, and leads");

    /* Unchanged buttons produce nothing - history is transitions only. */
    halyard_input_writer_init(&w);
    w.have_previous = 1;
    w.previous.buttons = HALYARD_PAD_CROSS;
    s.buttons = HALYARD_PAD_CROSS;
    check(halyard_input_build_history_payload(&w, &s, buf, sizeof(buf)) == 0,
          "no transition emits nothing");

    /* Two transitions in one frame produce two events back to back. */
    halyard_input_writer_init(&w);
    w.have_previous = 1;
    s.buttons = HALYARD_PAD_CROSS | HALYARD_PAD_OPTIONS;
    n = halyard_input_build_history_payload(&w, &s, buf, sizeof(buf));
    check(n == 5, "cross (3-byte) + options (2-byte) = 5 bytes");
}

/*
 * History is CUMULATIVE and newest-first, and every packet re-sends the recent events. Leaving that out
 * made input unusable on hardware: a dropped packet loses a transition permanently, so a press whose
 * release never arrives leaves the console holding the button down.
 */
static void test_history_resend(void)
{
    halyard_input_writer w;
    halyard_input_state s;
    uint8_t buf[64];
    size_t n;

    halyard_input_writer_init(&w);
    memset(&s, 0, sizeof(s));
    w.have_previous = 1;

    /* Press cross: one event. */
    s.buttons = HALYARD_PAD_CROSS;
    n = halyard_input_build_history_payload(&w, &s, buf, sizeof(buf));
    check(n == 3, "first transition = 1 event");

    /* Release it: the new event leads, the earlier press follows. */
    w.previous.buttons = HALYARD_PAD_CROSS;
    s.buttons = 0;
    n = halyard_input_build_history_payload(&w, &s, buf, sizeof(buf));
    check(n == 6, "second transition re-sends the first = 6 bytes");
    check(buf[1] == 0x88 && buf[2] == 0x00, "newest event (release) is FIRST");
    check(buf[4] == 0x88 && buf[5] == 0xff, "the earlier press follows it");

    /* The list is bounded: after many transitions only the newest few are carried. */
    {
        int i;
        for (i = 0; i < 10; i++) {
            w.previous.buttons = (i % 2) ? HALYARD_PAD_CIRCLE : 0u;
            s.buttons = (i % 2) ? 0u : HALYARD_PAD_CIRCLE;
            n = halyard_input_build_history_payload(&w, &s, buf, sizeof(buf));
        }
        check(n == 3 * HALYARD_INPUT_HISTORY_RESEND, "history is capped at the resend count");
    }
}

static void test_header(void)
{
    uint8_t buf[16];
    size_t n = halyard_input_write_header(0x06, 0x1234, buf, sizeof(buf));

    check(n == 0x0c, "header is 12 bytes");
    check(buf[0] == 0x06, "type at offset 0");
    check(buf[1] == 0x12 && buf[2] == 0x34, "sequence is big-endian at 1..2");
    check(buf[3] == 0, "offset 3 is zero");
    /* Key position (4..7) and tag (8..11) belong to the sealer and must start clear. */
    check(buf[4] == 0 && buf[7] == 0 && buf[8] == 0 && buf[11] == 0,
          "key position and tag left for the sealer");
    check(halyard_input_write_header(0x06, 0, buf, 11) == 0, "short header buffer refused");
}

int main(void)
{
    test_state_payload();
    test_history_events();
    test_history_resend();
    test_header();
    printf("\n%d passed, %d failed\n", g_passed, g_failed);
    return g_failed == 0 ? 0 : 1;
}
