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

/* ---- Takion ---- */
#include "../../../libripcord/takion/takion_data_chunk.h"
#include "../../../libripcord/takion/takion_sack_chunk.h"
#include "../../../libripcord/takion/takion_handshake.h"
#include "../../../libripcord/takion/takion_control_proto.h"
#include "../../../libripcord/takion/takion_control_sealer.h"
#include "../../../libripcord/takion/senkusha_echo.h"

/* first: 1 first-fragment parser, 0 continuation. out: tsn, channel, ending, payload offset, length. */
int diff_takion_data(int first, const uint8_t *chunk, size_t length, uint32_t *tsn, unsigned *channel,
                     int *ending, size_t *payload_offset, size_t *payload_length)
{
    const uint8_t *payload;
    int ok = first ? takion_data_parse_first(chunk, length, tsn, channel, ending, &payload, payload_length)
                   : takion_data_parse_continuation(chunk, length, tsn, channel, ending, &payload, payload_length);
    if (ok)
        *payload_offset = (size_t)(payload - chunk);
    return ok;
}

int diff_takion_sack(const uint8_t *chunk, size_t length, uint32_t out[4])
{
    takion_sack_info info;
    if (!takion_sack_parse(chunk, length, &info))
        return 0;
    out[0] = info.cumulative_tsn_ack;
    out[1] = info.a_rwnd;
    out[2] = info.gap_ack_block_count;
    out[3] = info.dup_tsn_count;
    return 1;
}

int diff_takion_init_ack(const uint8_t *chunk, size_t length, uint32_t *tag, uint32_t *tsn, uint8_t cookie[32])
{
    return takion_parse_init_ack(chunk, length, tag, tsn, cookie);
}

int diff_control_peek(const uint8_t *data, size_t length, uint32_t *type)
{
    return takion_control_peek_type(data, length, type);
}

int diff_control_validate(const uint8_t *data, size_t length) { return takion_control_validate(data, length); }

/* Flattens a parsed reply: 4 numbers, then (offset, length) pairs into `data` for the byte fields, with
 * offset SIZE_MAX for an absent optional. */
int diff_control_reply(const uint8_t *data, size_t length, uint32_t nums[4], size_t spans[8])
{
    takion_session_reply r;
    if (!takion_control_parse_session_reply(data, length, &r))
        return 0;
    nums[0] = r.server_version;
    nums[1] = r.token;
    nums[2] = (uint32_t)r.encrypted_key_accepted;
    nums[3] = (uint32_t)r.version_accepted;
#define SPAN(i, p, n) do { spans[2*(i)] = (p) ? (size_t)((const uint8_t *)(p) - data) : (size_t)-1; spans[2*(i)+1] = (n); } while (0)
    SPAN(0, r.session_key, r.session_key_length);
    SPAN(1, r.server_version_string, r.server_version_string_length);
    SPAN(2, r.ecdh_public_key, r.ecdh_public_key_length);
    SPAN(3, r.ecdh_signature, r.ecdh_signature_length);
    return 1;
}

int diff_control_stream_info(const uint8_t *data, size_t length, uint32_t nums[3], size_t spans[4])
{
    takion_stream_info i;
    if (!takion_control_parse_stream_info(data, length, &i))
        return 0;
    nums[0] = i.width;
    nums[1] = i.height;
    nums[2] = (uint32_t)i.has_resolution;
    SPAN(0, i.video_header, i.video_header_length);
    SPAN(1, i.audio_header, i.audio_header_length);
    return 1;
}

int diff_control_disconnect(const uint8_t *data, size_t length, size_t span[2])
{
    const char *reason;
    size_t n;
    if (!takion_control_parse_disconnect(data, length, &reason, &n))
        return 0;
    span[0] = (size_t)((const uint8_t *)reason - data);
    span[1] = n;
    return 1;
}

int diff_control_version_ack(const uint8_t *data, size_t length, uint32_t *version)
{
    return takion_control_parse_protocol_version_ack(data, length, version);
}

