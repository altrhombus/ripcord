/*
 * ripcord - account ("web"/no-PIN) pairing: known answers, and the flow against a fake console.
 *
 * Reads vectors/account-pairing.kat, produced by
 *
 *     dotnet run --project tools/Ripcord.ProtocolLab -- vectors
 *
 * and checks three layers against it: seed recovery from customData1 (halyard_account_seed.h), the
 * account route's material wrap and key (halyard_registration.h), and a whole /sess/rgst exchange,
 * request bytes and reply, (halyard_account_regist.h). Every answer in that file was computed by the
 * .NET implementation from synthetic inputs; every answer here is computed by the C one.
 *
 * Then it runs halyard_account_regist_run against a fake console that does the console's half with this
 * core's own inverse functions - gather, account unwrap, the seed key - and answers with an encrypted
 * record. That cannot prove a console accepts our bytes; it proves the flow assembles the layers in the
 * right order and that each failure it can reach is reported as the right status.
 *
 * WHY THE VECTORS MATTER HERE IN PARTICULAR. The account route's last real bug was a material wrapped
 * with the PIN route's transform: byte-perfect in every other respect, refused with 403 / 80108b09, and
 * nothing on the client's side of the wire could have said why. The same mistake in a C port would look
 * identical. It is caught here, on a host, against numbers the reference computed.
 */
#include "../halyard/halyard_account_seed.h"
#include "../halyard/halyard_registration.h"
#include "../halyard/halyard_v1.h"
#include "../session/halyard_account_regist.h"
#include "../session/halyard_account_regist_flow.h"
#include "../util/rc_base64.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/*
 * The test's own "randomness", and it is a counter on purpose - see regist_flow_test.c for why the host
 * seam refuses to provide one. Nothing it produces leaves this process.
 */
static uint8_t g_counter;

/* The longest customData1 wire string: base64 of base64 of the largest accepted ciphertext. */
#define RC_ACCOUNT_ENCODED_MAX \
    RC_BASE64_ENCODED_SIZE(RC_BASE64_ENCODED_SIZE(HALYARD_ACCOUNT_SEED_MAX_CIPHERTEXT))

int rc_random_bytes(uint8_t *out, size_t length)
{
    size_t i;

    for (i = 0; i < length; i++)
        out[i] = (uint8_t)(g_counter++ * 29u + 11u);
    return 1;
}

static int g_passed;
static int g_failed;

static void check(int condition, const char *what, int line)
{
    if (condition) {
        g_passed++;
    } else {
        g_failed++;
        printf("FAIL (line %d): %s\n", line, what);
    }
}

static int unhex(const char *text, uint8_t *out, size_t out_size, size_t *out_length)
{
    size_t n = strlen(text);
    size_t i;

    if ((n & 1u) != 0u || n / 2u > out_size)
        return 0;
    for (i = 0; i < n; i += 2) {
        unsigned value;

        if (sscanf(text + i, "%2x", &value) != 1)
            return 0;
        out[i / 2] = (uint8_t)value;
    }
    *out_length = n / 2u;
    return 1;
}

/* ---- the known-answer vectors ---- */

