/*
 * ripcord - the registration flow against a fake console on loopback.
 *
 * WHAT THIS PROVES AND WHAT IT DOES NOT. It cannot prove the console accepts our bytes; only a console
 * can. What it proves is that the flow assembles the three layers correctly and, above all, that every
 * failure it can reach reports the RIGHT ONE of its named statuses - because the whole argument for
 * having named statuses is that a user standing in front of two screens with an expiring PIN needs to
 * be told which thing went wrong, and a status that is merely plausible is worse than none.
 *
 * The fake console is a thread on 127.0.0.1 that speaks the shape of the protocol: it reads the POST,
 * recovers the material from the context exactly as the console must, derives the same key from the PIN
 * it "displayed", and answers with an encrypted pairing record. That it can do so at all is the real
 * check - it exercises unwrap and the key derivation from the far side, which the known-answer vectors
 * verify arithmetically but never actually use in the direction the console does.
 */
#include "../halyard/halyard_registration.h"
#include "../halyard/halyard_v1.h"
#include "../session/halyard_regist_flow.h"

#include <arpa/inet.h>
#include <netinet/in.h>
#include <pthread.h>
#include "../platform/rc_platform.h"

#include <stdio.h>
#include <string.h>
#include <sys/socket.h>
#include <unistd.h>

/*
 * THE TEST'S OWN RANDOMNESS, AND IT IS NOT RANDOM.
 *
 * rc_platform_host.c deliberately provides no rc_random_bytes: "offering a host one would make 'built
 * for the host' quietly mean 'built with fake key material'. A host test that needs randomness should
 * inject it, not source it." This is that injection, and it is a counter rather than a PRNG on purpose.
 *
 * It is safe here for one reason only: the sole peer is a fake console in this same process, and
 * nothing this produces leaves it. It would be catastrophic anywhere else, which is why it lives in the
 * test rather than behind a build flag that could one day be set by accident.
 */
static uint8_t g_counter;

