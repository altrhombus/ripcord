/*
 * ripcord-ps3 - H.264 bit reader and parameter-set self-test.
 *
 * THIS BUILDS FOR THE HOST, NOT THE PS3 - see tests/Makefile. There is no console involved and no PSL1GHT;
 * the bitstream code is portable C and there is no reason to need hardware to find out it is wrong.
 *
 * TWO KINDS OF VECTOR, AND THE SPLIT IS DELIBERATE.
 *
 * The SPS and PPS below are the real ones, transcribed byte for byte out of the elementary stream the
 * console sent (captures/3ds/video.264, written by ripcord-3ds's dumpvideo=1). That is legitimate ground
 * truth and not a self-consistency shortcut - the same argument fec_test.c makes for the console's own
 * dumped inverse table. A parameter set is encoder configuration: profile, level, resolution, frame
 * numbering. It is identical for every console and every account, carries nothing tied to either, and its
 * decoded values are already published in docs/protocol/ps5-av-stream.md. Checking the parser against
 * synthesised bytes only would prove it self-consistent and prove nothing about the stream it exists for.
 *
 * Slice headers are synthesised instead, by the little bit writer below. Not because synthetic is better
 * here - real would be better - but because a slice NAL's bytes ARE captured payload, and
 * ps5-av-stream.md's redaction note says none of that is reproduced. The writer costs forty lines and
 * buys more coverage than the two slices that could have been lifted: field combinations this console
 * never sends are exactly the ones a parser gets wrong.
 *
 * WHAT IS NOT TESTED HERE. The slice parser stops at redundant_pic_cnt by design, so nothing below
 * exercises reference list modification, prediction weights or the marking process - there is nothing to
 * exercise. rc_h264_params.h says why.
 */
#include "../source/media/rc_h264_bits.h"
#include "../source/media/rc_h264_params.h"

#include <stdio.h>
#include <string.h>

static int g_passed;
static int g_failed;

#define CHECK(cond, ...) do { \
    if (cond) { \
        g_passed++; \
    } else { \
        g_failed++; \
        printf("FAIL %s:%d: ", __FILE__, __LINE__); \
        printf(__VA_ARGS__); \
        printf("\n"); \
    } \
} while (0)

/* ---------------------------------------------------------------------------------------------------
 * The console's own parameter sets, NAL header byte stripped (0x67 and 0x68 respectively).
 * ------------------------------------------------------------------------------------------------ */

static const uint8_t k_real_sps[] = {
    0x4d, 0x40, 0x1f, 0x91, 0x8a, 0x02, 0x80, 0xbf, 0xe5, 0xc0, 0x5a, 0x83, 0x03, 0x03,
    0x20, 0x00, 0x01, 0xf4, 0x80, 0x00, 0x75, 0x30, 0x11, 0x34, 0x38, 0x74, 0x54
};

static const uint8_t k_real_pps[] = { 0xee, 0x3c, 0x80 };

/* ---------------------------------------------------------------------------------------------------
 * A minimal Exp-Golomb bit writer, so slice headers can be built rather than captured.
 * ------------------------------------------------------------------------------------------------ */

typedef struct {
    uint8_t buf[64];
    size_t bits;
} bitwriter;

static void bw_init(bitwriter *w)
{
    memset(w, 0, sizeof(*w));
}

static void bw_u(bitwriter *w, unsigned n, uint32_t value)
{
    unsigned i;
    for (i = 0u; i < n; i++) {
        unsigned bit = (value >> (n - 1u - i)) & 1u;
        size_t byte = w->bits >> 3;
        unsigned off = (unsigned)(w->bits & 7u);
        if (byte >= sizeof(w->buf)) {
            return;
        }
        w->buf[byte] = (uint8_t)(w->buf[byte] | (bit << (7u - off)));
        w->bits++;
    }
}

static void bw_ue(bitwriter *w, uint32_t value)
{
    uint32_t k = value + 1u;
    unsigned m = 0u;
    uint32_t t = k;
    while (t != 0u) { m++; t >>= 1; }
    bw_u(w, m - 1u, 0u);
    bw_u(w, m, k);
}

static void bw_se(bitwriter *w, int32_t value)
{
    bw_ue(w, (value > 0) ? ((uint32_t)value * 2u - 1u) : ((uint32_t)(-value) * 2u));
}

static size_t bw_bytes(const bitwriter *w)
{
    return (w->bits + 7u) / 8u;
}

/* ---------------------------------------------------------------------------------------------------
 * Bit reader
 * ------------------------------------------------------------------------------------------------ */