static int run_seed(char *rest)
{
    char *tok[6];
    uint8_t data1[16], data2[16], seed[16], ct[HALYARD_ACCOUNT_SEED_MAX_CIPHERTEXT];
    uint8_t got[16], decoded[HALYARD_ACCOUNT_SEED_MAX_CIPHERTEXT];
    char encoded[RC_ACCOUNT_ENCODED_MAX];
    size_t n, ct_len;
    int is_ps5;
    int i;

    for (i = 0; i < 6; i++) {
        tok[i] = strtok(i == 0 ? rest : NULL, " \r\n");
        if (tok[i] == NULL)
            return 0;
    }
    is_ps5 = atoi(tok[0]);
    if (!unhex(tok[1], data1, 16, &n) || n != 16 || !unhex(tok[2], data2, 16, &n) || n != 16
        || !unhex(tok[3], seed, 16, &n) || n != 16 || !unhex(tok[4], ct, sizeof(ct), &ct_len))
        return 0;

    n = halyard_account_seed_decode_custom_data1(tok[5], strlen(tok[5]), decoded, sizeof(decoded));
    check(n == ct_len && memcmp(decoded, ct, ct_len) == 0,
          "customData1 double-base64 decodes to the reference ciphertext", __LINE__);

    check(halyard_account_seed_recover(is_ps5, data1, data2, ct, ct_len, got), "recover succeeds", __LINE__);
    check(memcmp(got, seed, 16) == 0, "seed matches the reference", __LINE__);

    memset(got, 0, sizeof(got));
    check(halyard_account_seed_recover_custom_data1(is_ps5, data1, data2, tok[5], strlen(tok[5]), got)
              && memcmp(got, seed, 16) == 0,
          "seed recovered straight from the wire string", __LINE__);

    /* The encoder and the console-side seal, both reproduced byte for byte. */
    n = halyard_account_seed_encode_custom_data1(ct, ct_len, encoded, sizeof(encoded));
    check(n == strlen(tok[5]) && strcmp(encoded, tok[5]) == 0, "customData1 encodes identically", __LINE__);
    if (ct_len == 16) {
        uint8_t sealed[16];

        halyard_account_seed_seal(is_ps5, data1, data2, seed, sealed);
        check(memcmp(sealed, ct, 16) == 0, "seal reproduces the reference ciphertext", __LINE__);
    }

    /* The OTHER family's context key must not recover it - otherwise the family was never used. */
    check(halyard_account_seed_recover(!is_ps5, data1, data2, ct, ct_len, got) && memcmp(got, seed, 16) != 0,
          "the other family's context key yields noise", __LINE__);

    /* And data1/data2 have roles: swapped, they give noise, which is the silent failure to guard. */
    check(halyard_account_seed_recover(is_ps5, data2, data1, ct, ct_len, got) && memcmp(got, seed, 16) != 0,
          "data1 is the key and data2 the material, not the other way round", __LINE__);
    return 1;
}

static int run_wrap(char *rest)
{
    char *tok[4];
    uint8_t context[HALYARD_REGISTRATION_CONTEXT_LENGTH], material[16], want[16], got[16], back[16];
    uint8_t pin_wrap[16];
    size_t ctx_len, n;
    int is_ps5;
    int i;

    for (i = 0; i < 4; i++) {
        tok[i] = strtok(i == 0 ? rest : NULL, " \r\n");
        if (tok[i] == NULL)
            return 0;
    }
    is_ps5 = atoi(tok[0]);
    if (!unhex(tok[1], context, sizeof(context), &ctx_len) || !unhex(tok[2], material, 16, &n) || n != 16
        || !unhex(tok[3], want, 16, &n) || n != 16)
        return 0;

    check(halyard_registration_wrap_account_material(is_ps5, material, context, ctx_len, got),
          "account wrap succeeds", __LINE__);
    check(memcmp(got, want, 16) == 0, "account wrap matches the reference", __LINE__);
    check(halyard_registration_unwrap_account_material(is_ps5, got, context, ctx_len, back)
              && memcmp(back, material, 16) == 0,
          "account unwrap returns the material", __LINE__);

    /*
     * The two transforms must DISAGREE. .NET asserts the same, so they cannot be quietly unified later:
     * unifying them is exactly the bug that cost the account route its last week.
     */
    check(halyard_registration_wrap_material(is_ps5, material, context, ctx_len, pin_wrap)
              && memcmp(pin_wrap, got, 16) != 0,
          "the PIN route's wrap is a different transform", __LINE__);
    return 1;
}

