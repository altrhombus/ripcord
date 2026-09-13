/*
 * ripcord-ps3 - run ports/common's own test suites on the console.
 *
 * WHY. Every assertion in ports/common has only ever been checked on little-endian x86. The PPE is
 * big-endian. openh264 was in exactly that position two days ago and the answer turned out to be fine -
 * but it was established by decoding 400 MB and comparing hashes, not by reading the source and
 * concluding it looked careful. The core deserves the same standard, and it is the code every port
 * shares: a latent byte-order assumption in the Takion reassembler or the FEC tables would surface as a
 * session that connects and then drops, on one platform, with nothing in the log.
 *
 * Reading says it should be clean - 28 sites assemble multi-byte values with explicit shifts and there
 * is not one multi-byte pointer cast in the transport, stream, session or util layers. That is an
 * argument. 2,912 assertions passing on the hardware is a result.
 *
 * SEVEN OF THE ELEVEN RUNNERS, and the split is not arbitrary: these are the ones that need no external
 * vector file. The other four read .kat files emitted by the .NET side, which means getting those onto
 * the console and is a second step rather than a harder one. What is covered here is FEC, the Takion
 * transport and reassembler, stream framing and demux, discovery, the control session and input
 * encoding - all of it pure logic over bytes, which is precisely where byte order hides.
 */
#ifndef RC_CORE_TESTS_H
#define RC_CORE_TESTS_H

#ifdef __cplusplus
extern "C" {
#endif

#define RC_CORE_TEST_COUNT 7

typedef struct {
    const char *name;
    int         exit_code;   /* 0 = every assertion in that runner passed */
} rc_core_test_result;

/*
 * Runs all seven. Returns the number that FAILED, so zero is success. `results` is filled in order and
 * must hold RC_CORE_TEST_COUNT entries.
 *
 * The runners print their own detail with printf, which on this platform goes to a TTY nobody is
 * reading, so the caller redirects stdout to a file first and replays it afterwards - see main.c. That
 * is why this returns codes rather than text: the text already exists and belongs to them.
 */
int rc_core_tests_run(rc_core_test_result *results);

#ifdef __cplusplus
}
#endif

#endif /* RC_CORE_TESTS_H */