/* kind: 0 control, 1 congestion, 2 input (payload_offset used). Seals `count` packets in sequence
 * through one sealer, each in place, so the shared key position is compared too. */
void diff_seal_sequence(const uint8_t key[16], const uint8_t iv[16], const int *kinds, uint8_t **packets,
                        const size_t *lengths, const size_t *payload_offsets, size_t count)
{
    takion_control_sealer s;
    size_t i;
    takion_control_sealer_init(&s, key, iv);
    for (i = 0; i < count; i++) {
        if (kinds[i] == 0)
            takion_control_sealer_seal(&s, packets[i], lengths[i]);
        else if (kinds[i] == 1)
            takion_control_sealer_seal_congestion(&s, packets[i], lengths[i]);
        else
            takion_control_sealer_seal_input(&s, packets[i], lengths[i], payload_offsets[i]);
    }
}

int diff_verify_control(const uint8_t key[16], const uint8_t iv[16], const uint8_t *packet, size_t length)
{
    takion_control_verifier v;
    takion_control_verifier_init(&v, key, iv);
    return takion_control_verifier_check(&v, packet, length);
}

int diff_senkusha_echo(const uint8_t *datagram, size_t length, uint8_t *sequence)
{
    return senkusha_echo_is_echo(datagram, length, sequence);
}

/* ---- the 9303 wire, and the scripted console ---- */
#include "../../../libripcord/tests/fake_dgram_console.h"

int diff_dgram_prelude(const uint8_t *data, size_t length, uint8_t out[88])
{
    halyard_dgram_prelude p;
    if (!halyard_dgram_prelude_parse(data, length, &p))
        return 0;
    halyard_dgram_prelude_write(&p, out, 88);
    return 1;
}

/* Every chunk's (kind, flags, body offset, body length, source, destination, words), 7 values each. */
size_t diff_dgram_chunks(const uint8_t *data, size_t length, size_t *out, size_t max_chunks)
{
    halyard_dgram_chunk c;
    size_t offset = 0, n = 0;
    while (n < max_chunks && halyard_dgram_chunk_next(data, length, &offset, &c)) {
        size_t *o = out + 7 * n++;
        o[0] = c.type; o[1] = c.flags; o[2] = (size_t)(c.body - data); o[3] = c.body_length;
        o[4] = c.source_port; o[5] = c.destination_port; o[6] = c.word_count;
    }
    return n;
}

int diff_http_complete(const uint8_t *data, size_t length) { return halyard_dgram_http_complete(data, length); }

typedef void (*diff_emit_fn)(void *user, const uint8_t *datagram, size_t length);

typedef struct {
    fake_console fc;
    diff_emit_fn emit;
    void *user;
    char init_reply[512];
    char other_reply[512];
} diff_console;

static void console_emit(void *ctx, const uint8_t *d, size_t n)
{
    diff_console *c = ctx;
    c->emit(c->user, d, n);
}

void *diff_console_new(diff_emit_fn emit, void *user, const char *init_reply, const char *other_reply,
                       int close_before_answering)
{
    diff_console *c = calloc(1, sizeof(*c));
    if (c == NULL)
        return NULL;
    c->emit = emit;
    c->user = user;
    fake_console_init(&c->fc, console_emit, c);
    if (init_reply != NULL) {
        strncpy(c->init_reply, init_reply, sizeof(c->init_reply) - 1);
        c->fc.init_reply = c->init_reply;
    }
    if (other_reply != NULL) {
        strncpy(c->other_reply, other_reply, sizeof(c->other_reply) - 1);
        c->fc.other_reply = c->other_reply;
    }
    c->fc.close_before_answering = close_before_answering;
    return c;
}

void diff_console_free(void *c) { free(c); }

void diff_console_datagram(void *c, const uint8_t *d, size_t n)
{
    fake_console_on_datagram(&((diff_console *)c)->fc, d, n);
}