static int run_rgst(char *rest)
{
    char *tok[12];
    static uint8_t random_context[HALYARD_REGISTRATION_CONTEXT_LENGTH];
    static uint8_t body_want[HALYARD_ACCOUNT_REGIST_MAX_BODY], body_got[HALYARD_ACCOUNT_REGIST_MAX_BODY];
    static uint8_t reply_plain[HALYARD_ACCOUNT_REGIST_MAX_RECORD], reply_cipher[HALYARD_ACCOUNT_REGIST_MAX_RECORD];
    static uint8_t response[HALYARD_ACCOUNT_REGIST_MAX_RECORD + 256];
    static uint8_t request[HALYARD_ACCOUNT_REGIST_MAX_BODY + 512];
    uint8_t material[16], seed[16], key_want[16], key_got[16], regkey[16], companion[16];
    size_t ctx_len, body_len, plain_len, cipher_len, regkey_len, n, got_len;
    halyard_account_regist_exchange ex;
    halyard_regist_record rec;
    halyard_account_regist_status status;
    int is_ps5, key_type, http_status;
    char reason[32];
    int head;
    int i;

    for (i = 0; i < 12; i++) {
        tok[i] = strtok(i == 0 ? rest : NULL, " \r\n");
        if (tok[i] == NULL)
            return 0;
    }
    is_ps5 = atoi(tok[0]);
    key_type = atoi(tok[11]);
    if (!unhex(tok[2], random_context, sizeof(random_context), &ctx_len)
        || ctx_len != HALYARD_REGISTRATION_CONTEXT_LENGTH
        || !unhex(tok[3], material, 16, &n) || n != 16
        || !unhex(tok[4], seed, 16, &n) || n != 16
        || !unhex(tok[5], key_want, 16, &n) || n != 16
        || !unhex(tok[6], body_want, sizeof(body_want), &body_len)
        || !unhex(tok[7], reply_plain, sizeof(reply_plain), &plain_len)
        || !unhex(tok[8], reply_cipher, sizeof(reply_cipher), &cipher_len)
        || !unhex(tok[9], regkey, sizeof(regkey), &regkey_len)
        || !unhex(tok[10], companion, 16, &n) || n != 16)
        return 0;

    /* The body, byte for byte: context with the material scattered in, then the encrypted field. */
    got_len = halyard_account_regist_build_body(&ex, is_ps5, seed, tok[1], random_context, material,
                                                body_got, sizeof(body_got));
    check(got_len == body_len, "body length matches the reference", __LINE__);
    check(got_len == body_len && memcmp(body_got, body_want, body_len) == 0,
          "body matches the reference byte for byte", __LINE__);
    check(memcmp(ex.field.key, key_want, 16) == 0, "the exchange holds the reference's seed key", __LINE__);

    check(halyard_registration_derive_account_key(is_ps5, body_want, HALYARD_REGISTRATION_CONTEXT_LENGTH,
                                                  seed, key_got)
              && memcmp(key_got, key_want, 16) == 0,
          "seed XOR table key matches, from the context as sent", __LINE__);

    /* The console's side of the request: gather, unwrap with the account transform. */
    {
        uint8_t wrapped[16], recovered[16];

        check(halyard_registration_gather(body_want, HALYARD_REGISTRATION_CONTEXT_LENGTH, wrapped)
                  && halyard_registration_unwrap_account_material(is_ps5, wrapped, body_want,
                                                                  HALYARD_REGISTRATION_CONTEXT_LENGTH,
                                                                  recovered)
                  && memcmp(recovered, material, 16) == 0,
              "the console recovers the material from the context as sent", __LINE__);
    }

    /* The whole request: the PIN route's head over this body. */
    n = halyard_account_regist_build_request(&ex, is_ps5, seed, tok[1], "192.0.2.10", random_context,
                                             material, request, sizeof(request));
    check(n > body_len && memcmp(request + n - body_len, body_want, body_len) == 0,
          "the request ends in the reference body", __LINE__);
    check(memcmp(request, is_ps5 ? "POST /sie/ps5/rp/sess/rgst " : "POST /sie/ps4/rp/sess/rgst ", 27) == 0,
          "and starts with the family's rgst path", __LINE__);

    /* The reply, behind a 200 head. */
    head = snprintf((char *)response, sizeof(response),
                    "HTTP/1.1 200 OK\r\nRP-SupportGcm: 1\r\nContent-Length: %u\r\n\r\n", (unsigned)cipher_len);
    if (head < 0 || (size_t)head + cipher_len > sizeof(response))
        return 0;
    memcpy(response + head, reply_cipher, cipher_len);

    check(halyard_account_regist_response_complete(response, (size_t)head + cipher_len),
          "a whole reply is complete", __LINE__);
    check(!halyard_account_regist_response_complete(response, (size_t)head + cipher_len - 1u),
          "one byte short is not", __LINE__);

    status = halyard_account_regist_open_response(&ex, response, (size_t)head + cipher_len, &http_status,
                                                  reason, sizeof(reason), &rec);
    check(status == HALYARD_ACCOUNT_REGIST_OK, "reply opens", __LINE__);
    check(http_status == 200, "status reported", __LINE__);
    check(rec.is_ps5 == is_ps5, "family from the RegistKey field name", __LINE__);
    check(rec.registration_key_length == regkey_len && memcmp(rec.registration_key, regkey, regkey_len) == 0,
          "registkey matches the reference's parse", __LINE__);
    check(memcmp(rec.companion, companion, 16) == 0, "companion matches", __LINE__);
    check(rec.key_type == key_type, "key type matches", __LINE__);

    /* The plaintext itself, through the exchange's field. */
    {
        static uint8_t opened[HALYARD_ACCOUNT_REGIST_MAX_RECORD];

        halyard_control_field_decrypt(&ex.field, HALYARD_REGISTRATION_FIELD_COUNTER, reply_cipher, opened,
                                      cipher_len);
        check(cipher_len == plain_len && memcmp(opened, reply_plain, plain_len) == 0,
              "reply decrypts to the reference plaintext", __LINE__);
    }

    /* A wrong seed is a bad record, not a crash and not a success. */
    seed[0] ^= 0x01;
    halyard_account_regist_build_body(&ex, is_ps5, seed, tok[1], random_context, material, body_got,
                                      sizeof(body_got));
    status = halyard_account_regist_open_response(&ex, response, (size_t)head + cipher_len, NULL, NULL, 0,
                                                  &rec);
    check(status == HALYARD_ACCOUNT_REGIST_ERR_BAD_RECORD, "a wrong seed is reported as a bad record",
          __LINE__);
    return 1;
}

