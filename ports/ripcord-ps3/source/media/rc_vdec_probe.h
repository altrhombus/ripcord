/*
 * ripcord-ps3 - a probe for the console's own H.264 decoder (cellVdec, via PSL1GHT's codec/vdec.h).
 *
 * WHY A PROBE BEFORE AN IMPLEMENTATION. b144 established that openh264 on the PPE is the ceiling: with
 * the network finally clean the console tripled its bitrate, frames went from 8.2 KB to 23.3 KB, and the
 * measured cost per successful decode rose to somewhere around 70-83 ms - a 12-14 fps limit on a 30 fps
 * stream. The hardware decoder is the way out, and it runs on the SPEs rather than the PPE.
 *
 * The obstacle is that vdecType.profile_level has no constant anywhere in the SDK headers. It is a number
 * the library expects and that this project has no derivation for. Guessing it from recall is exactly the
 * failure mode CLAUDE.md names - fluent, confident specificity about a value nobody checked - so this
 * sweeps the space instead and lets the console say which values it accepts. That is a derivation.
 *
 * The same probe reports the memory each accepted level wants, which sizes the allocation the real
 * implementation will need, and whether the module loads at all.
 *
 * Nothing here decodes anything. It queries, and it is safe to run before the decoder exists.
 */
#ifndef RC_VDEC_PROBE_H
#define RC_VDEC_PROBE_H

typedef struct {
    int module_load;          /* sysModuleLoad's return for the H.264 decoder module */
    int queried;              /* how many profile_level values were tried */
    int accepted;             /* how many vdecQueryAttr accepted */
    int first_accepted;       /* the lowest accepted value, -1 if none */
    int last_accepted;        /* the highest accepted value, -1 if none */
    unsigned first_mem_size;  /* what the lowest accepted value asks for */
    unsigned largest_mem_size;/* the largest any accepted value asks for */
    int largest_mem_level;    /* which value that was */
    unsigned cmd_depth;       /* from the lowest accepted value */
    unsigned ver_major;
    unsigned ver_minor;
    int last_error;           /* the error the last REJECTED query returned */
} rc_vdec_probe_result;

/* Loads the module and sweeps profile_level. Returns 1 if the module loaded, whatever the sweep found. */
int rc_vdec_probe(rc_vdec_probe_result *out);

#endif /* RC_VDEC_PROBE_H */