/* inits, hellos, closes, requests, ctrl_open, frames */
void diff_console_counts(void *c, int out[6])
{
    fake_console *f = &((diff_console *)c)->fc;
    out[0] = f->inits; out[1] = f->hellos; out[2] = f->closes_received;
    out[3] = f->requests; out[4] = f->ctrl_open; out[5] = f->frames;
}

size_t diff_console_frame(void *c, int i, const uint8_t **data)
{
    fake_console *f = &((diff_console *)c)->fc;
    *data = f->frame[i];
    return f->frame_length[i];
}

size_t diff_console_request(void *c, int i, const char **text)
{
    fake_console *f = &((diff_console *)c)->fc;
    *text = f->request[i];
    return f->request_length[i];
}

/* ---- discovery, wake, /sess ---- */
#include "../../../libripcord/discovery/halyard_discovery.h"
#include "../../../libripcord/discovery/halyard_wake.h"
#include "../../../libripcord/session/halyard_sess_fields.h"
#include "../../../libripcord/session/halyard_sess_request.h"
#include "../../../libripcord/session/halyard_ctrl_message.h"

/* Four NUL-terminated fields into `out` (host_id, host_type, host_name, system_version), 128 bytes each. */
int diff_discovery_parse(const uint8_t *data, size_t length, char out[4][128], int *is_awake)
{
    halyard_discovered_console c;
    if (!halyard_discovery_parse_response((const char *)data, length, NULL, &c))
        return 0;
    snprintf(out[0], 128, "%s", c.host_id);
    snprintf(out[1], 128, "%s", c.host_type);
    snprintf(out[2], 128, "%s", c.host_name);
    snprintf(out[3], 128, "%s", c.system_version);
    *is_awake = c.is_awake;
    return 1;
}

int diff_wake_credential(const uint8_t *key, size_t length, char out[16])
{
    return halyard_wake_credential(key, length, out, 16);
}

size_t diff_wake_payload(int ps5, const char *credential, char *out, size_t size)
{
    return halyard_wake_build_payload(ps5 ? &halyard_discovery_profile_ps5 : &halyard_discovery_profile_ps4,
                                      credential, out, size);
}

void diff_sess_auth(const uint8_t *key, size_t length, uint8_t out[16]) { halyard_sess_field_auth_plaintext(key, length, out); }
void diff_sess_did(const uint8_t *id, size_t length, uint8_t out[32]) { halyard_sess_field_did_plaintext(id, length, out); }
size_t diff_sess_os(int major, int minor, char *out, size_t size) { return halyard_sess_field_os_type_plaintext(major, minor, out, size); }

size_t diff_ctrl_parse(const uint8_t *data, size_t length, unsigned *type, size_t *payload_length)
{
    const uint8_t *payload;
    return halyard_ctrl_message_parse(data, length, type, &payload, payload_length);
}

/* Returns bytes consumed (0 = incomplete); the status, and the named header's value into out. */
size_t diff_sess_response(const char *data, size_t length, int *status, const char *name, char *out, size_t size, int *has)
{
    halyard_sess_response r;
    size_t n = halyard_sess_response_parse(data, length, &r);
    if (n == 0)
        return 0;
    *status = r.status_code;
    *has = halyard_sess_response_header(data, &r, name, out, size);
    return n;
}

/* ---- STUN, candidates, and the 9303 association ---- */
#include "../../../libripcord/net/rc_stun.h"
#include "../../../libripcord/session/halyard_wan_candidates.h"

/* 0 none; else family, port, 16 address bytes. Returns 1 if it parsed as STUN. */
int diff_stun_parse(const uint8_t *data, size_t length, uint16_t *type, uint8_t txid[12], int *has, uint8_t *family,
                    uint16_t *port, uint8_t address[16])
{
    rc_stun_message m;
    if (!rc_stun_parse(data, length, &m))
        return 0;
    *type = m.type;
    memcpy(txid, m.transaction_id, 12);
    *has = m.has_mapped_address;
    *family = m.mapped_address.family;
    *port = m.mapped_address.port;
    memcpy(address, m.mapped_address.address, 16);
    return 1;
}