/* ---- things the vectors do not reach ---- */

static void test_seed_edges(void)
{
    uint8_t out[HALYARD_ACCOUNT_SEED_MAX_CIPHERTEXT];
    uint8_t d[16] = { 0 };
    uint8_t seed[16];
    char wire[RC_ACCOUNT_ENCODED_MAX];
    uint8_t short_ct[15] = { 0 };

    check(halyard_account_seed_decode_custom_data1("", 0, out, sizeof(out)) == 0, "empty refused", __LINE__);
    check(halyard_account_seed_decode_custom_data1("not base64!", 11, out, sizeof(out)) == 0,
          "outer layer that is not base64 is refused", __LINE__);
    /* "AAAA" is valid base64 for three zero bytes, which are not base64 text themselves. */
    check(halyard_account_seed_decode_custom_data1("AAAA", 4, out, sizeof(out)) == 0,
          "an inner layer that is not base64 text is refused", __LINE__);
    check(!halyard_account_seed_recover(1, d, d, short_ct, sizeof(short_ct), seed),
          "a ciphertext shorter than the seed is refused", __LINE__);

    /* Too long for the bound: 65 bytes encode fine and must still be refused on the way in. */
    {
        uint8_t big[HALYARD_ACCOUNT_SEED_MAX_CIPHERTEXT + 1];

        memset(big, 0x5a, sizeof(big));
        check(halyard_account_seed_encode_custom_data1(big, sizeof(big), wire, sizeof(wire)) == 0,
              "the encoder shares the bound", __LINE__);
    }
}

