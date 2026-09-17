/*
 * ripcord-ps3 - the background, and the one detail somebody will notice without being able to name it.
 *
 * THE XMB's WAVE IS THE MOST RECOGNISABLE THING ABOUT THIS CONSOLE'S SOFTWARE, and it is recognisable
 * precisely because it is restrained: very low contrast, very slow, and mostly empty. Anything louder
 * than that stops being this machine's background and starts being a screensaver.
 *
 * AND IT TAKES ITS HUE FROM THE MONTH, as the XMB's did. That is the whole trick: somebody who launches
 * this in October and again in July sees something different and cannot say what. It costs a table and
 * one call to localtime.
 *
 * WHAT THIS FILE IS NOT. It does not own a buffer, a display, or a frame rate. It fills a rectangle of
 * ARGB that somebody else allocated, and says how long it took - see rc_wave_last_us, which exists
 * because the answer decides whether the shell's motion can run at the flip rate or has to go to the
 * SPEs. That is a measurement to take on hardware, not a thing to guess at.
 */
#ifndef RC_WAVE_H
#define RC_WAVE_H

#include <stdint.h>

/*
 * Reads the clock and picks this month's colour. Call once; it is cheap but it is not free, and the
 * month is not going to change while somebody is looking at a menu.
 */
void rc_wave_open(void);

/* This month's accent, for the rest of the shell to agree with the background. Opaque ARGB. */
uint32_t rc_wave_accent(void);

/* The month's name, for the log - so a screenshot of a colour can be matched to a decision. */
const char *rc_wave_month(void);

/*
 * Fills `w` x `h` pixels at `dst`, stepping `stride_px` pixels a row, with the wave as it stands at
 * `ms` milliseconds. Writes every pixel: there is no background to preserve underneath.
 */
void rc_wave_draw(uint32_t *dst, int w, int h, int stride_px, uint64_t ms);

/*
 * THE SAME PICTURE, A ROW AT A TIME - for a caller that wants to composite something over each row
 * while it is still in cache rather than write the whole screen and then read it all back.
 *
 * rc_wave_begin does the frame's work and must be called first; rc_wave_row is then an expansion into
 * whatever buffer the caller gives it, which is expected to be one row long and to stay hot. `y` is a
 * row of the FULL-SIZE picture, and rows may be asked for in any order, though in practice they are
 * asked for in the order they are drawn.
 */
void rc_wave_begin(int w, int h, uint64_t ms);
void rc_wave_row(uint32_t *dst, int w, int y);

/*
 * THE SMALL BUFFER ITSELF, for a caller that can expand it faster than rc_wave_row can - an SPE. Valid
 * only between rc_wave_begin and the next one, 128-byte aligned with a row stride that is a multiple of
 * 16, because the MFC requires both. NULL before the first rc_wave_begin.
 *
 * The motes are NOT in it: they are drawn at full size, so a caller expanding the buffer itself has to
 * put them on afterwards with rc_wave_motes_row. rc_wave_row does both and needs neither call.
 */
const uint32_t *rc_wave_small(int *w, int *h, int *stride_px);
void rc_wave_motes_row(uint32_t *dst, int w, int y);

/* How long the last fill took. The number that decides what the next stage can afford. */
unsigned rc_wave_last_us(void);

#endif /* RC_WAVE_H */
