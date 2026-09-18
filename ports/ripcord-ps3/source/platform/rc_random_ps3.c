/*
 * ripcord-ps3 - the random-bytes seam.
 *
 * ports/common/util/rc_random.h holds the argument for why this is a named seam with a hard-failing
 * contract, and it is not repeated here: two callers need real entropy - the 16-byte handshakeKey that
 * authenticates the ECDH exchange, and the ephemeral ECDH private scalar - and NEITHER SHOWS A SYMPTOM
 * when it does not get it. A session keyed from a counter connects, plays, and looks exactly like a
 * working one.
 *
 * This file exists because that argument was previously the reason rc_platform_ps3.c refused to
 * implement the function at all. It said, correctly, that a call this port could not confirm must fail
 * to link rather than be stubbed, and that when the call WAS confirmed it belonged in its own file
 * rather than being appended to one about clocks. This is that file.
 *
 * WHAT WAS CONFIRMED, AND HOW - 2026-09-11, against ps3dev nightly-2026-07-26.
 *
 *   - sysGetRandomNumber(void *addr, u64 size) is declared in <lv2/system.h>. The header describes the
 *     generator as FIPS186-2 DSS and caps one call at RANDOM_NUMBER_MAX_SIZE, which is 4096 bytes.
 *   - It is defined in liblv2.a, which this port already links.
 *   - It is NOT a direct syscall but a PRX stub: it sits in .sceStub.text and resolves against the
 *     library named in .rodata.sceResident, which is `sysPrxForUser` - the always-resident user library
 *     that the loader binds without being asked, and the same one sysGetSystemTime comes from. So there
 *     is no sysModuleLoad() to perform before calling it, which was the open question worth settling
 *     before writing this. Read out of our own installed toolchain with ar/objdump, not from
 *     documentation and not from anyone else's code.
 *
 * WHAT WAS NOT, and both are why rc_random_init() below is a probe rather than a formality:
 *
 *   [X] THE RETURN CONVENTION. The declared type is u32 and every lv2-family call in this SDK returns
 *       0 for success with error codes up in 0x8001xxxx, so zero-is-success is the reading taken here.
 *       It is an inference from a pattern, not a documented fact.
 *   [X] ALIGNMENT. The header says `void *addr` and nothing about what it must be aligned to. lv2
 *       syscalls are not always so relaxed. Rather than guess - or bounce every draw through an aligned
 *       scratch buffer to defend against a requirement that may not exist - the probe deliberately asks
 *       for bytes at an ODD address, so the first boot answers the question instead of a later key
 *       quietly being wrong.
 *
 * NOTHING HERE HAS RUN ON A CONSOLE. It compiles; that is a different claim, and in this file of all
 * files the distance between the two is the whole point.
 */

#include "util/rc_random.h"

#include <string.h>

#include <lv2/system.h>

/* Set once rc_random_init() has seen the generator actually produce bytes. Everything below refuses to
 * hand out key material until it is, because "the call exists" and "the call works" are not the same
 * thing on a platform nobody here has booted. */
static int s_ready;

/*
 * One draw, no policy - the thin wrapper over the SDK call, so that the chunking loop and the probe can
 * share exactly one interpretation of what the call means. Returns 1 on success.
 */
static int rc_ps3_draw(uint8_t *out, size_t length)
{
    /* Callers never exceed this, but the cap belongs next to the call rather than only at the call
     * sites: a future caller that forgets it would get a silently short fill, which is the same class
     * of invisible failure this whole seam exists to prevent. */
    if (length == 0u || length > (size_t)RANDOM_NUMBER_MAX_SIZE)
        return 0;

    return sysGetRandomNumber(out, (u64)length) == 0u;
}

int rc_random_init(void)
{
    /* 40 bytes so that a 32-byte draw at offset 1 fits. The offset is the point: see the ALIGNMENT note
     * at the top. If lv2 requires an aligned destination, this is where that is discovered - at startup,
     * loudly, with the connection refused - rather than in a handshake key nobody can inspect. */
    uint8_t probe[40];
    uint8_t *unaligned = probe + 1;
    const size_t draw = 32u;
    size_t i;

    if (s_ready)
        return 1;

    memset(probe, 0, sizeof(probe));

    if (!rc_ps3_draw(unaligned, draw))
        return 0;

    /*
     * A smoke test, and deliberately only that. It cannot tell a CSPRNG from a bad one - no cheap test
     * can - so it checks the single failure that is both plausible and catastrophic: a call that
     * reports success and writes nothing, leaving the zeros memset put there. That is what an
     * unimplemented syscall looks like on a modified console, and an all-zero key is the worst possible
     * one. Anything that is not all zeros is accepted; this is a liveness check, not a statistical one,
     * and claiming otherwise in a comment would be worse than not checking at all.
     */
    for (i = 0u; i < draw; i++) {
        if (unaligned[i] != 0u) {
            s_ready = 1;
            break;
        }
    }

    /* Key material was in this buffer, briefly. It is 32 bytes of stack that would otherwise stay
     * readable for the life of the program; the generator is not weakened by it, but leaving it is a
     * habit worth not having. */
    memset(probe, 0, sizeof(probe));

    return s_ready;
}

void rc_random_exit(void)
{
    /* Nothing to tear down: sysPrxForUser is resident whether this port likes it or not, and there is no
     * handle, container or service of ours to give back. The function stays because the seam declares it
     * and a port that silently omits half a contract is worse than one with an empty body - the 3DS has
     * a real psExit() to call here, and the next platform may too. */
    s_ready = 0;
}

int rc_random_bytes(uint8_t *out, size_t length)
{
    size_t done = 0u;

    if (out == NULL)
        return 0;
    if (length == 0u)
        return 1;

    /* Zero first, so that every failure path below leaves an obviously-wrong buffer rather than stack
     * residue that might pass a casual glance at a hexdump. Lifted from the 3DS implementation, which
     * argues it at rc_random.h: zeros are not safe, they are GREPPABLE, and that is the point. */
    memset(out, 0, length);

    if (!s_ready)
        return 0;

    /* The chunking loop the 4096-byte cap forces. No caller in this project asks for anything near it -
     * the largest is a 32-byte scalar - so this will not execute twice in practice. It is written
     * because the cap is real and a future caller will not read this file first. */
    while (done < length) {
        size_t chunk = length - done;

        if (chunk > (size_t)RANDOM_NUMBER_MAX_SIZE)
            chunk = (size_t)RANDOM_NUMBER_MAX_SIZE;

        if (!rc_ps3_draw(out + done, chunk)) {
            /* A partial fill is the dangerous shape: the front of the buffer is good entropy and the
             * back is whatever was there, which is exactly the kind of key that looks fine in a
             * hexdump. Discard all of it. */
            memset(out, 0, length);
            return 0;
        }
        done += chunk;
    }

    return 1;
}

int rc_random_rng_callback(void *ctx, uint8_t *out, size_t length)
{
    (void)ctx;
    /* Inverted on purpose: mbedtls treats 0 as success. rc_platform.h now spells out that the two
     * conventions in this seam meet here and nowhere else - it used to describe them the wrong way
     * round, which is a mistake that makes every failure look like a success. */
    return rc_random_bytes(out, length) ? 0 : -1;
}
