/*
 * ripcord-ps3 - the console's own H.264 decoder (cellVdec) behind the live-decode seam.
 *
 * VALIDATED BEFORE IT WAS WIRED IN, which is the only reason it is here. b159 decoded the same capture
 * with this decoder and with openh264 and compared them plane by plane: Y, U and V are bit-identical
 * over all 230,400 luma bytes, and a third decoder (ffmpeg, on the development machine) produces the
 * same luma hash again. Three independent decoders agreeing is what makes it safe to put this in the
 * path that draws the screen.
 *
 * Two facts that the validation established and that this file depends on, neither of them stated in any
 * SDK header:
 *
 *   - vdecType.profile_level is H.264's level_idc. b145 swept 0..255 and the console accepted exactly
 *     10 11 12 13 20 21 22 30 31 32 40 41 42.
 *   - the output planes are packed at the DISPLAY size, Y then U then V, stride equal to width. The
 *     reported picture_size is larger (640x368x1.5 for a 640x360 picture) and is a buffer requirement
 *     rather than a description of the layout - b159 found U and V after 360 rows, not 368.
 *
 * And one that cost six builds: every buffer the decoder writes into must be 128-BYTE ALIGNED. This
 * hardware moves data in 128-byte units, and a misaligned destination corrupts the last bytes of every
 * row - visible as an 8-pixel strip down the right edge of the picture, which is exactly what b158 saw.
 */
#ifndef RC_DECODE_VDEC_H
#define RC_DECODE_VDEC_H

#include <stddef.h>
#include <stdint.h>

#include "rc_decode_probe.h"

/* 1 if the module loads and the decoder can be opened for a picture this size. */
int  rc_decode_vdec_open(int width, int height);
void rc_decode_vdec_set_sink(rc_decode_picture_fn fn, void *ctx);

/*
 * Submits one access unit and delivers at most one finished picture to the sink. Returns 1 if a picture
 * was delivered.
 *
 * DELIVERY HAPPENS HERE, ON THE CALLER'S THREAD, not in the library's callback. The callback collects
 * the picture into one of two buffers this file owns and marks it ready; this call picks it up. The sink
 * drives the SPE colour conversion and the RSX blit, and those have only ever run on the thread that
 * calls this - keeping them there is worth the flag. There is no copy: the buffers are ours, so the
 * handoff is a pointer and a flag.
 */
int  rc_decode_vdec_feed(const uint8_t *access_unit, size_t length, rc_decode_live_stats *stats);
void rc_decode_vdec_close(void);

/* What the decoder's own thread saw in the buffer immediately after writing it - see rc_decode_vdec.c. */
unsigned rc_decode_vdec_callback_luma_max(void);
unsigned rc_decode_vdec_callback_pictures(void);

#endif /* RC_DECODE_VDEC_H */
