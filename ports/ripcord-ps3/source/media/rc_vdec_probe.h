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

#include <stdint.h>

/* The accepted profile_level values, in order, filled by rc_vdec_probe. b145 found 13 of them spanning
 * 10 to 42, which is exactly H.264's own level_idc set - but a count and a range are an inference, and
 * this is the list itself. */
#define RC_VDEC_LEVELS_MAX 32

typedef struct {
    int module_load;
    int levels[RC_VDEC_LEVELS_MAX];
    int level_count;          /* sysModuleLoad's return for the H.264 decoder module */
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

/*
 * THE SECOND QUESTION, and the one that decides whether cellVdec is usable at all: does it decode the
 * same bytes into the same pictures?
 *
 * This runs the console's decoder over the same capture rc_decode_probe feeds to openh264 and hashes the
 * output the same way - Y, then U, then V, one running FNV-1a per picture, via the same function. Equal
 * hashes mean the two decoders agree, which is the only evidence worth having before the live path is
 * moved onto a decoder nobody here has run.
 *
 * It is deliberately OFFLINE. Nothing in the streaming path changes, and a failure costs a run rather
 * than a session.
 *
 * EVERY STEP IS LOGGED AS IT IS ATTEMPTED, through the caller's own log function, and that is the whole
 * point rather than a convenience. b146 recorded the step in the struct below and printed it after the
 * call returned - which names nothing at all when the call never returns, which is precisely the case
 * the field existed for. The console hung and the last line in the log was the one printed before the
 * probe started. A record that only survives success is not instrumentation.
 *
 * rc_log flushes per line, so a line written before a call is on disk before that call can hang.
 */
typedef enum {
    RC_VDEC_STEP_NONE = 0,
    RC_VDEC_STEP_READ_FILE,
    RC_VDEC_STEP_QUERY_ATTR,
    RC_VDEC_STEP_ALLOC,
    RC_VDEC_STEP_OPEN,
    RC_VDEC_STEP_START_SEQUENCE,
    RC_VDEC_STEP_DECODE_AU,
    RC_VDEC_STEP_GET_PICTURE,
    RC_VDEC_STEP_END_SEQUENCE,
    RC_VDEC_STEP_CLOSE,
    RC_VDEC_STEP_DONE
} rc_vdec_step;

typedef struct {
    int      level;           /* the profile_level asked for */
    unsigned mem_size;        /* what vdecQueryAttr wanted for it */
    int      opened;
    int      aus_fed;
    int      pictures_out;
    int      width, height;
    uint64_t hash[8];         /* RC_DECODE_PROBE_FRAMES - same order, same function */
    int      hashes;
    int      last_error;      /* the library's own return from whatever failed */
    int      last_step;       /* rc_vdec_step - the last call ATTEMPTED */
    uint64_t decode_ticks;    /* feeding only; the hash is not in here */

    /*
     * THE FIRST PICTURE, PULLED APART. b151 proved the decoder works - 14 pictures, no errors - and that
     * its output does not hash the same as openh264's, with both reporting 640x360. The combined hash
     * cannot say why. These can: the luma hashed on its own, and the chroma hashed at two candidate
     * offsets, because the capture's SPS is 640x368 and a decoder that keeps 368 rows starts its U plane
     * at 640*368 while this probe was reading it at 640*360.
     */
    uint64_t hash_y;
    uint64_t hash_u_at_visible;  /* U assuming the planes follow the 360 visible rows */
    uint64_t hash_u_at_padded;   /* U assuming they follow the 368 coded rows */
    uint64_t hash_v_at_visible;
    uint64_t hash_v_at_padded;
    int      padded_height;      /* what the 16-aligned height would be */

    /*
     * THE STRIDE, DERIVED RATHER THAN ASSUMED. b152 showed the two decoders disagree on the LUMA, not
     * just the chroma, and printed the reason in passing: openh264's own strides are 704 and 352 for a
     * 640-wide picture. It pads, and this probe was reading vdec's output as though stride equalled
     * width. If vdec pads too then every row after the first is read misaligned, which corrupts Y and
     * makes any question about chroma offsets meaningless.
     *
     * No SDK header states the stride, so it is swept against openh264's known-good luma hash - the same
     * method that turned profile_level from a guess into the level_idc set. `matched_stride` is the one
     * that reproduces the reference, or 0 if nothing in the range does, which would mean the difference
     * is in the pixels rather than their arrangement.
     */
    int      matched_stride;
    int      matched_luma_rows;  /* rows of luma before the chroma begins, once the stride is known */
    unsigned picture_size;       /* what the decoder itself says the picture occupies */
    unsigned picture_attr;
    unsigned picture_status;
} rc_vdec_decode_result;

/* Decodes up to `max_frames` pictures from the Annex-B capture at `path` using the console's decoder.
 * Returns 1 if at least one picture came out. `out` is filled either way. */
/* Called with one already-formatted line per step attempted. Must write through to storage - see above. */
typedef void (*rc_vdec_log_fn)(const char *message);

/*
 * `reference_y` is openh264's luma hash for the first picture, used to identify the stride by sweep.
 * Pass 0 to skip that search.
 */
int rc_vdec_decode_probe(const char *path, int level, int max_frames, rc_vdec_log_fn log,
                         uint64_t reference_y, uint64_t reference_u, rc_vdec_decode_result *out);

#endif /* RC_VDEC_PROBE_H */
