#include "rc_soc.h"

#include <malloc.h>
#include <stdlib.h>

static u32 *s_soc_buffer = NULL;

int rc_soc_init(void)
{
    if (s_soc_buffer != NULL)
        return 0;

    /* memalign, not malloc: socInit hard-requires 0x1000 alignment. Getting this wrong links and boots
     * fine and only misbehaves under load - exactly the "drops during keyframe bursts" trap SETUP.md
     * warns about, which is why it is asserted here rather than left to the caller to remember. */
    s_soc_buffer = (u32 *)memalign(0x1000, RC_SOC_BUFFER_SIZE);
    if (s_soc_buffer == NULL)
        return -1;

    Result rc = socInit(s_soc_buffer, RC_SOC_BUFFER_SIZE);
    if (R_FAILED(rc)) {
        free(s_soc_buffer);
        s_soc_buffer = NULL;
        return (int)rc;
    }
    return 0;
}

void rc_soc_exit(void)
{
    if (s_soc_buffer == NULL)
        return;

    socExit();
    free(s_soc_buffer);
    s_soc_buffer = NULL;
}

struct in_addr rc_soc_local_address(void)
{
    struct in_addr addr;
    addr.s_addr = (in_addr_t)gethostid();
    return addr;
}
