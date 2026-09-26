/*
 * The C core behind a narrow, allocation-owning surface for engine/ripcord-diff. Every struct stays on
 * this side of the boundary, so the Rust tests never mirror a C layout; they pass bytes and get bytes and
 * events back. Nothing here is protocol logic: each function forwards to libripcord unchanged.
 */
#include "../../../libripcord/stream/stream_demux.h"
#include "../../../libripcord/stream/stream_packet_crypto.h"
#include "../../../libripcord/stream/fec_reed_solomon.h"
#include "../../../libripcord/stream/fec_galois.h"
#include "../../../libripcord/halyard/halyard_v1.h"
#include "../../../libripcord/halyard/halyard_registration.h"
#include "../../../libripcord/halyard/halyard_account_seed.h"

#include <stdlib.h>
#include <string.h>

/* ---- packet crypto ---- */

void *diff_pc_new(const uint8_t key[16], const uint8_t iv[16])
{
    stream_packet_crypto *c = malloc(sizeof(*c));
    if (c != NULL)
        stream_packet_crypto_init(c, key, iv);
    return c;
}

void diff_pc_free(void *c) { free(c); }

int diff_pc_compute_tag(void *c, uint64_t key_pos, const uint8_t *packet, size_t length, int tag_offset,
                        int zero_key_pos, uint8_t out[4])
{
    return stream_packet_crypto_compute_tag(c, key_pos, packet, length, tag_offset, zero_key_pos, out);
}

void diff_pc_crypt(void *c, uint64_t key_pos, uint8_t *payload, size_t length)
{
    stream_packet_crypto_crypt_payload(c, key_pos, payload, length);
}

/* ---- FEC ---- */

int diff_fec_encode(uint8_t *buf, size_t unit_size, size_t stride, int k, int m)
{
    return fec_reed_solomon_encode(buf, unit_size, stride, k, m);
}

int diff_fec_decode(uint8_t *buf, size_t unit_size, size_t stride, int k, int m, const uint8_t *present)
{
    return fec_reed_solomon_decode(buf, unit_size, stride, k, m, present);
}

/* ---- the demuxer, with its events flattened into one callback ---- */

enum { EV_VIDEO = 1, EV_AUDIO = 2, EV_LOSS = 3, EV_CONTROL = 4 };

typedef void (*diff_event_fn)(void *user, int kind, int a, int b, const uint8_t *data, size_t length);

typedef struct {
    stream_demux demux;
    stream_packet_crypto crypto;
    diff_event_fn fn;
    void *user;
} diff_demux;

static void on_video(void *u, const uint8_t *d, size_t n, int key)
{
    diff_demux *x = u;
    x->fn(x->user, EV_VIDEO, key, 0, d, n);
}

static void on_audio(void *u, const uint8_t *d, size_t n)
{
    diff_demux *x = u;
    x->fn(x->user, EV_AUDIO, 0, 0, d, n);
}

static void on_loss(void *u, int first, int last)
{
    diff_demux *x = u;
    x->fn(x->user, EV_LOSS, first, last, NULL, 0);
}

static void on_control(void *u, const stream_header *h, const uint8_t *d, size_t n)
{
    diff_demux *x = u;
    x->fn(x->user, EV_CONTROL, h->type, 0, d, n);
}

/* `key` NULL selects the passthrough seam. */
void *diff_demux_new(const uint8_t *key, const uint8_t *iv, diff_event_fn fn, void *user)
{
    diff_demux *x = calloc(1, sizeof(*x));
    stream_demux_sink sink;
    stream_demux_crypto crypto;

    if (x == NULL)
        return NULL;
    x->fn = fn;
    x->user = user;
    memset(&sink, 0, sizeof(sink));
    sink.userdata = x;
    sink.video_frame_ready = on_video;
    sink.audio_frame_ready = on_audio;
    sink.video_loss_detected = on_loss;
    sink.control_packet_received = on_control;
    if (key != NULL) {
        stream_packet_crypto_init(&x->crypto, key, iv);
        crypto = stream_demux_packet_crypto(&x->crypto);
    } else {
        crypto = stream_demux_passthrough_crypto();
    }
    stream_demux_init(&x->demux, crypto, sink);
    return x;
}

void diff_demux_free(void *x) { free(x); }

void diff_demux_set_header(void *x, const uint8_t *data, size_t length)
{
    stream_demux_set_video_header(&((diff_demux *)x)->demux, data, length);
}

void diff_demux_ingest(void *x, const uint8_t *packet, size_t length)
{
    stream_demux_ingest(&((diff_demux *)x)->demux, packet, length);
}

void diff_demux_stats(void *x, long *received, long *lost, long *auth_failures, long *too_many)
{
    stream_demux *d = &((diff_demux *)x)->demux;
    stream_demux_take_packet_stats(d, received, lost);
    *auth_failures = d->auth_failures;
    *too_many = d->stat_frames_too_many_units;
}

int diff_demux_is_hevc(void *x) { return stream_demux_video_is_hevc(&((diff_demux *)x)->demux); }

/* ---- control plane ---- */

int diff_kdf(const uint8_t nonce[16], const uint8_t companion[16], int version, uint8_t key[16],
             uint8_t material[16])
{
    return halyard_control_kdf_derive(nonce, companion, version, key, material) == 0;
}

void diff_context_key(int codec, int version, uint8_t out[16])
{
    memcpy(out, halyard_field_context_key(codec, version), 16);
}

/* mode 0 encrypt, 1 decrypt, 2 streaminfo. */
int diff_control_crypt(const uint8_t nonce[16], const uint8_t companion[16], int codec, int version,
                       uint64_t counter, int mode, uint8_t *data, size_t length)
{
    halyard_control_field f;
    if (halyard_control_field_init(&f, nonce, companion, codec, version) != 0)
        return 0;
    if (mode == 0)
        halyard_control_field_encrypt(&f, counter, data, data, length);
    else if (mode == 1)
        halyard_control_field_decrypt(&f, counter, data, data, length);
    else
        halyard_control_streaminfo_crypt(&f, counter, data, data, length);
    return 1;
}

/* ---- registration and the account seed ---- */

int diff_regist_key(int is_ps5, const uint8_t *context, size_t length, uint32_t passcode, uint8_t out[16])
{
    return halyard_registration_derive_key(is_ps5, context, length, passcode, out);
}

int diff_regist_account_key(int is_ps5, const uint8_t *context, size_t length, const uint8_t seed[16],
                            uint8_t out[16])
{
    return halyard_registration_derive_account_key(is_ps5, context, length, seed, out);
}

/* account 0 selects the PIN route's wrap, 1 the account route's; unwrap 1 runs the inverse. */
int diff_regist_wrap(int is_ps5, int account, int unwrap, const uint8_t in[16], const uint8_t *context,
                     size_t length, uint8_t out[16])
{
    if (account)
        return unwrap ? halyard_registration_unwrap_account_material(is_ps5, in, context, length, out)
                      : halyard_registration_wrap_account_material(is_ps5, in, context, length, out);
    return unwrap ? halyard_registration_unwrap_material(is_ps5, in, context, length, out)
                  : halyard_registration_wrap_material(is_ps5, in, context, length, out);
}

size_t diff_seed_decode(const char *text, size_t length, uint8_t *out, size_t cap)
{
    return halyard_account_seed_decode_custom_data1(text, length, out, cap);
}

int diff_seed_recover(int is_ps5, const uint8_t d1[16], const uint8_t d2[16], const char *text, size_t length,
                      uint8_t out[16])
{
    return halyard_account_seed_recover_custom_data1(is_ps5, d1, d2, text, length, out);
}
