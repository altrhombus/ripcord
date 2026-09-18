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
 * THE DECODING, SETTLED BY b202. Syscall 383 takes a device selector and the address of a word. That
 * word came back 0x3FC30000 for the Cell and 0x40400000 for the RSX on an idle-warm console, which is
 * 8.8 FIXED POINT in its top half - 63.76 C and 64.25 C - not whole degrees in the top byte as this
 * file first assumed. Both plausible, and the fraction is why the guess was visible: a top byte alone
 * would have left the low half unexplained. Tenths are reported because the sensor supplies them and
 * the difference this exists to measure may be small. The raw word is still logged beside them.
 *
 * IT COSTS ABOUT 14 ms A READ, which is the other thing b202 settled and by far the more important.
 * This is a hypervisor round trip to a hardware sensor, not a register read. Sampling both sensors once
 * a second from the session loop stalled the drain for 4.4 seconds, lost 3,896 units of 4,175 and put
 * 4 fps on the screen. NOTHING HERE MAY BE CALLED FROM THE A/V PATH, at any interval. The two callers
 * are on either side of the hold, where the cost lands outside everything being measured.
 *
 * The syscall is not part of the retail application ABI, so it can simply fail. Everything here answers
 * 0 when it does and no caller changes behaviour on that.
 */
#ifndef RC_THERMAL_H
#define RC_THERMAL_H

#define RC_THERMAL_CELL 0u
#define RC_THERMAL_RSX  1u

/*
 * One sensor. Returns 1 and fills both outputs on success, 0 on failure (outputs untouched).
 * celsius_x10 is TENTHS of a degree - 638 is 63.8 C - because the sensor reports a fraction.
 */
int rc_thermal_read(unsigned device, unsigned *celsius_x10, unsigned *raw);

/*
 * The running record. rc_thermal_sample() keeps the first and the most recent reading for each sensor.
 * See the cost note above before adding a third call site.
 */
typedef struct {
    int      available;      /* 0 if the syscall never answered - the rest is then meaningless */
    unsigned samples;
    /* All TENTHS of a degree. No peak field: with a sample either side of the hold, last IS the peak. */
    unsigned cell_first, cell_last;
    unsigned rsx_first,  rsx_last;
    unsigned cell_raw_last, rsx_raw_last;
} rc_thermal_record;

void rc_thermal_reset(rc_thermal_record *rec);
void rc_thermal_sample(rc_thermal_record *rec);

#endif /* RC_THERMAL_H */
