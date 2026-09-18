/*
 * ripcord-3ds - the launch spec (spec sec4.3), the JSON that configures the stream.
 *
 * Ported from HalyardStreamingSession.BuildLaunchSpecJson / BuildStreamResolutions. This is a
 * single-line UTF-8 JSON document with its keys in a fixed order, most of whose contents are constants
 * transcribed from the vendor's own decrypted launch spec - `"ps3AccessToken"`, `"bravia_tv"`,
 * `"psnId"` and friends are not placeholders this port failed to fill in, they are what the capture
 * contains and what the console accepts.
 *
 * THIS IS HOW handshakeKey REACHES THE CONSOLE, which is the only reason it is on the critical path.
 * The 16 random bytes that authenticate the ECDH exchange travel as the base64 `"handshakeKey"` member
 * of this document, encrypted under the CONTROL plane's key - so the stream plane's security depends on
 * a control-plane cipher, and a console that cannot decrypt this will simply never answer
 * SESSION_REQUEST. See takion_session_negotiator.h.
 *
 * THE CIPHER IS OFB AT COUNTER 0, WHICH DESERVES A WARNING. The document is encrypted with
 * halyard_control_streaminfo_crypt (AES-128-OFB under out1) at counter **0** - the same counter
 * `RP-Auth` already used with the CFB field cipher, so the two share an IV and their first keystream
 * blocks are identical. That is what the .NET reference does and what spec sec4.3 describes, but the
 * .NET side flags the counter value as NOT pinned by any captured vector. If a console accepts
 * /sess/ctrl and then silently never answers SESSION_REQUEST, this counter is a prime suspect and
 * trying the running counter instead is the first experiment.
 *
 * KNOWN DIVERGENCE FROM THE CAPTURE: the vendor's own launch spec carries
 * `"adaptiveStreamMode":"resize"` as the last key of `requestGameSpecification`; both this port and the
 * .NET side omit it. If SESSION_REPLY never arrives, adding it back is the first thing to try.
 */
#ifndef HALYARD_LAUNCH_SPEC_H
#define HALYARD_LAUNCH_SPEC_H

#include <stddef.h>
#include <stdint.h>

/* The document runs to roughly 1.1 KB before encryption; this is generous headroom for a wide ladder. */
#define HALYARD_LAUNCH_SPEC_MAX 2048
/* base64 grows by 4/3, plus padding and a NUL. */
#define HALYARD_LAUNCH_SPEC_B64_MAX ((HALYARD_LAUNCH_SPEC_MAX * 4 / 3) + 8)

typedef struct {
    int width;          /* requested video width  (3DS screen is 400x240, so 640x360 is the sane rung) */
    int height;         /* requested video height */
    int fps;            /* 30 or 60 */
    int bitrate_kbps;   /* network.bwKbpsSent */
    int mtu;            /* network.mtu - declared form, not the interface MTU (see the .c) */
    int rtt_ms;         /* network.rtt - 0 when senkusha produced no sample */
    int is_hevc;        /* 0 = "avc" (the only option on this hardware - MVD decodes H.264 only) */
    int is_hdr;         /* 0 = "SDR". "HDR" is an inference on the .NET side, never observed */
} halyard_launch_spec_params;

/*
 * Builds the plaintext JSON into `out`. Returns the length written (excluding the NUL), or 0 if the
 * buffer is too small. `handshake_key` is the 16 random bytes, embedded base64.
 *
 * `out` should be HALYARD_LAUNCH_SPEC_MAX bytes. Do not put one on the stack in a 3DS build - the frame
 * limit is 8 KB and this plus its base64 form is most of that on its own.
 */
size_t halyard_launch_spec_build(const halyard_launch_spec_params *params,
                                 const uint8_t handshake_key[16],
                                 char *out, size_t out_size);

/*
 * The stream-resolution ladder, exposed for testing: every standard rung strictly shorter than
 * `height`, then the requested size appended last so it always scores highest. Returns bytes written.
 */
size_t halyard_launch_spec_resolutions(int width, int height, int fps, char *out, size_t out_size);

#endif /* HALYARD_LAUNCH_SPEC_H */