static void test_bit_reader_basics(void)
{
    /* 1010 1100 0011 0000 */
    static const uint8_t d[] = { 0xac, 0x30 };
    rc_h264_bits br;
    uint32_t v;
    int flag;

    rc_h264_bits_init(&br, d, sizeof(d));
    CHECK(rc_h264_bits_u(&br, 1u, &v) && v == 1u, "first bit is MSB-first, got %u", v);
    CHECK(rc_h264_bits_u(&br, 3u, &v) && v == 2u, "next three bits should be 010, got %u", v);
    CHECK(rc_h264_bits_u(&br, 4u, &v) && v == 0xcu, "low nibble of 0xac should be 0xc, got %u", v);
    CHECK(rc_h264_bits_u(&br, 8u, &v) && v == 0x30u, "second byte should read whole, got 0x%x", v);
    CHECK(rc_h264_bits_consumed(&br) == 16u, "consumed should be 16, got %zu", rc_h264_bits_consumed(&br));

    /* One bit past the end must fail, and stay failed. */
    CHECK(!rc_h264_bits_u(&br, 1u, &v), "read past end should fail");
    CHECK(v == 0u, "a failed read must still define its output");
    CHECK(!rc_h264_bits_ok(&br), "failure should be sticky");
    CHECK(!rc_h264_bits_flag(&br, &flag), "every read after a failure should fail too");
}

static void test_exp_golomb(void)
{
    /* ue: 1 -> 0, 010 -> 1, 011 -> 2, 00100 -> 3, 00101 -> 4 ... packed into bytes.
     * 1 010 011 00100 = 1010 0110 0100 ...  -> 0xa6 0x40 */
    static const uint8_t d[] = { 0xa6, 0x40 };
    rc_h264_bits br;
    uint32_t v;
    int32_t s;
    unsigned i;

    rc_h264_bits_init(&br, d, sizeof(d));
    CHECK(rc_h264_bits_ue(&br, &v) && v == 0u, "ue(1) should be 0, got %u", v);
    CHECK(rc_h264_bits_ue(&br, &v) && v == 1u, "ue(010) should be 1, got %u", v);
    CHECK(rc_h264_bits_ue(&br, &v) && v == 2u, "ue(011) should be 2, got %u", v);
    CHECK(rc_h264_bits_ue(&br, &v) && v == 3u, "ue(00100) should be 3, got %u", v);

    /* se mapping (sec 9.1.1): codeNum 0,1,2,3,4 -> 0,+1,-1,+2,-2. Round-trip through the writer. */
    {
        static const int32_t cases[] = { 0, 1, -1, 2, -2, 7, -7, 1000, -1000 };
        for (i = 0u; i < sizeof(cases) / sizeof(cases[0]); i++) {
            bitwriter w;
            rc_h264_bits rb;
            bw_init(&w);
            bw_se(&w, cases[i]);
            rc_h264_bits_init(&rb, w.buf, bw_bytes(&w));
            CHECK(rc_h264_bits_se(&rb, &s) && s == cases[i],
                  "se round-trip failed for %d, got %d", cases[i], s);
        }
    }

    /* A run of zeros longer than any legal code must fail rather than walk the buffer. */
    {
        static const uint8_t zeros[8] = { 0, 0, 0, 0, 0, 0, 0, 0 };
        rc_h264_bits rb;
        rc_h264_bits_init(&rb, zeros, sizeof(zeros));
        CHECK(!rc_h264_bits_ue(&rb, &v), "an unterminated ue must fail, not loop to the end");
    }
}