static void test_response_edges(void)
{
    halyard_account_regist_exchange ex;
    halyard_regist_record rec;
    int http_status = 0;
    char reason[32];
    static const char refusal[] =
        "HTTP/1.1 403 Forbidden\r\nRP-Application-Reason: 80108b09\r\nContent-Length: 0\r\n\r\n";
    static const char no_length[] = "HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nabc";
    static const char bad_length[] = "HTTP/1.1 200 OK\r\ncontent-length: 12x\r\n\r\n";
    static const char head_only[] = "HTTP/1.1 200 OK\r\nContent-Length: 3\r\n";

    memset(&ex, 0, sizeof(ex));

    check(halyard_account_regist_open_response(&ex, (const uint8_t *)refusal, sizeof(refusal) - 1,
                                               &http_status, reason, sizeof(reason), &rec)
              == HALYARD_ACCOUNT_REGIST_ERR_REFUSED,
          "a 403 is a refusal", __LINE__);
    check(http_status == 403 && strcmp(reason, "80108b09") == 0,
          "with the console's own reason kept", __LINE__);

    check(halyard_account_regist_open_response(&ex, (const uint8_t *)"garbage", 7, &http_status, reason,
                                               sizeof(reason), &rec)
              == HALYARD_ACCOUNT_REGIST_ERR_MALFORMED,
          "no HTTP head is malformed", __LINE__);

    check(halyard_account_regist_response_complete((const uint8_t *)no_length, sizeof(no_length) - 1),
          "no Content-Length: complete once the head ends", __LINE__);
    check(halyard_account_regist_response_complete((const uint8_t *)bad_length, sizeof(bad_length) - 1),
          "an unreadable Content-Length is treated as absent", __LINE__);
    check(!halyard_account_regist_response_complete((const uint8_t *)head_only, sizeof(head_only) - 1),
          "an unterminated head is not complete", __LINE__);
}

/* ---- the flow, against a fake console ---- */

typedef enum { FAKE_OK, FAKE_REFUSE, FAKE_WRONG_SEED, FAKE_DEAD } fake_mode;

static struct {
    fake_mode mode;
    int is_ps5;
    uint8_t seed[16];
    int saw_field;
} g_fake;

/*
 * The console's half, written with this core's inverse functions: find the body, recover the material
 * with the ACCOUNT unwrap, derive the seed key, check the field says what a client's must, and answer.
 */
