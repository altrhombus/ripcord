/*
 * ripcord-ps3 - the on-screen diagnostics overlay. See the .c for what it may and may not do to the
 * memory it draws into.
 *
 * ORDER MATTERS AND IS THE CALLER'S PROBLEM. This writes into the back buffer directly, so it has to
 * run AFTER the picture has been put there and BEFORE the flip. With the RSX scaler that means after
 * the blit command has been queued - the RSX executes the buffer in order, so a draw issued from the
 * PPE afterwards can still land first. rc_video_flip's wait is what separates them.
 */
#ifndef RC_OVERLAY_H
#define RC_OVERLAY_H

#include <stdint.h>

#define RC_OVERLAY_WHITE  0x00E8E8E8u
#define RC_OVERLAY_DIM    0x00909090u
#define RC_OVERLAY_GOOD   0x0070D070u
#define RC_OVERLAY_WARN   0x00E0C040u
#define RC_OVERLAY_BAD    0x00E06060u

void rc_overlay_set(int on);
int  rc_overlay_on(void);

/* Paints the background for `lines` lines and resets the cursor. 0 if there is nothing to draw on. */
int  rc_overlay_begin(int lines);
void rc_overlay_line(uint32_t colour, const char *fmt, ...);
void rc_overlay_end(void);

#endif /* RC_OVERLAY_H */
