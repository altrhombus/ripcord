#include "halyard_control_arm.h"

#include <string.h>

void halyard_control_arm_build_probe(int is_ps5, char out[HALYARD_CONTROL_ARM_PROBE_SIZE])
{
    memcpy(out, is_ps5 ? "SRC3" : "SRC2", HALYARD_CONTROL_ARM_PROBE_SIZE);
}

int halyard_control_arm_is_reply(int is_ps5, const uint8_t *data, size_t length)
{
    const char *expect = is_ps5 ? "RES3" : "RES2";

    if (length < HALYARD_CONTROL_ARM_PROBE_SIZE)
        return 0;
    return memcmp(data, expect, HALYARD_CONTROL_ARM_PROBE_SIZE) == 0;
}

