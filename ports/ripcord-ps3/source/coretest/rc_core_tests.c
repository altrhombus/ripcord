/* See rc_core_tests.h. Each runner keeps its own main(); the build renames them with -Dmain=<x>_main. */
#include "rc_core_tests.h"

int discovery_test_main(void);
int session_test_main(void);
int takion_test_main(void);
int fec_test_main(void);
int stream_header_test_main(void);
int stream_demux_test_main(void);
int input_test_main(void);

int rc_core_tests_run(rc_core_test_result *results)
{
    int failed = 0;
    int i = 0;

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

    for (i = 0; i < RC_CORE_TEST_COUNT; i++)
        if (results[i].exit_code != 0)
            failed++;

    return failed;
}
