/* See rc_core_tests.h. Each runner keeps its own main(); the build renames them with -Dmain=<x>_main. */
#include "rc_core_tests.h"

#include <stdio.h>
#include <string.h>

int discovery_test_main(void);
int session_test_main(void);
int takion_test_main(void);
int fec_test_main(void);
int stream_header_test_main(void);
int stream_demux_test_main(void);
int input_test_main(void);

/* The vector-backed four keep main(int, char **) because they take the .kat path as argv[1]. */
int vector_runner_main(int argc, char **argv);
int stream_crypto_test_main(int argc, char **argv);
int ecdh_test_main(int argc, char **argv);
int control_proto_test_main(int argc, char **argv);

/*
 * Runs one vector-backed suite, or reports it skipped if its file is not on the console. The existence
 * check is here rather than inside the runners because they are the core's tests, built for a target
 * they were not written for, and "the operator did not copy a file across" is this port's problem
 * rather than theirs.
 */
static int run_with_vectors(rc_core_test_result *r, const char *name, const char *leaf,
                            int (*fn)(int, char **))
{
    static char path[256];
    char *argv[2];
    FILE *probe;

    r->name = name;
    r->skipped = 0;

    (void)snprintf(path, sizeof(path), "%s%s", RC_CORE_TEST_VECTOR_DIR, leaf);

    probe = fopen(path, "r");
    if (probe == NULL) {
        r->skipped = 1;
        r->exit_code = 0;
        return 0;
    }
    fclose(probe);

    argv[0] = (char *)name;
    argv[1] = path;
    r->exit_code = fn(2, argv);
    return r->exit_code;
}

int rc_core_tests_run(rc_core_test_result *results)
{
    int failed = 0;
    int i = 0;

    memset(results, 0, sizeof(*results) * (size_t)RC_CORE_TEST_COUNT);

    /*
     * Order matters only for reading the log. FEC is last of the cheap ones and much the largest - 2,654
     * of the 2,912 assertions - so a failure anywhere else shows up before the long one runs.
     */
    results[i].name = "discovery";     results[i].exit_code = discovery_test_main();     i++;
    results[i].name = "session";       results[i].exit_code = session_test_main();       i++;
    results[i].name = "takion";        results[i].exit_code = takion_test_main();        i++;
    results[i].name = "stream_header"; results[i].exit_code = stream_header_test_main(); i++;
    results[i].name = "stream_demux";  results[i].exit_code = stream_demux_test_main();  i++;
    results[i].name = "input";         results[i].exit_code = input_test_main();         i++;
    results[i].name = "fec";           results[i].exit_code = fec_test_main();           i++;

    /*
     * The vector-backed four, last, because they are the ones that can be absent - a run without the
     * .kat files still gets the whole picture above it before anything reports a skip.
     */
    run_with_vectors(&results[i++], "control_crypto", "control-crypto.kat", vector_runner_main);
    run_with_vectors(&results[i++], "stream_crypto",  "stream-crypto.kat",  stream_crypto_test_main);
    run_with_vectors(&results[i++], "ecdh",           "session-crypto.kat", ecdh_test_main);
    run_with_vectors(&results[i++], "control_proto",  "control-proto.kat",  control_proto_test_main);

    for (i = 0; i < RC_CORE_TEST_COUNT; i++)
        if (results[i].exit_code != 0)
            failed++;

    return failed;
}
