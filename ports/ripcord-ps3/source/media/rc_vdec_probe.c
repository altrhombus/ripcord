#include "rc_vdec_probe.h"

#include <codec/vdec.h>
#include <sysmodule/sysmodule.h>

#include <string.h>

/*
 * The sweep. 0..255 covers any plausible encoding of the value - a level_idc, a small enum, or a packed
 * profile/level byte - without this file having to assume which of those it is. The point is to come back
 * with the set the console accepts, not to confirm a guess.
 *
 * vdecQueryAttr only asks how much memory a configuration would need. It allocates nothing, starts
 * nothing and can be called before any decoder exists, which is what makes a sweep reasonable here.
 */
#define RC_VDEC_SWEEP_MAX 256

int rc_vdec_probe(rc_vdec_probe_result *out)
{
    int level;

    if (out == NULL)
        return 0;

    memset(out, 0, sizeof(*out));
    out->first_accepted = -1;
    out->last_accepted = -1;
    out->largest_mem_level = -1;

    out->module_load = (int)sysModuleLoad(SYSMODULE_VDEC_H264);
    if (out->module_load != 0)
        return 0;

    for (level = 0; level < RC_VDEC_SWEEP_MAX; level++) {
        vdecType type;
        vdecAttr attr;
        s32 rc;

        memset(&type, 0, sizeof(type));
        memset(&attr, 0, sizeof(attr));
        type.codec_type = VDEC_CODEC_TYPE_H264;
        type.profile_level = (u32)level;

        out->queried++;
        rc = vdecQueryAttr(&type, &attr);
        if (rc != 0) {
            out->last_error = (int)rc;
            continue;
        }

        out->accepted++;
        if (out->first_accepted < 0) {
            out->first_accepted = level;
            out->first_mem_size = (unsigned)attr.mem_size;
            out->cmd_depth = (unsigned)attr.cmd_depth;
            out->ver_major = (unsigned)attr.ver_major;
            out->ver_minor = (unsigned)attr.ver_minor;
        }
        out->last_accepted = level;
        if ((unsigned)attr.mem_size > out->largest_mem_size) {
            out->largest_mem_size = (unsigned)attr.mem_size;
            out->largest_mem_level = level;
        }
    }

    return 1;
}
