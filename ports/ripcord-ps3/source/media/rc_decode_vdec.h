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

/*
 * THE SLICE COUNT THIS DECODER STOPS COPING WITH, bracketed by measurement rather than documented
 * anywhere. 65 slices in a 720p picture decode perfectly; 136 produce black pictures with no error
 * reported by anything. 128 is the obvious candidate for the real limit and 136 is just past it, but the
 * only two points actually measured are 65 and 136, so the warning fires between them rather than at a
 * number nobody has tested.
 *
 * The console slices so that each network unit decodes independently, which means slice count follows
 * units per frame and therefore the bandwidth we declare in the launch spec. That is the lever: this is
 * a reason to declare less, not a reason to decode differently.
 */
#define RC_VDEC_SLICES_WARN 96u

/* 1 if the module loads and the decoder can be opened for a picture this size. */
int  rc_decode_vdec_open(int width, int height);
void rc_decode_vdec_set_sink(rc_decode_picture_fn fn, void *ctx);

/* Setting this switches the decoder's output format to ARGB32 at the next open. */
void rc_decode_vdec_set_rgb_sink(rc_decode_picture_rgb_fn fn, void *ctx);

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
int rc_decode_vdec_level(void);
int rc_decode_vdec_num_spus(void);   /* SPEs the decoder was opened with */
unsigned rc_decode_vdec_mem_size(void);
int rc_decode_vdec_sps_profile(void);
int rc_decode_vdec_sps_level(void);     /* as the stream declared it, before any clamp */
int rc_decode_vdec_sps_max_ref(void);   /* level 4.2 allows 4 reference frames at 1080p */
unsigned rc_decode_vdec_level_clamped(void);
int rc_decode_vdec_clamp_safe(void);
unsigned rc_decode_vdec_au_bad_start(void);  /* submitted units not beginning with a start code */
unsigned rc_decode_vdec_au_largest(void);
unsigned rc_decode_vdec_au_max_nals(void);    /* most NAL units seen in one access unit */
unsigned rc_decode_vdec_au_max_slices(void);  /* most coded slices - the shape a decoder cares about */
unsigned rc_decode_vdec_au_last_slices(void); /* and the most recent, which is what shows a change */
unsigned rc_decode_vdec_drop_ring_full(void); /* submissions refused because no slot was free */
unsigned rc_decode_vdec_drop_submit(void);    /* refused by the decoder itself, after waiting */
int      rc_decode_vdec_drop_submit_error(void);
unsigned rc_decode_vdec_submit_waits(void);   /* submissions that had to wait for a queue slot */
unsigned rc_decode_vdec_drop_collect(void);   /* a picture announced and not collectable */
unsigned rc_decode_vdec_pictures_overwritten(void); /* decoded, then replaced before it was taken */

/* 1 if a frame has been lost since the last call, which breaks the reference chain and needs a keyframe
 * to repair. Reading it clears it, so the caller asks once per loss rather than once per poll. */
int rc_decode_vdec_take_chain_broken(void);
unsigned rc_decode_vdec_first_nal_types(void);   /* would the stream's reference frames fit level 4.2's DPB */
unsigned rc_decode_vdec_picture_addr(void);
unsigned rc_decode_vdec_first_au(uint8_t out[8]);   /* returns its length */

/*
 * Decode into RSX local memory rather than main memory, so the RSX can scale the picture where it lies
 * instead of something copying it there first. Ask before open; ..._picture_in_vram reports what was
 * actually obtained, since the request can be refused and decoding still has to work.
 *
 * NOTHING ON THE PPE MAY SAMPLE THE PICTURE PER FRAME once this is on - Cell reads from that memory are
 * roughly two orders of magnitude slower than writes.
 */
void rc_decode_vdec_want_vram(int on);
int rc_decode_vdec_picture_in_vram(void);

/* The delivered picture's RSX offset. Valid only inside a sink callback; 0 when it is in main memory. */
uint32_t rc_decode_vdec_delivered_offset(void);

#endif /* RC_DECODE_VDEC_H */