static int fake_console(void *user, const uint8_t *request, size_t request_length,
                        uint8_t *response, size_t response_size, size_t *out_response_length)
{
    static const char record[] =
        "PS5-RegistKey: 3161326233633464\r\nRP-Key: 000102030405060708090a0b0c0d0e0f\r\nRP-KeyType: 2\r\n";
    const uint8_t *body = NULL;
    size_t body_len, i;
    uint8_t wrapped[16], material[16], seed[16];
    uint8_t field_plain[HALYARD_ACCOUNT_REGIST_MAX_FIELD];
    halyard_control_field field;
    int head;

    (void)user;
    if (g_fake.mode == FAKE_DEAD)
        return 0;

    for (i = 0; i + 3 < request_length; i++) {
        if (memcmp(request + i, "\r\n\r\n", 4) == 0) {
            body = request + i + 4;
            break;
        }
    }
    if (body == NULL)
        return 0;
    body_len = request_length - (size_t)(body - request);
    if (body_len <= HALYARD_REGISTRATION_CONTEXT_LENGTH
        || body_len - HALYARD_REGISTRATION_CONTEXT_LENGTH > sizeof(field_plain))
        return 0;

    if (g_fake.mode == FAKE_REFUSE) {
        head = snprintf((char *)response, response_size,
                        "HTTP/1.1 403 Forbidden\r\nRP-Application-Reason: 80108bff\r\nContent-Length: 0\r\n\r\n");
        *out_response_length = (size_t)head;
        return 1;
    }

    memcpy(seed, g_fake.seed, 16);
    if (g_fake.mode == FAKE_WRONG_SEED)
        seed[15] ^= 0x80;

    if (!halyard_registration_gather(body, HALYARD_REGISTRATION_CONTEXT_LENGTH, wrapped)
        || !halyard_registration_unwrap_account_material(g_fake.is_ps5, wrapped, body,
                                                         HALYARD_REGISTRATION_CONTEXT_LENGTH, material)
        || !halyard_registration_account_field_init(&field, g_fake.is_ps5, body,
                                                    HALYARD_REGISTRATION_CONTEXT_LENGTH, seed, material))
        return 0;

    halyard_control_field_decrypt(&field, HALYARD_REGISTRATION_FIELD_COUNTER,
                                  body + HALYARD_REGISTRATION_CONTEXT_LENGTH, field_plain,
                                  body_len - HALYARD_REGISTRATION_CONTEXT_LENGTH);
    g_fake.saw_field = memcmp(field_plain, "Client-Type: " HALYARD_REGIST_CLIENT_TYPE_HEX "\r\nNp-AccountId: ",
                              13 + 64 + 2 + 14) == 0;

    head = snprintf((char *)response, response_size, "HTTP/1.1 200 OK\r\nContent-Length: %u\r\n\r\n",
                    (unsigned)(sizeof(record) - 1));
    if (head < 0 || (size_t)head + sizeof(record) - 1 > response_size)
        return 0;
    halyard_control_field_encrypt(&field, HALYARD_REGISTRATION_FIELD_COUNTER, (const uint8_t *)record,
                                  response + head, sizeof(record) - 1);
    *out_response_length = (size_t)head + sizeof(record) - 1;
    return 1;
}

static void test_flow(int is_ps5)
{
    halyard_account_regist_params params;
    halyard_account_regist_result result;
    uint8_t data1[16], data2[16];
    uint8_t ct[16];
    char wire[RC_ACCOUNT_ENCODED_MAX];

    /* The cloud tier's half, played here: data1/data2 out, the console's customData1 back. */
    check(halyard_account_regist_generate_key_material(data1, data2), "key material generated", __LINE__);
    check(memcmp(data1, data2, 16) != 0, "two independent draws", __LINE__);

    memset(&params, 0, sizeof(params));
    params.is_ps5 = is_ps5;
    strcpy(params.account_id, "1234567890123456");
    strcpy(params.client_ip, "192.0.2.5");

    memset(&g_fake, 0, sizeof(g_fake));
    g_fake.is_ps5 = is_ps5;
    rc_random_bytes(g_fake.seed, 16);               /* "the console" chooses the seed */
    halyard_account_seed_seal(is_ps5, data1, data2, g_fake.seed, ct);
    check(halyard_account_seed_encode_custom_data1(ct, sizeof(ct), wire, sizeof(wire)) > 0,
          "the console publishes customData1", __LINE__);
    check(halyard_account_seed_recover_custom_data1(is_ps5, data1, data2, wire, strlen(wire), params.seed)
              && memcmp(params.seed, g_fake.seed, 16) == 0,
          "and the client recovers the console's seed", __LINE__);

    g_fake.mode = FAKE_OK;
    check(halyard_account_regist_run(&params, fake_console, NULL, &result), "the flow registers", __LINE__);
    check(result.status == HALYARD_ACCOUNT_REGIST_OK && result.http_status == 200, "status OK", __LINE__);
    check(g_fake.saw_field, "the console read a correct field under the seed key", __LINE__);
    check(result.record.registration_key_length == 8 && memcmp(result.record.registration_key, "1a2b3c4d", 8) == 0,
          "and the client read the record", __LINE__);

    g_fake.mode = FAKE_REFUSE;
    check(!halyard_account_regist_run(&params, fake_console, NULL, &result)
              && result.status == HALYARD_ACCOUNT_REGIST_ERR_REFUSED && result.http_status == 403
              && strcmp(result.console_reason, "80108bff") == 0,
          "a refusal says so, with the console's reason", __LINE__);

    g_fake.mode = FAKE_WRONG_SEED;
    check(!halyard_account_regist_run(&params, fake_console, NULL, &result)
              && result.status == HALYARD_ACCOUNT_REGIST_ERR_BAD_RECORD,
          "a console using another seed is a bad record", __LINE__);

    g_fake.mode = FAKE_DEAD;
    check(!halyard_account_regist_run(&params, fake_console, NULL, &result)
              && result.status == HALYARD_ACCOUNT_REGIST_ERR_TRANSPORT,
          "a dead association is a transport failure", __LINE__);

    params.client_ip[0] = '\0';
    check(!halyard_account_regist_run(&params, fake_console, NULL, &result)
              && result.status == HALYARD_ACCOUNT_REGIST_ERR_BAD_PARAMS,
          "no client address is refused before anything is sent", __LINE__);
    check(!halyard_account_regist_run(&params, NULL, NULL, &result)
              && result.status == HALYARD_ACCOUNT_REGIST_ERR_BAD_PARAMS,
          "no transport is refused too", __LINE__);
}

