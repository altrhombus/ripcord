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
 * THE ANNEX-B SECTIONS BUILD THEIR STREAMS OUT OF BOTH. A test stream is the real parameter sets, wrapped
 * back into NAL units with start codes, interleaved with synthesised slices - so the splitter is exercised
 * against the byte patterns the console actually emits while the picture-boundary rule is exercised against
 * field combinations the console never sends.
 *
 * WHAT IS NOT TESTED HERE. The slice parser stops at redundant_pic_cnt by design, so nothing below
 * exercises reference list modification, prediction weights or the marking process - there is nothing to
 * exercise. rc_h264_params.h says why.
 */
#include "../source/media/rc_h264_bits.h"
#include "../source/media/rc_h264_annexb.h"
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

/*
 * rbsp_trailing_bits (sec 7.3.2.11): a one bit, then zeros to the byte boundary.
 *
 * Real NAL units carry it and these fixtures did not, which was harmless while they were handed straight
 * to a parser. It stops being harmless once they go through the Annex-B splitter, because the splitter
 * trims trailing zero bytes - so a header whose last byte happened to hold only zero bits would lose that
 * byte and the parse would run off the end. The stop bit guarantees the final byte is non-zero, which is
 * exactly the property the real syntax exists to provide.
 */
static void bw_trailing_bits(bitwriter *w)
{
    bw_u(w, 1u, 1u);
    while ((w->bits & 7u) != 0u) {
        bw_u(w, 1u, 0u);
    }
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

/*
 * A MINIMAL BASELINE SPS THAT REACHES THE VUI, built rather than captured.
 *
 * The colour signalling (video_full_range_flag, matrix_coefficients) decides whether cellVdec's fixed
 * limited-range assumption is right, and a stream that says "full range" and is expanded as limited
 * blows its highlights - the sand-dune symptom that sent us to read this field at all. The parser walks
 * the VUI exactly as far as those fields; nothing captured exercises that walk, so it is built here.
 *
 * profile_idc 66 (Baseline) has no chroma block, which keeps the builder short: the parser skips
 * straight from sps_id to log2_max_frame_num. pic_order_cnt_type 2 and frame_mbs_only 1 skip their
 * optional sub-blocks too. `vst`/`full_range`/`colour`/`matrix` shape the VUI; `vui=0` omits it entirely.
 */
static void build_min_sps(bitwriter *w, int vui, int vst, int full_range, int colour, int matrix)
{
    bw_init(w);
    bw_u(w, 8u, 66u);        /* profile_idc = Baseline (no chroma block) */
    bw_u(w, 8u, 0u);         /* constraint_flags */
    bw_u(w, 8u, 30u);        /* level_idc 3.0 */
    bw_ue(w, 0u);            /* sps_id */
    bw_ue(w, 0u);            /* log2_max_frame_num_minus4 */
    bw_ue(w, 2u);            /* pic_order_cnt_type 2 - no extra fields */
    bw_ue(w, 1u);            /* max_num_ref_frames */
    bw_u(w, 1u, 0u);         /* gaps_in_frame_num_value_allowed_flag */
    bw_ue(w, 79u);           /* pic_width_in_mbs_minus1  -> 1280 */
    bw_ue(w, 44u);           /* pic_height_in_map_units_minus1 -> 720, frame_mbs_only */
    bw_u(w, 1u, 1u);         /* frame_mbs_only_flag -> no mb_adaptive field */
    bw_u(w, 1u, 1u);         /* direct_8x8_inference_flag */
    bw_u(w, 1u, 0u);         /* frame_cropping_flag = 0 */
    bw_u(w, 1u, (uint32_t)(vui ? 1 : 0));   /* vui_parameters_present_flag */

    if (vui) {
        bw_u(w, 1u, 0u);     /* aspect_ratio_info_present_flag */
        bw_u(w, 1u, 0u);     /* overscan_info_present_flag */
        bw_u(w, 1u, (uint32_t)(vst ? 1 : 0));   /* video_signal_type_present_flag */
        if (vst) {
            bw_u(w, 3u, 5u); /* video_format = unspecified */
            bw_u(w, 1u, (uint32_t)(full_range ? 1 : 0));
            bw_u(w, 1u, (uint32_t)(colour ? 1 : 0));   /* colour_description_present_flag */
            if (colour) {
                bw_u(w, 8u, 1u);                       /* colour_primaries (BT.709) */
                bw_u(w, 8u, 1u);                       /* transfer_characteristics */
                bw_u(w, 8u, (uint32_t)matrix);         /* matrix_coefficients */
            }
        }
    }
    bw_trailing_bits(w);
}

static void test_vui_colour(void)
{
    bitwriter w;
    rc_h264_sps sps;

    /* Full range, BT.709 matrix, stated outright - the case that matters most, because it is the one
     * cellVdec's limited-range assumption gets WRONG. */
    build_min_sps(&w, 1, 1, 1, 1, 1);
    CHECK(rc_h264_sps_parse(w.buf, bw_bytes(&w), &sps), "a baseline SPS with a full VUI should parse");
    CHECK(sps.video_signal_type_present_flag == 1, "video_signal_type should be seen");
    CHECK(sps.video_full_range_flag == 1, "full range should be read as 1, got %d",
          sps.video_full_range_flag);
    CHECK(sps.colour_description_present_flag == 1, "colour description should be seen");
    CHECK(sps.matrix_coefficients == 1, "BT.709 matrix should be 1, got %d", sps.matrix_coefficients);
    CHECK(sps.coded_width == 1280u && sps.coded_height == 720u, "the builder's size should round-trip");

    /* Limited range, and 0 is a REAL value, not absence - the distinction the -1 default exists for. */
    build_min_sps(&w, 1, 1, 0, 1, 1);
    CHECK(rc_h264_sps_parse(w.buf, bw_bytes(&w), &sps), "limited-range SPS should parse");
    CHECK(sps.video_full_range_flag == 0, "limited range should be read as 0, got %d",
          sps.video_full_range_flag);

    /* A VUI whose video_signal_type is absent: the colour fields stay "not stated" (-1), never 0. A
     * caller must be able to tell "the stream said limited" from "the stream said nothing". */
    build_min_sps(&w, 1, 0, 0, 0, 0);
    CHECK(rc_h264_sps_parse(w.buf, bw_bytes(&w), &sps), "SPS with a VUI but no video_signal_type parses");
    CHECK(sps.video_full_range_flag == -1, "absent range must stay -1 (not stated), got %d",
          sps.video_full_range_flag);
    CHECK(sps.matrix_coefficients == -1, "absent matrix must stay -1, got %d", sps.matrix_coefficients);

    /* No VUI at all: same "not stated" defaults, and the parse still succeeds - the colour fields are
     * diagnostic, and the coded size callers depend on was known before the VUI. */
    build_min_sps(&w, 0, 0, 0, 0, 0);
    CHECK(rc_h264_sps_parse(w.buf, bw_bytes(&w), &sps), "SPS with no VUI should still parse");
    CHECK(sps.vui_parameters_present_flag == 0, "no VUI expected");
    CHECK(sps.video_full_range_flag == -1 && sps.matrix_coefficients == -1,
          "no VUI means not stated, not zero");
    CHECK(sps.coded_width == 1280u, "the coded size is still read without a VUI");
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
    bw_trailing_bits(w);
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

/* ---------------------------------------------------------------------------------------------------
 * Annex-B splitting
 * ------------------------------------------------------------------------------------------------ */

typedef struct {
    uint8_t buf[4096];
    size_t len;
} annexb_builder;

static void ab_init(annexb_builder *s)
{
    memset(s, 0, sizeof(*s));
}

static void ab_raw(annexb_builder *s, const uint8_t *bytes, size_t n)
{
    size_t i;
    for (i = 0u; i < n && s->len < sizeof(s->buf); i++) {
        s->buf[s->len++] = bytes[i];
    }
}

static void ab_start_code(annexb_builder *s, size_t length)
{
    static const uint8_t k_sc[4] = { 0x00u, 0x00u, 0x00u, 0x01u };
    ab_raw(s, k_sc + (4u - length), length);
}

/* One NAL unit: start code of the requested length, the header byte, then the payload. */
static void ab_nal(annexb_builder *s, size_t sc_length, uint8_t header,
                   const uint8_t *payload, size_t n)
{
    ab_start_code(s, sc_length);
    ab_raw(s, &header, 1u);
    ab_raw(s, payload, n);
}

static void test_annexb_splitting(void)
{
    static const uint8_t k_leading_junk[] = { 0xde, 0xad, 0x00, 0x02, 0xbe, 0xef };
    static const uint8_t k_two_zeros[] = { 0x00u, 0x00u };
    static const uint8_t k_payload_a[] = { 0x11, 0x22, 0x33 };
    static const uint8_t k_payload_b[] = { 0x44 };
    annexb_builder s;
    rc_h264_annexb it;
    rc_h264_nal nal;

    /* An empty buffer, and a buffer with no start code in it at all, both yield nothing rather than
     * inventing a unit out of whatever the caller handed over. */
    rc_h264_annexb_init(&it, NULL, 0u);
    CHECK(!rc_h264_annexb_next(&it, &nal), "a null buffer yields no NAL units");
    rc_h264_annexb_init(&it, k_leading_junk, sizeof(k_leading_junk));
    CHECK(!rc_h264_annexb_next(&it, &nal), "a buffer with no start code yields no NAL units");

    ab_init(&s);
    ab_raw(&s, k_leading_junk, sizeof(k_leading_junk));   /* bytes before the first start code */
    ab_nal(&s, 4u, 0x67u, k_payload_a, sizeof(k_payload_a));
    ab_raw(&s, k_two_zeros, sizeof(k_two_zeros));         /* cabac_zero_words after the first unit */
    ab_nal(&s, 3u, 0x21u, k_payload_b, sizeof(k_payload_b));

    rc_h264_annexb_init(&it, s.buf, s.len);

    CHECK(rc_h264_annexb_next(&it, &nal), "first NAL should be found past the leading junk");
    CHECK(nal.type == 7u, "first NAL type should be 7 (SPS), got %u", nal.type);
    CHECK(nal.ref_idc == 3u, "first NAL nal_ref_idc should be 3, got %u", nal.ref_idc);
    CHECK(nal.forbidden_zero == 0, "forbidden_zero_bit should be clear");
    CHECK(nal.size == sizeof(k_payload_a) + 1u,
          "trailing zeros must not be counted into the unit: expected %u bytes, got %u",
          (unsigned)(sizeof(k_payload_a) + 1u), (unsigned)nal.size);
    CHECK(nal.payload_size == sizeof(k_payload_a) && nal.payload[0] == 0x11u,
          "payload should start after the header byte");

    CHECK(rc_h264_annexb_next(&it, &nal), "second NAL should be found after a 3-byte start code");
    CHECK(nal.type == 1u && nal.ref_idc == 1u, "second NAL header should decode to type 1, ref_idc 1");
    CHECK(nal.size == 2u, "second NAL should be 2 bytes, got %u", (unsigned)nal.size);

    CHECK(!rc_h264_annexb_next(&it, &nal), "the buffer is exhausted");
    CHECK(it.empty_units == 0u, "no empty units in a well-formed buffer, got %u",
          (unsigned)it.empty_units);

    /* Back-to-back start codes are not NAL units. They are counted rather than silently swallowed,
     * because a run of them means the buffer is damaged. */
    ab_init(&s);
    ab_start_code(&s, 4u);
    ab_start_code(&s, 4u);
    ab_nal(&s, 4u, 0x41u, k_payload_b, sizeof(k_payload_b));
    rc_h264_annexb_init(&it, s.buf, s.len);
    CHECK(rc_h264_annexb_next(&it, &nal) && nal.type == 1u, "the real unit is still found");
    CHECK(!rc_h264_annexb_next(&it, &nal), "and it is the only one");
    CHECK(it.empty_units >= 1u, "empty units should be counted, got %u", (unsigned)it.empty_units);

    /* The forbidden_zero_bit is set, which sec 7.4.1 says cannot happen - so this is corruption, and the
     * splitter reports it rather than the access-unit layer having to re-derive it. */
    ab_init(&s);
    ab_nal(&s, 4u, 0xe5u, k_payload_b, sizeof(k_payload_b));
    rc_h264_annexb_init(&it, s.buf, s.len);
    CHECK(rc_h264_annexb_next(&it, &nal) && nal.forbidden_zero == 1,
          "a set forbidden_zero_bit should be reported");
}

/* ---------------------------------------------------------------------------------------------------
 * Access units and picture boundaries
 * ------------------------------------------------------------------------------------------------ */

/* Appends a slice NAL built against the real parameter sets. */
static void ab_slice(annexb_builder *s, uint8_t header, uint32_t first_mb, uint32_t slice_type,
                     uint32_t frame_num, int is_idr, uint32_t idr_pic_id)
{
    bitwriter w;
    build_slice_header(&w, first_mb, slice_type, frame_num, is_idr, idr_pic_id, 0u);
    ab_nal(s, 4u, header, w.buf, bw_bytes(&w));
}

static void test_access_units(void)
{
    annexb_builder s;
    rc_h264_annexb it;
    rc_h264_nal nal;
    rc_h264_au_nal out;

    /* Static, not a local, and the header says why: this structure is twenty kilobytes. ripcord-3ds has
     * a comment in exactly this shape because its main thread had a 32 KB stack. */
    static rc_h264_au au;

    ab_init(&s);
    ab_nal(&s, 4u, 0x67u, k_real_sps, sizeof(k_real_sps));
    ab_nal(&s, 3u, 0x68u, k_real_pps, sizeof(k_real_pps));
    ab_slice(&s, 0x65u, 0u,  7u, 0u, 1, 0u);   /* IDR, first slice of picture 1 */
    ab_slice(&s, 0x65u, 60u, 7u, 0u, 1, 0u);   /* IDR, second slice of the same picture */
    ab_slice(&s, 0x41u, 0u,  5u, 1u, 0, 0u);   /* P, picture 2 - opens its own access unit */
    ab_nal(&s, 4u, 0x67u, k_real_sps, sizeof(k_real_sps));   /* opens access unit 3 */
    ab_nal(&s, 4u, 0x68u, k_real_pps, sizeof(k_real_pps));
    ab_slice(&s, 0x41u, 0u,  5u, 2u, 0, 0u);   /* P, picture 3 - inside the unit the SPS opened */

    rc_h264_au_init(&au);
    rc_h264_annexb_init(&it, s.buf, s.len);

    /* 1. SPS. Nothing has been fed, so this opens the first access unit. */
    CHECK(rc_h264_annexb_next(&it, &nal), "SPS unit present");
    CHECK(rc_h264_au_feed(&au, &nal, &out) == RC_H264_AU_NON_VCL, "an SPS is non-VCL");
    CHECK(out.begins_access_unit == 1, "the first NAL of the stream opens an access unit");
    CHECK(out.sps != NULL && out.sps->coded_width == 640u, "the SPS should be absorbed and readable");

    /* 2. PPS, still inside that unit. */
    CHECK(rc_h264_annexb_next(&it, &nal), "PPS unit present");
    CHECK(rc_h264_au_feed(&au, &nal, &out) == RC_H264_AU_NON_VCL, "a PPS is non-VCL");
    CHECK(out.begins_access_unit == 0, "a PPS following an SPS does not open a second unit");
    CHECK(out.pps != NULL && out.pps->entropy_coding_mode_flag == 1, "the PPS should be absorbed");

    /* 3. The IDR slice opens the picture but NOT the access unit - the SPS already did. This is the
     * distinction sec 7.4.1.2.3 forces and the reason the two flags are separate. */
    CHECK(rc_h264_annexb_next(&it, &nal), "first IDR slice present");
    CHECK(rc_h264_au_feed(&au, &nal, &out) == RC_H264_AU_SLICE, "an IDR NAL is a slice");
    CHECK(out.begins_picture == 1, "the first slice of the stream begins a picture");
    CHECK(out.begins_access_unit == 0, "but the SPS in front of it already opened the access unit");
    CHECK(out.slice.is_idr == 1 && out.slice.slice_type_base == RC_H264_SLICE_I, "IDR, I slice");

    /* 4. Second slice of the same picture. */
    CHECK(rc_h264_annexb_next(&it, &nal), "second IDR slice present");
    CHECK(rc_h264_au_feed(&au, &nal, &out) == RC_H264_AU_SLICE, "still a slice");
    CHECK(out.begins_picture == 0, "same frame_num and idr_pic_id: the same picture continues");
    CHECK(out.slice.first_mb_in_slice == 60u, "and it is not the first macroblock");

    /* 5. A P slice with no parameter sets in front of it: it opens both the picture and the unit. */
    CHECK(rc_h264_annexb_next(&it, &nal), "P slice present");
    CHECK(rc_h264_au_feed(&au, &nal, &out) == RC_H264_AU_SLICE, "a P slice");
    CHECK(out.begins_picture == 1, "a new frame_num begins a picture");
    CHECK(out.begins_access_unit == 1, "with nothing in front of it, the slice opens the unit too");

    /* 6-7. Parameter sets repeated mid-stream. The SPS closes the previous unit and opens a new one. */
    CHECK(rc_h264_annexb_next(&it, &nal), "repeated SPS present");
    CHECK(rc_h264_au_feed(&au, &nal, &out) == RC_H264_AU_NON_VCL, "non-VCL");
    CHECK(out.begins_access_unit == 1, "a parameter set after a slice opens the next access unit");
    CHECK(rc_h264_annexb_next(&it, &nal), "repeated PPS present");
    CHECK(rc_h264_au_feed(&au, &nal, &out) == RC_H264_AU_NON_VCL, "non-VCL");
    CHECK(out.begins_access_unit == 0, "the PPS is inside the unit the SPS opened");

    /* 8. And its slice begins a picture inside that already-open unit. */
    CHECK(rc_h264_annexb_next(&it, &nal), "final P slice present");
    CHECK(rc_h264_au_feed(&au, &nal, &out) == RC_H264_AU_SLICE, "a P slice");
    CHECK(out.begins_picture == 1, "frame_num moved again");
    CHECK(out.begins_access_unit == 0, "the repeated SPS opened this unit, not the slice");

    CHECK(!rc_h264_annexb_next(&it, &nal), "stream exhausted");
    CHECK(au.pictures == 3u, "three pictures expected, got %u", (unsigned)au.pictures);
    CHECK(au.access_units == 3u, "three access units expected, got %u", (unsigned)au.access_units);
    CHECK(au.slices == 4u, "four slices expected, got %u", (unsigned)au.slices);
    CHECK(au.dropped_no_params == 0u, "nothing should have been dropped");

    printf("  stream: %u bytes -> %u access units, %u pictures, %u slices\n",
           (unsigned)s.len, (unsigned)au.access_units, (unsigned)au.pictures, (unsigned)au.slices);
    printf("  sizeof(rc_h264_au) = %u bytes - static or heap, not a PPU thread stack\n",
           (unsigned)sizeof(au));
}

static void test_access_unit_refusals(void)
{
    static const uint8_t k_payload[] = { 0x88, 0x80 };
    annexb_builder s;
    rc_h264_annexb it;
    rc_h264_nal nal;
    rc_h264_au_nal out;
    static rc_h264_au au;

    /* A slice before its parameter sets is NEED_PARAMS, not an error. A client joining a stream in
     * progress, or one that lost the fragment carrying them, sees exactly this. */
    ab_init(&s);
    ab_slice(&s, 0x41u, 0u, 5u, 3u, 0, 0u);
    rc_h264_au_init(&au);
    rc_h264_annexb_init(&it, s.buf, s.len);
    CHECK(rc_h264_annexb_next(&it, &nal), "slice present");
    CHECK(rc_h264_au_feed(&au, &nal, &out) == RC_H264_AU_NEED_PARAMS,
          "a slice with no parameter sets should ask for them, not fail");
    CHECK(au.dropped_no_params == 1u, "and should be counted as dropped");
    CHECK(au.pictures == 0u && au.slices == 0u, "a dropped slice is not a picture");

    /* REFUSED: data partitioning. Partitions B and C carry no slice header, so grouping cannot see
     * them - refusing is the only answer that fails loudly instead of quietly. */
    ab_init(&s);
    ab_nal(&s, 4u, 0x42u, k_payload, sizeof(k_payload));   /* nal_unit_type 2 - partition A */
    rc_h264_au_init(&au);
    rc_h264_annexb_init(&it, s.buf, s.len);
    CHECK(rc_h264_annexb_next(&it, &nal) && nal.type == 2u, "partition A present");
    CHECK(rc_h264_au_feed(&au, &nal, &out) == RC_H264_AU_ERROR, "data partitioning should be refused");

    /* Corruption reported by the splitter is refused rather than parsed. */
    ab_init(&s);
    ab_nal(&s, 4u, 0xe7u, k_real_sps, sizeof(k_real_sps));
    rc_h264_au_init(&au);
    rc_h264_annexb_init(&it, s.buf, s.len);
    CHECK(rc_h264_annexb_next(&it, &nal), "unit present");
    CHECK(rc_h264_au_feed(&au, &nal, &out) == RC_H264_AU_ERROR,
          "a set forbidden_zero_bit means the bytes are not trustworthy");
    CHECK(au.sps_valid[0] == 0u, "and nothing from it should have been stored");
}

int main(void)
{
    test_bit_reader_basics();
    test_exp_golomb();
    test_emulation_prevention();
    test_real_sps();
    test_vui_colour();
    test_real_pps();
    test_rejects_malformed();
    test_slice_headers();
    test_annexb_splitting();
    test_access_units();
    test_access_unit_refusals();

    printf("\n%d passed, %d failed\n", g_passed, g_failed);
    return g_failed == 0 ? 0 : 1;
}
