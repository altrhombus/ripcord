/*
 * A STAND-IN FOR PSL1GHT'S <lv2/system.h>, FOR HOST BUILDS ONLY. Not a copy of it: the real header is
 * Sony-facing SDK code that is not ours to reproduce, and nothing here is transcribed from it. What is
 * reproduced is the interface rc_random_ps3.c consumes - one function name, its parameter and return
 * types, and the one constant that shapes the chunking loop - which is the same class of thing as the
 * BSD socket names the core already assumes, and is what makes a host build of that file possible at all.
 *
 * tests/random_test.c defines sysGetRandomNumber itself and drives it through the failure modes a
 * console cannot be asked to produce on demand. On a real PS3 build this directory is not on the
 * include path and the genuine header is used; see tests/Makefile.
 *
 * The values below must match the SDK's. RANDOM_NUMBER_MAX_SIZE is 4096 there. If that ever changes,
 * this file is wrong in a way only a console would notice, which is why the test also asserts the
 * chunking behaviour rather than the constant.
 */
#ifndef RC_TEST_STUB_LV2_SYSTEM_H
#define RC_TEST_STUB_LV2_SYSTEM_H

#include <stdint.h>

typedef uint32_t u32;
typedef uint64_t u64;

#define RANDOM_NUMBER_MAX_SIZE (4096)

u32 sysGetRandomNumber(void *addr, u64 size);

#endif /* RC_TEST_STUB_LV2_SYSTEM_H */
