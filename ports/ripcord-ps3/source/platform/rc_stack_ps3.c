#include "rc_stack_ps3.h"

#include <ppu-lv2.h>
#include <sys/thread.h>

static const char *s_base;
static unsigned long s_size;

void rc_stack_probe_init(void)
{
    sys_ppu_thread_stack_t info;
    char here;

    s_base = &here;
    s_size = 0UL;

    /* lv2 reports the buffer, not the current pointer. A failure here is not fatal - the used figure
     * is still meaningful on its own, and a zero size says plainly that this is unknown. */
    if (sysThreadGetStackInformation(&info) == 0)
        s_size = (unsigned long)info.size;
}

void rc_stack_probe(unsigned long *out_size, unsigned long *out_used, unsigned long *out_headroom)
{
    char here;
    unsigned long used = 0UL;

    /* The stack grows down on this hardware, so the base recorded near the top of the program is the
     * higher address. Guarded rather than assumed: if the relationship ever inverts, a nonsense figure
     * is worse than none. */
    if (s_base != NULL && s_base > &here)
        used = (unsigned long)(s_base - &here);

    if (out_size != NULL)
        *out_size = s_size;
    if (out_used != NULL)
        *out_used = used;
    if (out_headroom != NULL)
        *out_headroom = (s_size > used) ? (s_size - used) : 0UL;
}
