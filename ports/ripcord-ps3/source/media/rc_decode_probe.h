/*
 * ripcord-ps3 - does openh264 decode on the PPE, and does it produce the right pixels?
 *
 * Step 7's first hardware question. Everything established so far about this decoder was established
 * somewhere else: it compiles for the PPE, it links to a PowerPC64 executable, and it decodes this
 * project's own capture bit-exactly on big-endian PowerPC64 under qemu. None of that is the PPE. qemu is
 * a different processor model, glibc is not newlib, and a Linux process is not 256 MB of XDR with a
 * hypervisor underneath.
 *
 * So this reads the capture off the console's disk, runs it through openh264, and hashes each decoded
 * frame with FNV-1a - the same hash, over the same bytes in the same order, that the development machine
 * computed from the reference decode. Matching hashes mean the PS3 produced pixel-identical output to a
 * known-good decode of the same stream. Differing hashes localise the failure to a frame number.
 *
 * THE NAL SPLITTING IS OURS, NOT OPENH264'S, and that is deliberate. rc_h264_annexb.c has 142 host
 * checks behind it and is what the real client will use to find access-unit boundaries in a live stream;
 * feeding openh264 through it here exercises the seam that will exist later rather than a convenient one
 * that will not.
 */
#ifndef RC_DECODE_PROBE_H
#define RC_DECODE_PROBE_H

#include <stddef.h>
#include <stdint.h>

/* The implementation is C++ - openh264's ISVCDecoder is an abstract class, not a C handle - and every
 * caller in this port is C99. Without this the two disagree about linkage and the compiler says so. */
#ifdef __cplusplus
extern "C" {
#endif

#define RC_DECODE_PROBE_FRAMES 8

typedef struct {
    int      opened;                              /* the stream was found and read            */
    size_t   bytes;                               /* how much of it                           */
    int      initialised;                         /* openh264 accepted Initialize()           */
    int      nals_fed;                            /* NAL units handed to the decoder          */
    int      frames_out;                          /* frames it produced                       */
    int      width, height;                       /* as the decoder reported them             */
    uint64_t hash[RC_DECODE_PROBE_FRAMES];        /* FNV-1a 64 per frame, in output order     */
    int      hashes;                              /* how many of the above are filled         */
    int32_t  last_error;                          /* whatever failed, as openh264 reported it */
} rc_decode_probe_result;

/*
 * Decodes up to RC_DECODE_PROBE_FRAMES frames from the Annex-B stream at `path`. Returns 1 if the
 * decoder produced at least one frame, 0 otherwise; `out` is filled either way, because a run that
 * produced nothing still has to say how far it got.
 */
int rc_decode_probe(const char *path, rc_decode_probe_result *out);

/* The hash the caller compares against. Exposed so the development machine can compute it identically. */
uint64_t rc_decode_probe_hash_plane(const uint8_t *plane, int stride, int width, int height,
                                    uint64_t seed);

#ifdef __cplusplus
}
#endif

#endif /* RC_DECODE_PROBE_H */
