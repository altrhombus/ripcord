/*
 * libripcord fuzzing - the LAN discovery reply parser.
 *
 * The first bytes this client ever accepts from the network, and it accepts them from anything on the
 * LAN that answers a broadcast: nothing is authenticated at this point, so every field is attacker-
 * controlled text headed for fixed-size buffers.
 */
#include "../../discovery/halyard_discovery.h"
#include "fuzz_input.h"

#include <stddef.h>
#include <stdint.h>

int LLVMFuzzerTestOneInput(const uint8_t *data, size_t size);

int LLVMFuzzerTestOneInput(const uint8_t *data, size_t size)
{
    halyard_discovered_console console;

    (void)halyard_discovery_parse_response((const char *)data, size, NULL, &console);
    (void)halyard_discovery_parse_response((const char *)data, size, "192.0.2.1", &console);
    return 0;
}
