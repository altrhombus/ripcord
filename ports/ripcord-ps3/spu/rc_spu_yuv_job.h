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

/* The widest picture a strip buffer is sized for. 1280 covers 720p; the PPE refuses anything wider rather
 * than overrunning local store, which is 256 KB for everything including this program. */
#define RC_SPU_YUV_MAX_WIDTH 1280u

typedef struct {
    uint64_t y_ea;        /* luma plane, at the first row of THIS strip                */
    uint64_t u_ea;        /* chroma planes, likewise, already offset for the strip     */
    uint64_t v_ea;
    uint64_t dst_ea;      /* first output pixel of the strip, inside the display buffer */
    uint64_t done_ea;     /* where to write RC_SPU_PHASE_DONE when the strip is finished */
    uint32_t y_stride;
    uint32_t uv_stride;
    uint32_t dst_stride;  /* bytes per output line - the display pitch, not width * 4   */
    uint32_t width;       /* pixels per line                                            */
    uint32_t rows;        /* lines in this strip; always even                           */
    uint32_t sequence;    /* incremented per frame, so a stale done flag cannot be read
                           * as this frame's - see the PPE side                         */
} rc_spu_yuv_job;

#endif /* RC_SPU_YUV_JOB_H */