static void test_emulation_prevention(void)
{
    /* 0x00,0x00,0x03,0x01 - the 0x03 is an inserted emulation-prevention byte and must not be read,
     * so the reader should see three bytes whose last is 0x01. */
    static const uint8_t d[] = { 0x00, 0x00, 0x03, 0x01 };
    rc_h264_bits br;
    uint32_t v;

    rc_h264_bits_init(&br, d, sizeof(d));
    CHECK(rc_h264_bits_u(&br, 8u, &v) && v == 0x00u, "byte 0");
    CHECK(rc_h264_bits_u(&br, 8u, &v) && v == 0x00u, "byte 1");
    CHECK(rc_h264_bits_u(&br, 8u, &v) && v == 0x01u,
          "0x03 after 00 00 must be skipped, got 0x%02x", v);
    CHECK(rc_h264_bits_consumed(&br) == 24u,
          "consumed counts payload bits only, got %zu", rc_h264_bits_consumed(&br));
    CHECK(!rc_h264_bits_u(&br, 1u, &v), "nothing should remain after the skipped byte");

    /* A 0x03 after only ONE zero byte is real data and must be read. This is the direction the naive
     * implementation gets wrong: tracking "was the last byte zero" instead of "were the last two". */
    {
        static const uint8_t e[] = { 0x00, 0x03, 0x00 };
        rc_h264_bits rb;
        rc_h264_bits_init(&rb, e, sizeof(e));
        CHECK(rc_h264_bits_u(&rb, 8u, &v) && v == 0x00u, "byte 0");
        CHECK(rc_h264_bits_u(&rb, 8u, &v) && v == 0x03u,
              "0x03 after a single 00 is data, got 0x%02x", v);
        CHECK(rc_h264_bits_u(&rb, 8u, &v) && v == 0x00u, "byte 2");
    }

    /* Two escapes in a row: 0x00,0x00,0x03,0x00,0x00,0x03,0x05 flattens to five bytes ending 0x05. The
     * zero counter must reset after
     * each skip, or the second 0x03 is consumed as data. */
    {
        static const uint8_t f[] = { 0x00, 0x00, 0x03, 0x00, 0x00, 0x03, 0x05 };
        rc_h264_bits rb;
        unsigned i;
        rc_h264_bits_init(&rb, f, sizeof(f));
        for (i = 0u; i < 4u; i++) {
            CHECK(rc_h264_bits_u(&rb, 8u, &v) && v == 0x00u, "zero byte %u", i);
        }
        CHECK(rc_h264_bits_u(&rb, 8u, &v) && v == 0x05u,
              "second escape should also be skipped, got 0x%02x", v);
    }
}

/* ---------------------------------------------------------------------------------------------------
 * The real parameter sets
 * ------------------------------------------------------------------------------------------------ */

static rc_h264_sps g_sps;
static rc_h264_pps g_pps;

static void test_real_sps(void)
{
    CHECK(rc_h264_sps_parse(k_real_sps, sizeof(k_real_sps), &g_sps), "the console's SPS should parse");

    /* Every value below is also recorded in docs/protocol/ps5-av-stream.md. If one changes here, that
     * document is wrong too - they are the same measurement. */
    CHECK(g_sps.profile_idc == 77u, "profile_idc should be 77 (Main), got %u", g_sps.profile_idc);
    CHECK(g_sps.constraint_flags == 0x40u, "constraint flags should be 0x40, got 0x%02x",
          g_sps.constraint_flags);
    CHECK(g_sps.level_idc == 31u, "level_idc should be 31 (3.1), got %u", g_sps.level_idc);
    CHECK(g_sps.sps_id == 0u, "sps_id should be 0, got %u", g_sps.sps_id);
    CHECK(g_sps.chroma_format_idc == 1u, "4:2:0 expected, got chroma_format_idc %u",
          g_sps.chroma_format_idc);
    CHECK(g_sps.frame_mbs_only_flag == 1, "progressive expected");
    CHECK(g_sps.separate_colour_plane_flag == 0, "no separate colour planes expected");
    CHECK(g_sps.coded_width == 640u, "coded width should be 640, got %u", g_sps.coded_width);
    CHECK(g_sps.coded_height == 368u, "coded height should be 368, got %u", g_sps.coded_height);

    /* Main profile has no chroma block, so these must come from the defaults rather than the bitstream. */
    CHECK(g_sps.bit_depth_luma == 8u && g_sps.bit_depth_chroma == 8u, "8-bit expected");

    /* pic_order_cnt_type 2 means picture order IS decode order: no pic_order_cnt_lsb is coded at all,
     * and no reordering is representable. That is consistent with the I-and-P-only slice types measured,
     * and it removes a whole class of decoder state. frame_num is 7 bits, so it wraps at 128. */
    CHECK(g_sps.pic_order_cnt_type == 2u, "pic_order_cnt_type should be 2, got %u",
          g_sps.pic_order_cnt_type);
    CHECK(g_sps.log2_max_frame_num == 7u, "frame_num should be 7 bits, got %u",
          g_sps.log2_max_frame_num);
    CHECK(g_sps.frame_cropping_flag == 1, "640x368 coded is cropped to 640x360 for display");

    printf("  SPS: profile=%u level=%u %ux%u  log2_max_frame_num=%u poc_type=%u "
           "log2_max_poc_lsb=%u max_ref=%u crop=%d vui=%d\n",
           g_sps.profile_idc, g_sps.level_idc, g_sps.coded_width, g_sps.coded_height,
           g_sps.log2_max_frame_num, g_sps.pic_order_cnt_type, g_sps.log2_max_pic_order_cnt_lsb,
           g_sps.max_num_ref_frames, g_sps.frame_cropping_flag, g_sps.vui_parameters_present_flag);
}