int diff_parse_ipv4(const char *text, uint8_t out[4]) { return halyard_wan_parse_ipv4(text, out); }

int diff_choose_candidate(const char (*addresses)[64], size_t count, const uint8_t *iface, size_t iface_count,
                          const char *host)
{
    halyard_wan_candidate c[16];
    halyard_wan_interface nic[4];
    size_t i;
    for (i = 0; i < count && i < 16; i++) {
        memset(&c[i], 0, sizeof(c[i]));
        snprintf(c[i].type, sizeof(c[i].type), "X");
        snprintf(c[i].address, sizeof(c[i].address), "%s", addresses[i]);
    }
    for (i = 0; i < iface_count && i < 4; i++) {
        memcpy(nic[i].address, iface + 8 * i, 4);
        memcpy(nic[i].netmask, iface + 8 * i + 4, 4);
    }
    return halyard_wan_choose_candidate(c, count, nic, iface_count, host);
}

typedef struct {
    halyard_dgram_assoc assoc;
    uint8_t next;
    diff_emit_fn emit;   /* every send */
    diff_event_fn event; /* kind, a = opened_by_peer, data = whole inbound for DataReceived */
    void *user;
} diff_assoc;

static void assoc_send(void *ctx, const uint8_t *d, size_t n) { diff_assoc *a = ctx; a->emit(a->user, d, n); }
static void assoc_event(void *ctx, const halyard_dgram_event *e)
{
    diff_assoc *a = ctx;
    a->event(a->user, (int)e->kind, e->opened_by_peer, (int)e->chunk_type, e->data, e->data_length);
}
static int assoc_random(void *ctx, uint8_t *out, size_t n)
{
    diff_assoc *a = ctx;
    size_t i;
    for (i = 0; i < n; i++)
        out[i] = a->next++;
    return 1;
}

void *diff_assoc_new(const uint8_t local[20], const uint8_t peer[20], const uint8_t address[4], uint16_t port,
                     diff_emit_fn emit, diff_event_fn event, void *user)
{
    diff_assoc *a = calloc(1, sizeof(*a));
    halyard_dgram_callbacks cb;
    if (a == NULL)
        return NULL;
    a->next = 0x40;
    a->emit = emit;
    a->event = event;
    a->user = user;
    cb.send = assoc_send;
    cb.event = assoc_event;
    cb.random = assoc_random;
    cb.ctx = a;
    halyard_dgram_assoc_init(&a->assoc, &cb, local, peer, address, port);
    return a;
}

void diff_assoc_free(void *a) { free(a); }

/* op: 0 open, 1 retry, 2 openconn(arg = words), 3 reopen, 4 closeconn, 5 send, 6 recv, 7 clear. Returns the phase. */
int diff_assoc_op(void *p, int op, int arg, const uint8_t *data, size_t length)
{
    diff_assoc *a = p;
    switch (op) {
    case 0: halyard_dgram_assoc_open(&a->assoc); break;
    case 1: halyard_dgram_assoc_retry(&a->assoc); break;
    case 2: halyard_dgram_assoc_open_connection(&a->assoc, (halyard_dgram_addressing)arg); break;
    case 3: halyard_dgram_assoc_reopen_connection(&a->assoc); break;
    case 4: halyard_dgram_assoc_close_connection(&a->assoc); break;
    case 5: halyard_dgram_assoc_send(&a->assoc, data, length); break;
    case 6: halyard_dgram_assoc_on_datagram(&a->assoc, data, length); break;
    default: halyard_dgram_assoc_clear_inbound(&a->assoc); break;
    }
    return (int)a->assoc.phase;
}

/* ---- the account-id normaliser ---- */
#include "../../../libripcord/session/halyard_account_id.h"

int diff_account_id(const char *in, char *out, size_t size) { return (int)halyard_account_id_normalise(in, out, size); }