int main(int argc, char **argv)
{
    const char *path = (argc > 1) ? argv[1] : "vectors/account-pairing.kat";
    static char line[8192];
    FILE *f;
    int seeds = 0, wraps = 0, rgsts = 0;

    /*
     * Seed recovery needs only the control context keys, which every build carries; the account wrap
     * and key need the registration tables, which a legitimate build may omit. Said, not silently skipped.
     */
    if (!halyard_registration_available()) {
        printf("account_pairing_test: this build carries no registration tables - nothing to check\n");
        return 0;
    }

    f = fopen(path, "r");
    if (f == NULL) {
        printf("account_pairing_test: cannot open %s\n", path);
        return 1;
    }

    while (fgets(line, sizeof(line), f) != NULL) {
        char *space = strchr(line, ' ');
        int is_ps5_line;

        if (space == NULL || line[0] == '#')
            continue;
        *space = '\0';
        is_ps5_line = space[1] == '1';
        if (!is_ps5_line && !halyard_v1_has_ps4_registration)
            continue;

        if (strcmp(line, "seed") == 0) {
            if (run_seed(space + 1)) seeds++; else { g_failed++; printf("FAIL: unparsable seed vector\n"); }
        } else if (strcmp(line, "accountwrap") == 0) {
            if (run_wrap(space + 1)) wraps++; else { g_failed++; printf("FAIL: unparsable wrap vector\n"); }
        } else if (strcmp(line, "accountrgst") == 0) {
            if (run_rgst(space + 1)) rgsts++; else { g_failed++; printf("FAIL: unparsable rgst vector\n"); }
        }
    }
    fclose(f);

    /* An empty or truncated file would otherwise pass, which is the failure this runner exists against. */
    if (seeds == 0 || wraps == 0 || rgsts == 0) {
        printf("FAIL: missing vectors in %s (seed %d, accountwrap %d, accountrgst %d)\n", path, seeds, wraps,
               rgsts);
        g_failed++;
    }

    test_seed_edges();
    test_response_edges();
    test_flow(1);
    if (halyard_v1_has_ps4_registration)
        test_flow(0);

    printf("\n%d passed, %d failed (seed %d, accountwrap %d, accountrgst %d)\n", g_passed, g_failed, seeds,
           wraps, rgsts);
    return g_failed == 0 ? 0 : 1;
}
