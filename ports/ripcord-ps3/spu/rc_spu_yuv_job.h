/*
 * ripcord-ps3 - the job block for SPE colour conversion, shared by both halves.
 *
 * Like rc_spu_phase.h this contains no code and includes nothing but stdint, which is what makes it safe
 * to share between two architectures with two toolchains.
 *
 * SIZE IS A MULTIPLE OF 16 AND DELIBERATELY SO. The MFC refuses transfers whose size is not 1, 2, 4, 8 or
 * a multiple of 16, and IBM's Cell Broadband Engine Programmers Guide gives a worked example of a control
 * block that fails for exactly this reason. This is 64 bytes exactly; anything added must keep it so, and
 * the guide's advice is to pad explicitly rather than to count on the fields happening to line up.
 */
#ifndef RC_SPU_YUV_JOB_H
#define RC_SPU_YUV_JOB_H

#include <stdint.h>

/* How many rows one SPE converts per DMA round trip. Two, because 4:2:0 chroma is shared by a row PAIR -
 * converting an odd number would either re-fetch a chroma row or use the wrong one. */
#define RC_SPU_YUV_ROWS_PER_PASS 2u

/* The widest SOURCE a line buffer is sized for. 1920 covers a 1080p stream; the PPE refuses anything
 * wider rather than overrunning local store, which is 256 KB for everything including this program. */
#define RC_SPU_YUV_MAX_WIDTH 1920u

/* The widest OUTPUT. The display is whatever mode the television negotiated, and the source is whatever
 * the console agreed to send - those will rarely be the same, which is why scaling is not an alternative
 * to choosing a resolution but a permanent part of the path. */
#define RC_SPU_YUV_MAX_DST_WIDTH 1920u

/*
 * The strip is now described in OUTPUT rows, and the planes are given at row zero rather than
 * pre-offset. With scaling there is no longer a fixed relationship between a strip's first output row
 * and a source row - the SPE computes it - so offsetting the pointers on the PPE would be doing the same
 * arithmetic in two places and getting to disagree about it.
 *
 * 80 bytes, a multiple of 16, which the MFC requires of a transfer size. Anything added must keep it so.
 */
typedef struct {
    uint64_t y_ea;           /* luma plane at row 0                                          */
    uint64_t u_ea;           /* chroma planes at row 0                                       */
    uint64_t v_ea;
    uint64_t dst_ea;         /* first output pixel of THIS strip, inside the display buffer   */
    uint64_t done_ea;        /* where to write the sequence when the strip is finished        */
    uint32_t y_stride;
    uint32_t uv_stride;
    uint32_t dst_stride;     /* bytes per output line - the display pitch, not width * 4      */
    uint32_t src_width;
    uint32_t src_height;
    uint32_t dst_width;
    uint32_t dst_height;     /* of the whole output rect, not this strip - the vertical map
                              * is computed against the full picture or the strips will not
                              * join up                                                      */
    uint32_t first_dst_row;  /* this strip's first row within that rect                       */
    uint32_t dst_rows;       /* rows in this strip                                            */
    uint32_t sequence;

    /*
     * 1 when y_ea points at a packed 32-bit RGB picture rather than three planes, in which case the
     * source row is DMA'd straight into the line buffer and the colour conversion is skipped entirely.
     *
     * The decoder can produce this itself - VDEC_PICFMT_ARGB32, confirmed accepted and filled in b179 -
     * and b185 measured why it is worth taking: of the SPE's time, 1,667 us a frame goes on waiting for
     * the MFC and 16,444 us on converting and scaling. The conversion is the cost, not the transfers.
     *
     * The packing matches: convert_line writes 0x00RRGGBB and the decoder's ARGB32 is 0xAARRGGBB in the
     * same byte order, so the alpha lands in the byte the display ignores.
     */
    uint32_t source_argb;
    uint32_t pad[3];         /* keeps this a multiple of 16, which the MFC requires - see the header */
} rc_spu_yuv_job;

#endif /* RC_SPU_YUV_JOB_H */
