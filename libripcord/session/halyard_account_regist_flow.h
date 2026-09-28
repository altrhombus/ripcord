/*
 * ripcord - account ("web"/no-PIN) registration, the part that touches the platform.
 *
 * Two things need the platform and nothing else here does: the ephemeral data1/data2 the cloud tier
 * sends in its `commands` call, and the random context and material of the /sess/rgst request. Both
 * come from rc_random_bytes. The bytes then go to the console through a transport this file does NOT
 * implement - see "THE TRANSPORT" below.
 *
 * THE WHOLE ACCOUNT PAIRING, so it is clear which steps are here. From HalyardAccountPairing.RunAsync:
 *
 *   cloud tier    sign in; open the push WebSocket; create a session; send `commands` with
 *                 base64(data1), base64(data2); wait for the console to join and publish customData1;
 *                 exchange OFFER / ACCEPT / RESULT signaling; leave the session afterwards.
 *   this core     halyard_account_regist_generate_key_material     data1/data2
 *                 halyard_account_seed_recover_custom_data1         customData1 -> seed
 *                 the 9303 control association                      (not here - see below)
 *                 halyard_account_regist_run                        /sess/rgst -> pairing record
 *
 * THE TRANSPORT. On this route the console serves /sess/rgst over UDP 9303, inside the control
 * association whose prelude names both peers by the 20-byte ids they published in their signaling
 * OFFERs (HalyardDatagramControlChannel over HalyardControlAssociation). That association is shared
 * with the internet-play connect sequence, which carries /sess/init and /sess/ctrl over the SAME
 * association once rgst is done. This file takes it as a callback: "send these request bytes on a
 * fresh connection, and give me back the complete HTTP reply" - ExchangeAsync's contract - and the
 * callback over the real association is halyard_dgram_regist_exchange (halyard_dgram_session.h), with
 * the channel as `user`.
 *
 * NO SEARCH PROBE. The .NET client sends its SRC3/SRC2 probe to the console's host before every
 * registration POST, best effort, on both routes (HalyardRegistrationClient.RegisterAsync). Whether the
 * account route needs it is [X] - it is sent there because the client is shared, not because the account
 * route was shown to require it - and a probe is a socket, so it is the caller's to send:
 * halyard_control_arm_probe(host, is_ps5) before halyard_account_regist_run matches .NET exactly.
 */
#ifndef HALYARD_ACCOUNT_REGIST_FLOW_H
#define HALYARD_ACCOUNT_REGIST_FLOW_H

#include "halyard_account_regist.h"

#include <stddef.h>
#include <stdint.h>

/*
 * Fresh data1 (the seed-delivery field KEY) and data2 (its MATERIAL) for one cloud `commands` call.
 * Returns 1, or 0 if the platform could not produce random bytes - in which case both are zeroed, so a
 * caller that ignores the result sends something obviously wrong rather than something plausible.
 */
int halyard_account_regist_generate_key_material(uint8_t data1[16], uint8_t data2[16]);

/*
 * The transport: send `request` as one HTTP request on the account control association and return the
 * complete reply in `response` (halyard_dgram_http_complete decides "complete"). Returns 1
 * with *out_response_length set, or 0 on any failure - which is reported as ERR_TRANSPORT.
 */
typedef int (*halyard_account_regist_exchange_fn)(void *user,
                                                  const uint8_t *request, size_t request_length,
                                                  uint8_t *response, size_t response_size,
                                                  size_t *out_response_length);

typedef struct {
    int     is_ps5;
    char    account_id[64];  /* the signed-in PSN account id, from the cloud tier                  */
    char    client_ip[24];   /* THIS machine's address toward the console - the HOST header          */
    uint8_t seed[16];        /* from halyard_account_seed_recover_custom_data1                      */
} halyard_account_regist_params;

typedef struct {
    halyard_account_regist_status status;
    int                   http_status;
    char                  console_reason[32];  /* RP-Application-Reason, when the console gives one */
    halyard_regist_record record;              /* valid only when status is OK                      */
} halyard_account_regist_result;

/*
 * Build the request from fresh randomness, hand it to `exchange`, and open the reply.
 * Returns 1 when `out->record` is usable, 0 otherwise; `out->status` says which, always.
 *
 * NOT REENTRANT: the request and reply buffers are file-scope statics, as in halyard_regist_flow.c, so
 * a port with no heap and a small stack runs the same code. One registration at a time.
 */
int halyard_account_regist_run(const halyard_account_regist_params *params,
                               halyard_account_regist_exchange_fn exchange, void *user,
                               halyard_account_regist_result *out);

#endif /* HALYARD_ACCOUNT_REGIST_FLOW_H */
