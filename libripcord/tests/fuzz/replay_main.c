/*
 * libripcord fuzzing - a driver for compilers without libFuzzer.
 *
 * Apple's clang ships the sanitizers but not libFuzzer, so on a Mac the harnesses link against this
 * instead. It does two things:
 *
 *   replay FILE...     run each file once, which is how a crash libFuzzer found elsewhere is
 *                      reproduced here
 *   -random=N          run N inputs from a fixed-seed generator, half of them in the harnesses'
 *                      length-prefixed record format, so the stateful paths get more than one packet
 *
 * This is not a fuzzer. There is no coverage feedback, so it finds shallow bugs and nothing else. What
 * it does guarantee is that every harness builds and runs cleanly under AddressSanitizer and
 * UndefinedBehaviorSanitizer on every host, including the one the macOS client is built on. The
 * coverage-guided run is `make fuzz-run`, with a clang that has libFuzzer.
 */
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

int LLVMFuzzerTestOneInput(const uint8_t *data, size_t size);

#define REPLAY_MAX_INPUT 65536

static uint8_t s_buffer[REPLAY_MAX_INPUT];

static uint32_t s_state = 0x9e3779b9u;

static uint32_t next_random(void)
{
    /* xorshift32: deterministic, so a failure in CI reproduces locally with the same N. */
    s_state ^= s_state << 13;
    s_state ^= s_state >> 17;
    s_state ^= s_state << 5;
    return s_state;
}

static size_t random_bytes(uint8_t *out, size_t length)
{
    size_t i;

    for (i = 0; i < length; i++)
        out[i] = (uint8_t)next_random();
    return length;
}

static size_t random_records(uint8_t *out, size_t capacity)
{
    size_t used = 0;
    unsigned records = 1u + next_random() % 12u;

    while (records-- > 0 && used + 2 < capacity) {
        size_t length = next_random() % 1600u;

        if (length > capacity - used - 2)
            length = capacity - used - 2;
        out[used++] = (uint8_t)(length >> 8);
        out[used++] = (uint8_t)length;
        used += random_bytes(out + used, length);
    }
    return used;
}

static int replay_file(const char *path)
{
    FILE *file = fopen(path, "rb");
    size_t size;

    if (file == NULL) {
        fprintf(stderr, "replay: cannot open %s\n", path);
        return 1;
    }
    size = fread(s_buffer, 1, sizeof s_buffer, file);
    fclose(file);
    (void)LLVMFuzzerTestOneInput(s_buffer, size);
    return 0;
}

int main(int argc, char **argv)
{
    int i;
    int failures = 0;

    for (i = 1; i < argc; i++) {
        if (strncmp(argv[i], "-random=", 8) == 0) {
            long count = strtol(argv[i] + 8, NULL, 10);
            long n;

            for (n = 0; n < count; n++) {
                size_t size = (n % 2 == 0)
                    ? random_bytes(s_buffer, next_random() % 2048u)
                    : random_records(s_buffer, sizeof s_buffer);
                (void)LLVMFuzzerTestOneInput(s_buffer, size);
            }
            printf("%s: %ld random inputs, clean\n", argv[0], count);
        } else {
            failures += replay_file(argv[i]);
        }
    }
    return failures == 0 ? 0 : 1;
}
