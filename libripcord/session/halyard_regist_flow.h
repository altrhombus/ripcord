/*
 * ripcord - PIN registration, end to end.
 *
 * The three layers below this are each testable alone and none of them talks to a console:
 * halyard_registration.h is the key exchange, halyard_regist_message.h is the wire shape, and
 * halyard_control_arm.h is the search probe. This assembles them and is the only part that opens a
 * socket.
 *
 * WHY EVERY FAILURE HAS A NAME. Registration is the one flow a user drives, standing in front of two
 * screens, with a PIN that expires. When it fails the only useful output is which of a small number of
 * things went wrong - and the console is no help: it answers a wrong PIN, a wrong transport, an
 * unrecognised client and a stale search probe with the same 403 and the same generic application
 * reason. So the statuses below distinguish what THIS side can distinguish, and carry the console's own
 * reason when it gives one, rather than collapsing everything into "registration failed".
 */
#ifndef HALYARD_REGIST_FLOW_H
#define HALYARD_REGIST_FLOW_H

#include "halyard_regist_message.h"

#include <stddef.h>
#include <stdint.h>

typedef enum {
    HALYARD_REGIST_OK = 0,

    /* Before anything is sent. */
    HALYARD_REGIST_ERR_NO_TABLES,      /* this build carries no registration constants     */
    HALYARD_REGIST_ERR_BAD_PARAMS,     /* a missing host, account id or client address      */
    HALYARD_REGIST_ERR_NO_RANDOM,      /* the platform could not produce random bytes       */

    /* Reaching the console. */
    HALYARD_REGIST_ERR_CONNECT,        /* TCP 9295 refused or unreachable                   */
    HALYARD_REGIST_ERR_SEND,
    HALYARD_REGIST_ERR_NO_REPLY,       /* connected, then silence                           */

    /* The console answered. */
    HALYARD_REGIST_ERR_MALFORMED,      /* not an HTTP response this understands             */
    HALYARD_REGIST_ERR_REFUSED,        /* a non-2xx status; http_status and reason say more */
    HALYARD_REGIST_ERR_BAD_RECORD      /* 2xx, but the decrypted body is not a pairing record -
                                        * which in practice means the PIN was wrong, since the
                                        * key that decrypted it was derived from the PIN */
} halyard_regist_status;

/* A short, user-facing sentence for a status. Never NULL. */
const char *halyard_regist_status_text(halyard_regist_status status);

typedef struct {
    char     host[64];        /* the console, dotted quad */
    int      is_ps5;
    char     account_id[64];  /* the PSN account id, as the console expects it */
    uint32_t passcode;        /* the 8 digits shown on the console */
    char     client_ip[24];   /* THIS machine's LAN address - it goes in the HOST header */
} halyard_regist_params;

typedef struct {
    halyard_regist_status status;
    int                   saw_search_reply;  /* whether the console answered the probe */
    int                   http_status;
    char                  console_reason[32];/* RP-Application-Reason, when it gives one */
    halyard_regist_record record;            /* valid only when status is OK */
} halyard_regist_result;

/*
 * Runs the whole flow: probe, connect, POST, read, decrypt, parse. Blocking, and bounded by the
 * socket's own timeouts plus the probe window - expect it to take a couple of seconds.
 *
 * Returns 1 when `out->record` is usable, 0 otherwise; `out->status` says which, always.
 */
int halyard_regist_run(const halyard_regist_params *params, halyard_regist_result *out);

#endif /* HALYARD_REGIST_FLOW_H */