int rc_random_bytes(uint8_t *out, size_t length)
{
    size_t i;

    for (i = 0; i < length; i++)
        out[i] = (uint8_t)(g_counter++ * 31u + 17u);
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

/* What the fake console should do with the request it receives. */
typedef enum {
    FAKE_OK,
    FAKE_REFUSE,        /* 403 with an application reason, as a real one does */
    FAKE_WRONG_PIN,     /* answer 200, but encrypted under a different PIN     */
    FAKE_SILENT         /* accept the connection and close without answering   */
} fake_mode;

static struct {
    int listener;
    unsigned short port;
    fake_mode mode;
    uint32_t pin;
    int is_ps5;
    int saw_request;
    size_t request_length;
} g_fake;

static void *fake_console(void *arg)
{
    int client;
    static uint8_t buf[4096];
    size_t total = 0;
    ssize_t n;

    (void)arg;
    /*
     * A BOUNDED ACCEPT, because an unbounded one is how this test hung.
     *
     * If the client never connects - which is what happens the moment anything upstream of the connect
     * fails - an accept() with no timeout blocks forever, and the pthread_join that follows it blocks
     * with it. The run then has to be killed, and a killed run LEAVES THE LISTENER BOUND, which breaks
     * every later run with a message about the port being in use. One missing timeout cost all of that.
     */
    {
        struct timeval tv;

        tv.tv_sec = 5;
        tv.tv_usec = 0;
        setsockopt(g_fake.listener, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof(tv));
    }
    client = accept(g_fake.listener, NULL, NULL);
    if (client < 0)
        return NULL;
    {
        struct timeval tv;

        tv.tv_sec = 5;
        tv.tv_usec = 0;
        setsockopt(client, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof(tv));
    }

    /* Read the head plus body. The request says Content-Length, but this only needs enough of it. */
    while (total < sizeof(buf)) {
        n = recv(client, buf + total, sizeof(buf) - total, 0);
        if (n <= 0)
            break;
        total += (size_t)n;
        if (total > 4 && memmem(buf, total, "\r\n\r\n", 4) != NULL) {
            const uint8_t *body = (const uint8_t *)memmem(buf, total, "\r\n\r\n", 4) + 4;
            size_t have = total - (size_t)(body - buf);

            if (have >= HALYARD_REGISTRATION_CONTEXT_LENGTH + 16u)
                break;
        }
    }
    g_fake.saw_request = 1;
    g_fake.request_length = total;

    if (g_fake.mode == FAKE_SILENT) {
        close(client);
        return NULL;
    }

    if (g_fake.mode == FAKE_REFUSE) {
        static const char refusal[] =
            "HTTP/1.1 403 Forbidden\r\n"
            "RP-Application-Reason: 80108bff\r\n"
            "Content-Length: 0\r\n"
            "\r\n";
        send(client, refusal, sizeof(refusal) - 1, 0);
        close(client);
        return NULL;
    }

    /*
     * The console's side of the exchange, which is the part worth having a fake for: recover the
     * material from the context the client sent, derive the key from the context and the PIN IT
     * displayed, and encrypt the record with it.
     */
    {
        const uint8_t *body = (const uint8_t *)memmem(buf, total, "\r\n\r\n", 4) + 4;
        uint8_t wrapped[16], material[16];
        halyard_control_field field;
        static const char record[] =
            "PS5-RegistKey: 3161326233633464\r\n"
            "RP-Key: 000102030405060708090a0b0c0d0e0f\r\n"
            "RP-KeyType: 2\r\n";
        uint8_t cipher[sizeof(record)];
        char head[128];
        uint32_t pin = (g_fake.mode == FAKE_WRONG_PIN) ? g_fake.pin + 1u : g_fake.pin;
        int head_len;

        halyard_registration_gather(body, HALYARD_REGISTRATION_CONTEXT_LENGTH, wrapped);
        halyard_registration_unwrap_material(g_fake.is_ps5, wrapped, body,
                                             HALYARD_REGISTRATION_CONTEXT_LENGTH, material);
        halyard_registration_field_init(&field, g_fake.is_ps5, body,
                                        HALYARD_REGISTRATION_CONTEXT_LENGTH, pin, material);

        memcpy(cipher, record, sizeof(record) - 1);
        halyard_control_field_encrypt(&field, HALYARD_REGISTRATION_FIELD_COUNTER,
                                      cipher, cipher, sizeof(record) - 1);

        head_len = snprintf(head, sizeof(head),
                            "HTTP/1.1 200 OK\r\nContent-Length: %u\r\n\r\n",
                            (unsigned)(sizeof(record) - 1));
        send(client, head, (size_t)head_len, 0);
        send(client, cipher, sizeof(record) - 1, 0);
    }
    close(client);
    return NULL;
}

static int start_fake(fake_mode mode, uint32_t pin, int is_ps5, pthread_t *thread)
{
    struct sockaddr_in addr;
    socklen_t len = sizeof(addr);

    g_fake.mode = mode;
    g_fake.pin = pin;
    g_fake.is_ps5 = is_ps5;
    g_fake.saw_request = 0;

    g_fake.listener = socket(AF_INET, SOCK_STREAM, 0);
    if (g_fake.listener < 0)
        return 0;
    {
        int one = 1;
        setsockopt(g_fake.listener, SOL_SOCKET, SO_REUSEADDR, &one, sizeof(one));
    }
    memset(&addr, 0, sizeof(addr));
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    /*
     * THE PROTOCOL'S PORT, not one the kernel picks. The flow hardcodes 9295 because that is what the
     * protocol says, not a parameter - so a fake console has to be there to be spoken to. Binding it on
     * loopback is what lets the WHOLE flow run here: framing, socket, response parsing and decryption,
     * with only the console's judgement missing.
     */
    addr.sin_port = htons(HALYARD_REGIST_PORT);
    if (bind(g_fake.listener, (struct sockaddr *)&addr, sizeof(addr)) != 0
        || listen(g_fake.listener, 1) != 0
        || getsockname(g_fake.listener, (struct sockaddr *)&addr, &len) != 0)
        return 0;
    g_fake.port = ntohs(addr.sin_port);
    return pthread_create(thread, NULL, fake_console, NULL) == 0;
}

/*
 * THE WHOLE FLOW, against the fake. The search probe goes to a loopback address nothing answers, which
 * is exactly the best-effort case the real one has to tolerate - so this also checks that an unanswered
 * probe does not stop the POST.
 */
static void test_full_flow(fake_mode mode, uint32_t console_pin, uint32_t client_pin,
                           halyard_regist_status expect, const char *what)
{
    pthread_t thread;
    halyard_regist_params p;
    halyard_regist_result r;

    if (!start_fake(mode, console_pin, 1, &thread)) {
        printf("SKIP: could not bind a fake console on %d (something else is using it?)\n",
               HALYARD_REGIST_PORT);
        return;
    }

    memset(&p, 0, sizeof(p));
    snprintf(p.host, sizeof(p.host), "127.0.0.1");
    snprintf(p.account_id, sizeof(p.account_id), "1234567890123456");
    snprintf(p.client_ip, sizeof(p.client_ip), "127.0.0.1");
    p.is_ps5 = 1;
    p.passcode = client_pin;

    (void)halyard_regist_run(&p, &r);
    pthread_join(thread, NULL);
    close(g_fake.listener);

    check(g_fake.saw_request, "the fake console received a request", __LINE__);
    check(r.status == expect, what, __LINE__);

    if (expect == HALYARD_REGIST_OK) {
        check(r.record.registration_key_length == 8, "the registkey came back decoded", __LINE__);
        check(memcmp(r.record.registration_key, "1a2b3c4d", 8) == 0, "and intact", __LINE__);
        check(r.record.is_ps5 == 1, "with the family the console named", __LINE__);
    }
    if (expect == HALYARD_REGIST_ERR_REFUSED) {
        check(r.http_status == 403, "the status is carried", __LINE__);
        check(strcmp(r.console_reason, "80108bff") == 0,
              "and the console's own reason with it", __LINE__);
    }
}

static void test_bad_params(void)
{
    halyard_regist_params p;
    halyard_regist_result r;

    memset(&p, 0, sizeof(p));
    check(!halyard_regist_run(&p, &r), "empty parameters fail", __LINE__);
    check(r.status == HALYARD_REGIST_ERR_BAD_PARAMS, "named as bad parameters", __LINE__);

    /* A host but no account id is still incomplete, and must not reach the network to find out. */
    snprintf(p.host, sizeof(p.host), "192.0.2.1");
    snprintf(p.client_ip, sizeof(p.client_ip), "192.0.2.2");
    check(!halyard_regist_run(&p, &r), "a missing account id fails", __LINE__);
    check(r.status == HALYARD_REGIST_ERR_BAD_PARAMS, "before any socket is opened", __LINE__);
}

/*
 * THE ROUND TRIP, driven through the same calls the flow makes. The console side is genuinely the far
 * side of the exchange: it unwraps the material the client wrapped and re-derives the key from the PIN,
 * so a disagreement between wrap and unwrap, or between the two key derivations, shows up here as an
 * unreadable record rather than as a passing test.
 */
static void test_round_trip(int is_ps5, uint32_t console_pin, uint32_t client_pin, int expect_ok)
{
    uint8_t context[HALYARD_REGISTRATION_CONTEXT_LENGTH];
    uint8_t material[16], wrapped[16];
    uint8_t recovered_wrapped[16], recovered_material[16];
    halyard_control_field client_field, console_field;
    static const char record[] =
        "PS5-RegistKey: 3161326233633464\r\n"
        "RP-Key: 000102030405060708090a0b0c0d0e0f\r\n"
        "RP-KeyType: 2\r\n";
    uint8_t buf[sizeof(record)];
    halyard_regist_record parsed;
    size_t i;

    for (i = 0; i < sizeof(context); i++)
        context[i] = (uint8_t)(i * 7u + 3u);
    for (i = 0; i < sizeof(material); i++)
        material[i] = (uint8_t)(0xa0u + i);

    check(halyard_registration_wrap_material(is_ps5, material, context, sizeof(context), wrapped),
          "client wraps the material", __LINE__);
    check(halyard_registration_scatter(wrapped, context, sizeof(context)),
          "and scatters it into the context", __LINE__);
    check(halyard_registration_field_init(&client_field, is_ps5, context, sizeof(context),
                                          client_pin, material),
          "client derives its field crypto", __LINE__);

    /* --- the console's side, from the context alone --- */
    check(halyard_registration_gather(context, sizeof(context), recovered_wrapped),
          "console gathers the wrapped material back out", __LINE__);
    check(halyard_registration_unwrap_material(is_ps5, recovered_wrapped, context, sizeof(context),
                                               recovered_material),
          "console unwraps it", __LINE__);
    check(memcmp(recovered_material, material, sizeof(material)) == 0,
          "and recovers exactly what the client sent", __LINE__);
    check(halyard_registration_field_init(&console_field, is_ps5, context, sizeof(context),
                                          console_pin, recovered_material),
          "console derives its own field crypto from the PIN it displayed", __LINE__);

    memcpy(buf, record, sizeof(record) - 1);
    halyard_control_field_encrypt(&console_field, HALYARD_REGISTRATION_FIELD_COUNTER,
                                  buf, buf, sizeof(record) - 1);
    halyard_control_field_decrypt(&client_field, HALYARD_REGISTRATION_FIELD_COUNTER,
                                  buf, buf, sizeof(record) - 1);

    if (expect_ok) {
        check(halyard_regist_parse_record(buf, sizeof(record) - 1, &parsed),
              "the client reads the record back", __LINE__);
        check(parsed.registration_key_length == 8 && parsed.companion[15] == 0x0f,
              "with the key and companion intact", __LINE__);
    } else {
        /*
         * A WRONG PIN DOES NOT FAIL LOUDLY. The bytes decrypt to something; they are simply not a
         * record. That is exactly why the flow's status for this case says "wrong PIN" rather than
         * "malformed" - the shape of the failure carries the diagnosis.
         */
        check(!halyard_regist_parse_record(buf, sizeof(record) - 1, &parsed),
              "a wrong PIN yields bytes that are not a record", __LINE__);
    }
}

int main(void)
{
    if (!halyard_registration_available()) {
        printf("regist_flow_test: this build carries no registration tables - nothing to check\n");
        return 0;
    }

    test_bad_params();

    test_full_flow(FAKE_OK, 12345678u, 12345678u, HALYARD_REGIST_OK,
                   "a good registration reports OK");
    test_full_flow(FAKE_REFUSE, 12345678u, 12345678u, HALYARD_REGIST_ERR_REFUSED,
                   "a 403 reports REFUSED, not a generic failure");
    test_full_flow(FAKE_SILENT, 12345678u, 12345678u, HALYARD_REGIST_ERR_NO_REPLY,
                   "silence after connecting reports NO_REPLY");
    test_full_flow(FAKE_WRONG_PIN, 12345678u, 12345678u, HALYARD_REGIST_ERR_BAD_RECORD,
                   "a record we cannot read reports the wrong PIN");

    test_round_trip(1, 12345678u, 12345678u, 1);
    test_round_trip(1, 12345678u, 12345679u, 0);
    if (halyard_v1_has_ps4_registration)
        test_round_trip(0, 87654321u, 87654321u, 1);

    printf("\n%d passed, %d failed\n", g_passed, g_failed);
    return g_failed == 0 ? 0 : 1;
}
