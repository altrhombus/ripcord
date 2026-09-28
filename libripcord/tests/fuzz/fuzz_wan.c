/*
 * libripcord fuzzing - candidate addresses as the cloud tier hands them over.
 *
 * The console's OFFER reaches the core as text the front end copied out of JSON, so the address parser
 * and the selection read bytes that came from the network. Each record fills one candidate's address
 * field verbatim - unterminated when the record is long enough, which is the case a C reader must survive -
 * and the whole offer is then chosen from, against interfaces built from the input as well.
 */
#include "../../session/halyard_wan_candidates.h"
#include "fuzz_input.h"

#include <stddef.h>
#include <stdint.h>
#include <string.h>

int LLVMFuzzerTestOneInput(const uint8_t *data, size_t size);

#define FUZZ_WAN_CANDIDATES 8

int LLVMFuzzerTestOneInput(const uint8_t *data, size_t size)
{
    halyard_wan_candidate offer[FUZZ_WAN_CANDIDATES];
    halyard_wan_interface nics[2];
    halyard_wan_candidate ours[HALYARD_WAN_OUR_CANDIDATES_MAX];
    rc_fuzz_input in;
    const uint8_t *record;
    size_t record_length;
    size_t count = 0;
    char text[HALYARD_WAN_ADDRESS_MAX];
    uint8_t address[4];

    memset(offer, 0, sizeof(offer));
    memset(nics, 0, sizeof(nics));
    if (size >= 16)
        memcpy(nics, data, 16);

    rc_fuzz_input_init(&in, data, size);
    while (count < FUZZ_WAN_CANDIDATES && rc_fuzz_next(&in, &record, &record_length)) {
        size_t n = record_length < sizeof(offer[count].address) ? record_length : sizeof(offer[count].address);

        memcpy(offer[count].address, record, n);
        offer[count].port = (uint16_t)record_length;

        /* The parser proper takes a terminated string. */
        n = record_length < sizeof(text) - 1 ? record_length : sizeof(text) - 1;
        memcpy(text, record, n);
        text[n] = '\0';
        if (halyard_wan_parse_ipv4(text, address)) {
            char formatted[16];

            (void)halyard_wan_format_ipv4(address, formatted, sizeof(formatted));
            (void)halyard_wan_shares_subnet(address, nics, 2);
        }
        (void)halyard_wan_our_candidates(text, 1, text, 2, ours, HALYARD_WAN_OUR_CANDIDATES_MAX);
        count++;
    }

    (void)halyard_wan_choose_candidate(offer, count, nics, 2, count > 0 ? "192.0.2.7" : NULL);
    return 0;
}