static void test_real_pps(void)
{
    CHECK(rc_h264_pps_parse(k_real_pps, sizeof(k_real_pps), &g_pps), "the console's PPS should parse");
    CHECK(g_pps.pps_id == 0u, "pps_id should be 0, got %u", g_pps.pps_id);
    CHECK(g_pps.sps_id == 0u, "PPS should reference sps_id 0, got %u", g_pps.sps_id);
    CHECK(g_pps.entropy_coding_mode_flag == 1, "CABAC expected - this is the finding the port turns on");
    CHECK(g_pps.num_slice_groups_minus1 == 0u, "one slice group expected");

    printf("  PPS: cabac=%d qp=%d deblock_ctrl=%d redundant=%d ref_idx_l0=%u\n",
           g_pps.entropy_coding_mode_flag, g_pps.pic_init_qp,
           g_pps.deblocking_filter_control_present_flag, g_pps.redundant_pic_cnt_present_flag,
           g_pps.num_ref_idx_l0_default_active_minus1);
}

static void test_rejects_malformed(void)
{
    rc_h264_sps sps;
    rc_h264_pps pps;
    uint8_t truncated[4];

    CHECK(!rc_h264_sps_parse(k_real_sps, 2u, &sps), "a truncated SPS should be refused");
    CHECK(!rc_h264_pps_parse(k_real_pps, 1u, &pps), "a truncated PPS should be refused");
    CHECK(!rc_h264_sps_parse(NULL, 0u, &sps), "a null payload should be refused");

    /* REFUSED: FMO. Build a PPS with num_slice_groups_minus1 == 1 and confirm it stops rather than
     * guessing at slice-group map syntax. */
    {
        bitwriter w;
        bw_init(&w);
        bw_ue(&w, 0u);      /* pps_id */
        bw_ue(&w, 0u);      /* sps_id */
        bw_u(&w, 1u, 1u);   /* entropy_coding_mode_flag */
        bw_u(&w, 1u, 0u);   /* bottom_field_pic_order_in_frame_present_flag */
        bw_ue(&w, 1u);      /* num_slice_groups_minus1 = 1 -> FMO */
        CHECK(!rc_h264_pps_parse(w.buf, bw_bytes(&w), &pps),
              "FMO should be refused rather than mis-parsed");
    }

    memset(truncated, 0, sizeof(truncated));
    CHECK(!rc_h264_sps_parse(truncated, sizeof(truncated), &sps),
          "an all-zero buffer is not a parameter set");
}

/* ---------------------------------------------------------------------------------------------------
 * Slice headers, built against the real SPS/PPS above
 * ------------------------------------------------------------------------------------------------ */

static void build_slice_header(bitwriter *w, uint32_t first_mb, uint32_t slice_type,
                               uint32_t frame_num, int is_idr, uint32_t idr_pic_id,
                               uint32_t poc_lsb)
{
    bw_init(w);
    bw_ue(w, first_mb);
    bw_ue(w, slice_type);
    bw_ue(w, 0u);                                   /* pic_parameter_set_id */
    bw_u(w, g_sps.log2_max_frame_num, frame_num);
    if (is_idr) {
        bw_ue(w, idr_pic_id);
    }
    if (g_sps.pic_order_cnt_type == 0u) {
        bw_u(w, g_sps.log2_max_pic_order_cnt_lsb, poc_lsb);
    }
    /* bottom_field_pic_order_in_frame_present_flag and redundant_pic_cnt_present_flag are both 0 in the
     * real PPS, so nothing follows that this parser reads. */
}

