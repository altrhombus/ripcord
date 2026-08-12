/*
 * ripcord-3ds - the random-bytes seam.
 *
 * Two callers need real entropy, and both are security-critical in ways that are invisible when they go
 * wrong:
 *
 *   - `handshakeKey`, the 16 bytes that authenticate the ECDH exchange (takion_session_negotiator.h).
 *     A predictable one lets anything that can inject on the stream channel substitute its own public
 *     key, and the session still comes up and still plays.
 *   - the ephemeral ECDH private scalar (rc_ecdh.h), where a predictable value hands over the whole
 *     stream.
 *
 * Neither failure produces a symptom. That is the entire reason this is a named seam with a hard-failing
 * contract rather than a convenience wrapper: there is no observable difference between a session keyed
 * from the console's CSPRNG and one keyed from a counter, until someone is reading your traffic.
 *
 * ON DEVICE this is libctru's PS_GenerateRandomBytes (the `ps:ps` service - the system's own CSPRNG,
 * which is what the console's own software uses). mbedtls's built-in entropy sources are not an option:
 * they assume a hosted OS with /dev/urandom or a platform hook, neither of which the 3DS provides, which
 * is also why rc_ecdh takes its RNG as a caller-supplied callback instead of opening its own.
 *
 * OFF DEVICE (the host test builds) there is deliberately NO implementation. This file compiles to
 * nothing outside __3DS__, so a host program that tries to generate a real key fails to link rather than
 * silently getting a weak one. The host tests pass their own deterministic callback directly to rc_ecdh
 * instead - which is correct for known-answer vectors and would be a catastrophe here.
 */
#ifndef RC_RANDOM_H
#define RC_RANDOM_H

#include <stddef.h>
#include <stdint.h>

/*
 * Brings up the entropy service. Returns 1 on success, 0 on failure - and a caller that gets 0 must
 * ABORT the connection rather than continue with whatever is in the buffer. Call once at startup.
 */
int rc_random_init(void);

/* Tears the service down. Safe to call whether or not init succeeded. */
void rc_random_exit(void);

/*
 * Fills `out` with `length` cryptographically secure bytes. Returns 1 on success, 0 on failure.
 *
 * On failure `out` is zeroed rather than left with stale stack contents - not because zeros are safe (an
 * all-zero key is the worst possible one) but so that a caller who ignores the return value produces an
 * obviously broken, greppable key instead of something that looks random enough to ship.
 */
int rc_random_bytes(uint8_t *out, size_t length);

/*
 * The rc_ecdh.h-shaped adaptor, so this can be passed straight to rc_ecdh_generate(). `ctx` is unused.
 * Returns 0 on success and non-zero on failure, matching mbedtls's f_rng convention (the INVERSE of the
 * two functions above - a mismatch here would make every failure look like a success).
 */
int rc_random_rng_callback(void *ctx, uint8_t *out, size_t length);

#endif /* RC_RANDOM_H */
