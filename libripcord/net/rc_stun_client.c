/*
 * libripcord - the STUN socket layer. See rc_stun.h; the reference is StunClient.GatherAsync.
 *
 * Separate from rc_stun.c on purpose - the header says why - and the only file in the STUN code that
 * touches a socket, the clock or the CSPRNG.
 */
#include "rc_stun.h"
#include "rc_tcp.h"

#include "../platform/rc_platform.h"

#include <string.h>

#include <netinet/in.h>
#include <sys/socket.h>
#include <sys/types.h>

/*
 * How long to sleep between empty non-blocking reads. The core waits this way everywhere rather than in
 * poll(), because recvfrom-and-sleep is the idiom every port's sockets are known to honour (see
 * rc_platform.h and rc_tcp.h on what the PS3 does to assumptions about descriptors). 10 ms is a small
 * fraction of the 500 ms window and adds at most that much to the time an answer is noticed.
 */
#define STUN_POLL_INTERVAL_MS 10u

/* Large enough for any response a public server sends; a larger datagram is truncated by recvfrom, and
 * a truncated STUN message fails the declared-length check rather than being misread. */
#define STUN_RECV_BUFFER 1500

/*
 * StunClient.AwaitResponseAsync. Returns 1 with *out set, 0 when the attempt is over without an answer.
 *
 * Two deliberate matches with .NET: a Binding Success for our id that carries no readable address ends
 * the attempt at once (AwaitResponseAsync returns its null MappedAddress rather than waiting on), and
 * everything else - an error response, a stale id, a datagram that is not STUN - is discarded and the
 * wait continues.
 *
 * One deliberate difference: .NET ends the attempt on a SocketException, which is how an ICMP port-
 * unreachable surfaces on some platforms. Here a failing recvfrom is treated like an empty one and the
 * window runs out. Which errno a non-blocking read with no data reports is exactly the kind of thing the
 * ports disagree about, and misclassifying "nothing yet" as fatal would turn every attempt into an
 * instant failure. The cost of the conservative reading is time, never a wrong answer.
 */
static int await_response(int sock, const uint8_t *transaction_id, uint32_t timeout_ms,
                          rc_stun_address *out)
{
    uint64_t start_ms = rc_time_ms();

    for (;;) {
        uint8_t buf[STUN_RECV_BUFFER];
        ssize_t n = recvfrom(sock, buf, sizeof(buf), 0, NULL, NULL);

        if (n > 0) {
            /* The source address is not checked, as .NET does not check it: 96 random bits of
             * transaction id are the match, and a server may legitimately answer from another address. */
            rc_stun_response_status status = rc_stun_parse_binding_response(
                buf, (size_t)n, transaction_id, RC_STUN_TRANSACTION_ID_SIZE, out);

            if (status == RC_STUN_RESPONSE_OK)
                return 1;
            if (status == RC_STUN_RESPONSE_NO_ADDRESS)
                return 0;
        }

        /* Checked after every datagram, not only after an empty read: a socket that never runs dry -
         * a console already streaming at it - must not be able to hold the attempt open forever. */
        if (rc_time_ms() - start_ms >= (uint64_t)timeout_ms)
            return 0;
        if (n <= 0)
            rc_sleep_ms(STUN_POLL_INTERVAL_MS);
    }
}

rc_stun_gather_status rc_stun_gather(int sock, const struct sockaddr_in *servers, size_t server_count,
                                     unsigned attempts_per_server, uint32_t per_attempt_timeout_ms,
                                     rc_stun_address *out_reflexive, size_t *out_server_index)
{
    size_t server;

    if (sock < 0 || servers == NULL || out_reflexive == NULL || out_server_index == NULL)
        return RC_STUN_GATHER_BAD_ARGUMENT;
    if (attempts_per_server == 0)
        attempts_per_server = 1;
    if (!rc_socket_set_nonblocking(sock))
        return RC_STUN_GATHER_SOCKET_ERROR;

    for (server = 0; server < server_count; server++) {
        unsigned attempt;

        for (attempt = 0; attempt < attempts_per_server; attempt++) {
            uint8_t transaction_id[RC_STUN_TRANSACTION_ID_SIZE];
            uint8_t request[RC_STUN_HEADER_SIZE];
            size_t request_len;
            ssize_t sent;

            /* A fresh id per attempt, as .NET draws one, so a late answer to an earlier attempt is
             * discarded even though it would have been right. No fallback if the CSPRNG fails: a
             * guessable id lets anything off-path choose the reflexive address we advertise. */
            if (!rc_random_bytes(transaction_id, sizeof(transaction_id)))
                return RC_STUN_GATHER_NO_ENTROPY;
            request_len = rc_stun_build_binding_request(transaction_id, sizeof(transaction_id), request,
                                                        sizeof(request));

            /* .NET lets a send exception propagate out of GatherAsync, ending the whole gather; a
             * status is C's nearest equivalent. A socket that cannot send to one public server is not
             * going to do better with the next. */
            sent = sendto(sock, request, request_len, 0, (const struct sockaddr *)&servers[server],
                          (socklen_t)sizeof(servers[server]));
            if (sent < 0 || (size_t)sent != request_len)
                return RC_STUN_GATHER_SOCKET_ERROR;

            if (await_response(sock, transaction_id, per_attempt_timeout_ms, out_reflexive)) {
                *out_server_index = server;
                return RC_STUN_GATHER_OK;
            }
        }
    }
    return RC_STUN_GATHER_NO_ANSWER;
}
