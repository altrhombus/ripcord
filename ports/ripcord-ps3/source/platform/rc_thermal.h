/*
 * ripcord-ps3 - the Cell and RSX thermal sensors, read through lv2.
 *
 * WHY THIS EXISTS. Moving the colour conversion and scaling off the SPEs and on to the RSX is an
 * argument about power before it is an argument about speed: the claim is that a fixed-function blit
 * unit doing what three SIMD cores were doing in software draws less, and that cutting the Cell-to-RSX
 * transfer from 8.3 MB a frame to 3.7 lowers interconnect power on top of it. Both are reasonable and
 * neither has been weighed on this hardware. One fan serves both chips, so "moved the heat to the RSX"
 * is not by itself a win, and the fan curve is coarse enough that a few watts may not move it at all.
 *
 * A BASELINE HAS TO BE TAKEN BEFORE THE CHANGE, WHICH IS WHY THIS LANDS FIRST. There is no way to
 * measure what the SPE path costs once the SPE path is gone.
 *
 * THE DECODING IS UNCONFIRMED AND IS REPORTED RAW BESIDE IT. Syscall 383 takes a device selector and
 * the address of a word; the temperature is understood to be in that word's top byte, in whole degrees
 * Celsius. That understanding is not from a capture and not from anything this project derived, so the
 * raw word is logged next to the decoded value and one run settles whether the reading is sane. The
 * same reasoning as the video_state field in rc_video_ps3.c: record it, do not judge it.
 *
 * The syscall is not part of the retail application ABI, so it can simply fail. Everything here answers
 * 0 when it does and no caller changes behaviour on that.
 */
#ifndef RC_THERMAL_H
#define RC_THERMAL_H

#define RC_THERMAL_CELL 0u
#define RC_THERMAL_RSX  1u

/* One sensor. Returns 1 and fills both outputs on success, 0 on failure (outputs untouched). */
int rc_thermal_read(unsigned device, unsigned *celsius, unsigned *raw);

/*
 * The running record. rc_thermal_sample() is cheap enough to call once a second from the session loop;
 * it keeps the first, the highest and the most recent reading for each sensor.
 */
typedef struct {
    int      available;      /* 0 if the syscall never answered - the rest is then meaningless */
    unsigned samples;
    unsigned cell_first, cell_peak, cell_last;
    unsigned rsx_first,  rsx_peak,  rsx_last;
    unsigned cell_raw_last, rsx_raw_last;
} rc_thermal_record;

void rc_thermal_reset(rc_thermal_record *rec);
void rc_thermal_sample(rc_thermal_record *rec);

#endif /* RC_THERMAL_H */
