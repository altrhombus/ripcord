/* See rc_thermal.h - especially the note on the unconfirmed decoding. */
#include "rc_thermal.h"

#include <ppu-lv2.h>
#include <stddef.h>
#include <stdint.h>

/*
 * 383 is the temperature syscall. It is called through the raw lv2syscall2 macro rather than a PSL1GHT
 * wrapper because PSL1GHT does not wrap it - grep of ppu/include finds no temperature anywhere.
 */
#define RC_SYSCALL_GET_TEMPERATURE 383

/*
 * The lv2syscall macros DECLARE the argument registers, so they have to open a function rather than
 * appear in an expression - PSL1GHT's own wrappers all have this shape, and writing it as
 * `rc = lv2syscall2(...)` does not compile. The result comes back in p1, which return_to_user_prog
 * names.
 */
LV2_SYSCALL rc_sys_get_temperature(u64 device, u64 word_ea)
{
    lv2syscall2(RC_SYSCALL_GET_TEMPERATURE, device, word_ea);
    return_to_user_prog(s32);
}

int rc_thermal_read(unsigned device, unsigned *celsius, unsigned *raw)
{
    unsigned int word = 0u;
    int rc;

    if (celsius == NULL || raw == NULL)
        return 0;

    rc = (int)rc_sys_get_temperature((u64)device, (u64)(uintptr_t)&word);
    if (rc != 0)
        return 0;

    /*
     * Top byte, whole degrees - see the header. A reading outside 0..127 is refused rather than
     * reported: it would mean the decoding is wrong, and a wrong number in a thermal log is worse than
     * no number, because it is the kind of thing that gets quoted later.
     */
    *raw = word;
    *celsius = (word >> 24) & 0xffu;
    if (*celsius > 127u)
        return 0;
    return 1;
}

void rc_thermal_reset(rc_thermal_record *rec)
{
    unsigned i;
    unsigned char *p;

    if (rec == NULL)
        return;
    p = (unsigned char *)rec;
    for (i = 0u; i < sizeof(*rec); i++)
        p[i] = 0u;
}

static void note(unsigned value, unsigned *first, unsigned *peak, unsigned *last, unsigned samples)
{
    if (samples == 0u)
        *first = value;
    if (value > *peak)
        *peak = value;
    *last = value;
}

void rc_thermal_sample(rc_thermal_record *rec)
{
    unsigned cell_c = 0u, rsx_c = 0u, cell_raw = 0u, rsx_raw = 0u;

    if (rec == NULL)
        return;
    if (!rc_thermal_read(RC_THERMAL_CELL, &cell_c, &cell_raw))
        return;
    if (!rc_thermal_read(RC_THERMAL_RSX, &rsx_c, &rsx_raw))
        return;

    note(cell_c, &rec->cell_first, &rec->cell_peak, &rec->cell_last, rec->samples);
    note(rsx_c, &rec->rsx_first, &rec->rsx_peak, &rec->rsx_last, rec->samples);
    rec->cell_raw_last = cell_raw;
    rec->rsx_raw_last = rsx_raw;
    rec->samples++;
    rec->available = 1;
}
