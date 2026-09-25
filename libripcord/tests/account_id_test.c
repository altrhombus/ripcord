/*
 * ripcord - reading a PSN account id somebody typed in.
 *
 * WHAT IS WORTH TESTING is not that "123" parses. It is the two ways this can go wrong quietly. A value
 * that is accepted and misread travels as a wrong id, and a wrong id is not a loud failure - it is a
 * registration the console refuses for reasons it does not explain, in front of someone who has just
 * re-checked a number that was right all along. So the cases below are about what must be REFUSED, and
 * about the one input that has two valid readings.
 */
#include "../session/halyard_account_id.h"

#include <stdio.h>
#include <string.h>

static int g_failures;

static void check(int ok, const char *what)
{
    if (!ok) {
        printf("  FAIL %s\n", what);
        g_failures++;
    }
}

static void accepts(const char *in, const char *expect, const char *what)
{
    char out[HALYARD_ACCOUNT_ID_TEXT_MAX];
    halyard_account_id_status status = halyard_account_id_normalise(in, out, sizeof(out));

    if (status != HALYARD_ACCOUNT_ID_OK) {
        printf("  FAIL %s (refused: %s)\n", what, halyard_account_id_status_text(status));
        g_failures++;
        return;
    }
    if (strcmp(out, expect) != 0) {
        printf("  FAIL %s (got %s, wanted %s)\n", what, out, expect);
        g_failures++;
    }
}

static void refuses(const char *in, halyard_account_id_status expect, const char *what)
{
    char out[HALYARD_ACCOUNT_ID_TEXT_MAX];
    halyard_account_id_status status = halyard_account_id_normalise(in, out, sizeof(out));

    if (status != expect) {
        printf("  FAIL %s (status %d, wanted %d)\n", what, (int)status, (int)expect);
        g_failures++;
        return;
    }
    check(out[0] == '\0', "a refused input writes nothing");
}

/*
 * NOT A REAL ACCOUNT ID. 0x0123456789ABCDEF is a counting pattern, chosen so the hex and the decimal in
 * each case below can be checked by hand and so nothing here resembles anybody's account.
 */
#define PATTERN_HEX  "0123456789abcdef"
#define PATTERN_DEC  "81985529216486895"

static void test_decimal(void)
{
    accepts(PATTERN_DEC, PATTERN_DEC, "a decimal id passes through unchanged");
    accepts("1", "1", "a short decimal is not rejected for being short - the console judges the value");
    accepts("18446744073709551615", "18446744073709551615", "the largest 64-bit value fits");
    refuses("18446744073709551616", HALYARD_ACCOUNT_ID_UNREADABLE,
            "one past it is refused rather than wrapped");
    refuses("999999999999999999999", HALYARD_ACCOUNT_ID_UNREADABLE, "and so is anything longer");
    refuses("0", HALYARD_ACCOUNT_ID_UNREADABLE, "zero is not an account id");
    refuses("0000000000000000000", HALYARD_ACCOUNT_ID_UNREADABLE, "however it is written");
}

static void test_hex(void)
{
    accepts("0x" PATTERN_HEX, PATTERN_DEC, "an 0x-prefixed hex id converts to decimal");
    accepts("0X" PATTERN_HEX, PATTERN_DEC, "and a capital X");
    accepts(PATTERN_HEX, PATTERN_DEC, "sixteen bare hex characters cannot be anything else");
    accepts("0xFF", "255", "a short prefixed hex value still converts");
    refuses("0x", HALYARD_ACCOUNT_ID_UNREADABLE, "a prefix with no digits is not a number");
    refuses("0x00000000000000000", HALYARD_ACCOUNT_ID_UNREADABLE, "over sixteen hex digits is refused");
    refuses("0xdefg", HALYARD_ACCOUNT_ID_UNREADABLE, "a non-hex digit after the prefix is refused");
    refuses("0x0", HALYARD_ACCOUNT_ID_UNREADABLE, "and hex zero is still zero");

    /*
     * FEWER THAN SIXTEEN BARE HEX CHARACTERS IS REFUSED, and that is deliberate. "1a2b3c4d" could be a
     * truncated hex id or it could be somebody's typo; without the prefix or the full width there is
     * nothing to tell them apart, and guessing produces a plausible wrong number.
     */
    refuses("1a2b3c4d", HALYARD_ACCOUNT_ID_UNREADABLE, "a partial bare hex string is not guessed at");
}

static void test_the_ambiguous_one(void)
{
    /*
     * Sixteen DECIMAL digits is simultaneously a valid decimal id and a valid hex string. Decimal is the
     * form the wire wants and the form every id this project has seen is quoted in, so it wins - and a
     * test says so, because the alternative reading is a silently different number.
     */
    accepts("1234567890123456", "1234567890123456", "sixteen digits are read as decimal, not as hex");
}

static void test_base64_is_named_not_decoded(void)
{
    /* Eleven characters, and twelve with the padding - what eight bytes base64 to. */
    refuses("AQIDBAUGBwg", HALYARD_ACCOUNT_ID_BASE64, "the base64 form is recognised");
    refuses("AQIDBAUGBwg=", HALYARD_ACCOUNT_ID_BASE64, "with its padding too");
    refuses("++++++++++++", HALYARD_ACCOUNT_ID_BASE64, "by its alphabet rather than by its content");
}

static void test_whitespace_and_empty(void)
{
    accepts("  " PATTERN_DEC "  ", PATTERN_DEC, "surrounding whitespace is ignored, not refused");
    accepts("\t0x" PATTERN_HEX "\r\n", PATTERN_DEC, "around a hex value as well");
    refuses(NULL, HALYARD_ACCOUNT_ID_EMPTY, "no input at all is empty rather than unreadable");
    refuses("", HALYARD_ACCOUNT_ID_EMPTY, "and so is an empty string");
    refuses("   \t\r\n ", HALYARD_ACCOUNT_ID_EMPTY, "and so is nothing but whitespace");
}

static void test_junk(void)
{
    refuses("not an id", HALYARD_ACCOUNT_ID_UNREADABLE, "words are refused");
    refuses("123-456-789", HALYARD_ACCOUNT_ID_UNREADABLE, "so is a number with separators in it");
    refuses("12345678901234567890123456", HALYARD_ACCOUNT_ID_UNREADABLE, "so is far too long");
}

static void test_a_short_buffer_is_refused_not_overrun(void)
{
    char small[4];
    halyard_account_id_status status = halyard_account_id_normalise(PATTERN_DEC, small, sizeof(small));

    check(status == HALYARD_ACCOUNT_ID_UNREADABLE, "a buffer too small to hold the answer is refused");
    check(halyard_account_id_normalise(PATTERN_DEC, NULL, 64u) == HALYARD_ACCOUNT_ID_UNREADABLE,
          "and so is no buffer at all");
}

int main(void)
{
    printf("account id: what must be refused\n");
    test_decimal();
    test_hex();
    test_the_ambiguous_one();
    test_base64_is_named_not_decoded();
    test_whitespace_and_empty();
    test_junk();
    test_a_short_buffer_is_refused_not_overrun();

    if (g_failures != 0) {
        printf("account id: %d failed\n", g_failures);
        return 1;
    }
    printf("account id: ok\n");
    return 0;
}