static void test_slice_headers(void)
{
    bitwriter w;
    rc_h264_slice_header sh;
    rc_h264_slice_header prev;

    /* An IDR slice: slice_type 7 is "I, and all slices in this picture are I". */
    build_slice_header(&w, 0u, 7u, 0u, 1, 0u, 0u);
    CHECK(rc_h264_slice_header_parse(w.buf, bw_bytes(&w), 3u, RC_H264_NAL_IDR_SLICE,
                                     &g_sps, &g_pps, &sh), "IDR slice header should parse");
    CHECK(sh.first_mb_in_slice == 0u, "first_mb_in_slice");
    CHECK(sh.slice_type == 7u && sh.slice_type_base == RC_H264_SLICE_I,
          "slice_type 7 should reduce to I, got base %u", sh.slice_type_base);
    CHECK(sh.is_idr == 1, "nal_unit_type 5 means IDR");
    CHECK(sh.nal_ref_idc == 3u, "nal_ref_idc should be carried through");

    /* A non-IDR P slice, second slice of the same picture. */
    build_slice_header(&w, 40u, 5u, 1u, 0, 0u, 2u);
    CHECK(rc_h264_slice_header_parse(w.buf, bw_bytes(&w), 2u, RC_H264_NAL_SLICE,
                                     &g_sps, &g_pps, &sh), "P slice header should parse");
    CHECK(sh.first_mb_in_slice == 40u, "first_mb_in_slice should be 40, got %u", sh.first_mb_in_slice);
    CHECK(sh.slice_type_base == RC_H264_SLICE_P, "slice_type 5 should reduce to P, got %u",
          sh.slice_type_base);
    CHECK(sh.is_idr == 0, "nal_unit_type 1 is not IDR");
    CHECK(sh.frame_num == 1u, "frame_num should be 1, got %u", sh.frame_num);

    /* A mismatched PPS/SPS pair is a caller error and must be refused, not mis-parsed. */
    {
        rc_h264_pps wrong = g_pps;
        wrong.sps_id = 7u;
        CHECK(!rc_h264_slice_header_parse(w.buf, bw_bytes(&w), 2u, RC_H264_NAL_SLICE,
                                          &g_sps, &wrong, &sh),
              "a PPS naming a different SPS should be refused");
    }
    CHECK(!rc_h264_slice_header_parse(w.buf, bw_bytes(&w), 2u, RC_H264_NAL_SLICE,
                                      NULL, &g_pps, &sh), "a null SPS should be refused");

    /* Picture boundaries. Two slices of one picture, then a third that starts a new one. */
    build_slice_header(&w, 0u, 5u, 4u, 0, 0u, 8u);
    CHECK(rc_h264_slice_header_parse(w.buf, bw_bytes(&w), 2u, RC_H264_NAL_SLICE,
                                     &g_sps, &g_pps, &prev), "first slice parses");
    CHECK(rc_h264_slice_begins_new_picture(&prev, NULL, &g_sps),
          "the first slice of a stream always begins a picture");

    build_slice_header(&w, 60u, 5u, 4u, 0, 0u, 8u);
    CHECK(rc_h264_slice_header_parse(w.buf, bw_bytes(&w), 2u, RC_H264_NAL_SLICE,
                                     &g_sps, &g_pps, &sh), "second slice parses");
    CHECK(!rc_h264_slice_begins_new_picture(&sh, &prev, &g_sps),
          "same frame_num and POC means the same picture, even though first_mb is non-zero");

    build_slice_header(&w, 0u, 5u, 5u, 0, 0u, 10u);
    CHECK(rc_h264_slice_header_parse(w.buf, bw_bytes(&w), 2u, RC_H264_NAL_SLICE,
                                     &g_sps, &g_pps, &sh), "third slice parses");
    CHECK(rc_h264_slice_begins_new_picture(&sh, &prev, &g_sps),
          "a new frame_num begins a new picture");

    /* The POC comparison in rc_h264_slice_begins_new_picture is INERT for this console: it sends
     * pic_order_cnt_type 2, so no pic_order_cnt_lsb is coded and picture order is decode order. It is
     * kept because a stream we have not measured may use type 0 or 1. (An earlier version of this test
     * asserted a POC-driven boundary here and failed for exactly that reason - the assertion was wrong,
     * not the code, and the SPS is what said so.)
     *
     * The sec 7.4.1.2.4 condition that IS exercisable here is the reference/non-reference transition:
     * a disposable slice cannot belong to the same picture as a reference one. */
    build_slice_header(&w, 30u, 5u, 4u, 0, 0u, 0u);
    CHECK(rc_h264_slice_header_parse(w.buf, bw_bytes(&w), 0u, RC_H264_NAL_SLICE,
                                     &g_sps, &g_pps, &sh), "fourth slice parses");
    CHECK(rc_h264_slice_begins_new_picture(&sh, &prev, &g_sps),
          "nal_ref_idc dropping to zero begins a new picture even at the same frame_num");
}

int main(void)
{
    test_bit_reader_basics();
    test_exp_golomb();
    test_emulation_prevention();
    test_real_sps();
    test_real_pps();
    test_rejects_malformed();
    test_slice_headers();

    printf("\n%d passed, %d failed\n", g_passed, g_failed);
    return g_failed == 0 ? 0 : 1;
}
