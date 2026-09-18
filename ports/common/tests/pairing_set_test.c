/*
 * ripcord - the paired-console list, on a host, against real files.
 *
 * WHY THIS IS WORTH A SUITE. The file it parses holds the only copy of material that cannot be
 * regenerated without standing in front of a console reading a PIN off its screen. Every failure here is
 * therefore expensive in a way an ordinary parser bug is not: losing an entry costs somebody a trip to
 * another room, and the single-console version of this file lost one every time a second console was
 * paired, silently.
 *
 * So the cases below are about PRESERVATION - that a file written before sections existed still loads,
 * that adding a console keeps the others, that removing one moves the cursor to the right place, and
 * that a full set is refused rather than quietly dropping the oldest.
 */
#include "../session/halyard_pairing_file.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static int g_failures;
static char g_dir[256];

static void check(int ok, const char *what)
{
    if (!ok) {
        printf("  FAIL %s\n", what);
        g_failures++;
    }
}

/*
 * NOT ANYBODY'S KEYS. Sixteen bytes of 0xAB and eight of 0xCD - the shapes the loader requires, chosen
 * so nothing in this file resembles real material.
 */
#define FAKE_REGISTKEY "cdcdcdcdcdcdcdcd"
#define FAKE_COMPANION "abababababababababababababababab"

static void write_file(const char *contents)
{
    char path[320];
    FILE *f;

    snprintf(path, sizeof(path), "%spairing.txt", g_dir);
    f = fopen(path, "w");
    if (f == NULL) {
        printf("  FAIL could not write %s\n", path);
        g_failures++;
        return;
    }
    fputs(contents, f);
    fclose(f);
}

static void test_a_file_from_before_sections_still_loads(void)
{
    halyard_pairing_set set;
    halyard_pairing_record rec;

    /* Exactly the layout the single-console version wrote: console keys at the top, no [console]. */
    write_file("host=10.0.0.1\n"
               "platform=ps5\n"
               "registkey=" FAKE_REGISTKEY "\n"
               "companion=" FAKE_COMPANION "\n"
               "fps=60\n"
               "streambitrate=20000\n");

    check(halyard_pairing_file_load_set(g_dir, &set) == 1, "a legacy file loads as one console");
    check(set.count == 1, "with exactly one entry");
    check(set.selected == 0, "which is selected");
    check(strcmp(set.console[0].host, "10.0.0.1") == 0, "its host came through");
    check(set.console[0].registkey_length == 8u, "and its key");
    check(set.console[0].fps == 60, "and the shared settings landed on it");
    check(set.console[0].stream_bitrate_kbps == 20000, "all of them");

    check(halyard_pairing_file_load(g_dir, &rec) == 1, "the single-record loader still works");
    check(strcmp(rec.host, "10.0.0.1") == 0, "and returns that console");
}

static void test_several_consoles(void)
{
    halyard_pairing_set set;

    write_file("fps=60\n"
               "selected=1\n"
               "\n[console]\nhost=10.0.0.1\nname=Front room\n"
               "registkey=" FAKE_REGISTKEY "\ncompanion=" FAKE_COMPANION "\n"
               "\n[console]\nhost=10.0.0.2\nname=Upstairs\nplatform=ps4\n"
               "registkey=" FAKE_REGISTKEY "\ncompanion=" FAKE_COMPANION "\n");

    check(halyard_pairing_file_load_set(g_dir, &set) == 2, "two sections load as two consoles");
    check(strcmp(set.console[0].name, "Front room") == 0, "the first keeps its name");
    check(strcmp(set.console[1].name, "Upstairs") == 0, "and so does the second");
    check(set.console[0].is_ps5 == 1, "platform defaults to ps5");
    check(set.console[1].is_ps5 == 0, "and is per console, not shared");
    check(set.selected == 1, "the stored selection is honoured");
    check(set.console[0].fps == 60 && set.console[1].fps == 60,
          "the shared settings are on BOTH - this is the invariant the writer relies on");

    check(halyard_pairing_set_find(&set, "10.0.0.2") == 1, "a console is found by address");
    check(halyard_pairing_set_find(&set, "10.0.0.9") == -1, "and one that is not there is not");
    check(halyard_pairing_set_find(&set, NULL) == -1, "nor is no address at all");
}

static void test_pairing_a_second_console_keeps_the_first(void)
{
    halyard_pairing_set set;
    halyard_pairing_record rec;

    /* This is the bug the whole change exists for: the old save wrote one record over the file. */
    write_file("fps=60\n"
               "\n[console]\nhost=10.0.0.1\nname=Front room\n"
               "registkey=" FAKE_REGISTKEY "\ncompanion=" FAKE_COMPANION "\n");

    check(halyard_pairing_file_load(g_dir, &rec) == 1, "the first console loads");
    snprintf(rec.host, sizeof(rec.host), "%s", "10.0.0.2");
    snprintf(rec.name, sizeof(rec.name), "%s", "Upstairs");
    check(halyard_pairing_file_save(g_dir, &rec) == 1, "pairing a second one saves");

    check(halyard_pairing_file_load_set(g_dir, &set) == 2,
          "and the FIRST console is still there afterwards");
    check(halyard_pairing_set_find(&set, "10.0.0.1") == 0, "at its own index");
    check(set.selected == halyard_pairing_set_find(&set, "10.0.0.2"),
          "with the newly paired one selected, because that is what somebody just did");
}

static void test_upsert_replaces_rather_than_duplicates(void)
{
    halyard_pairing_set set;
    halyard_pairing_record rec;

    write_file("\n[console]\nhost=10.0.0.1\nname=Old name\n"
               "registkey=" FAKE_REGISTKEY "\ncompanion=" FAKE_COMPANION "\n");
    check(halyard_pairing_file_load_set(g_dir, &set) == 1, "one console to start with");

    rec = set.console[0];
    snprintf(rec.name, sizeof(rec.name), "%s", "New name");
    check(halyard_pairing_set_upsert(&set, &rec) == 0, "re-pairing the same address returns its index");
    check(set.count == 1, "and does not add a second entry for it");
    check(strcmp(set.console[0].name, "New name") == 0, "the entry was replaced");
}

static void test_a_settings_change_reaches_every_console(void)
{
    halyard_pairing_set set;
    halyard_pairing_record rec;

    write_file("fps=30\n"
               "\n[console]\nhost=10.0.0.1\nregistkey=" FAKE_REGISTKEY "\ncompanion=" FAKE_COMPANION "\n"
               "\n[console]\nhost=10.0.0.2\nregistkey=" FAKE_REGISTKEY "\ncompanion=" FAKE_COMPANION "\n");
    check(halyard_pairing_file_load_set(g_dir, &set) == 2, "two consoles");

    rec = set.console[1];
    rec.fps = 60;
    (void)halyard_pairing_set_upsert(&set, &rec);
    check(set.console[0].fps == 60 && set.console[1].fps == 60,
          "changing a shared setting on one console changes it on all of them");

    check(halyard_pairing_file_save_set(g_dir, &set) == 1, "the set writes back");
    check(halyard_pairing_file_load_set(g_dir, &set) == 2, "and reloads");
    check(set.console[0].fps == 60, "with the setting stored once and restored to everyone");
}

static void test_settings_can_be_applied_from_outside_the_set(void)
{
    halyard_pairing_set set;
    halyard_pairing_record settings;

    write_file("fps=30\n"
               "\n[console]\nhost=10.0.0.1\nregistkey=" FAKE_REGISTKEY "\ncompanion=" FAKE_COMPANION "\n"
               "\n[console]\nhost=10.0.0.2\nregistkey=" FAKE_REGISTKEY "\ncompanion=" FAKE_COMPANION "\n");
    check(halyard_pairing_file_load_set(g_dir, &set) == 2, "two consoles");

    settings = set.console[0];
    settings.fps = 60;
    settings.diagnostics = 1;
    /* A front end holding settings in a record of its own - what one does before anything is paired. */
    snprintf(settings.host, sizeof(settings.host), "%s", "not a console at all");

    halyard_pairing_set_apply_settings(&set, &settings);
    check(set.console[0].fps == 60 && set.console[1].fps == 60, "the settings reached both consoles");
    check(set.console[0].diagnostics == 1, "all of them");
    check(strcmp(set.console[0].host, "10.0.0.1") == 0,
          "and the per-console fields were NOT touched, which is the whole point of the split");
    check(set.count == 2, "nothing was added");

    halyard_pairing_set_apply_settings(NULL, &settings);
    halyard_pairing_set_apply_settings(&set, NULL);
    check(set.count == 2, "a null argument does nothing rather than something");
}

static void test_a_console_that_moved_is_found_by_its_own_id(void)
{
    halyard_pairing_set set;

    /* Two consoles, one of which carries the id discovery gave it. */
    write_file("\n[console]\nhost=10.0.0.1\nconsoleid=AABBCCDDEEFF\nname=Front room\n"
               "registkey=" FAKE_REGISTKEY "\ncompanion=" FAKE_COMPANION "\n"
               "\n[console]\nhost=10.0.0.2\nregistkey=" FAKE_REGISTKEY "\ncompanion=" FAKE_COMPANION "\n");
    check(halyard_pairing_file_load_set(g_dir, &set) == 2, "two consoles");
    check(strcmp(set.console[0].console_id, "AABBCCDDEEFF") == 0, "the console id came through");
    check(halyard_pairing_set_find_id(&set, "AABBCCDDEEFF") == 0, "and finds its console");
    check(halyard_pairing_set_find_id(&set, "NOSUCHID") == -1, "an id that is not there finds nothing");
    check(halyard_pairing_set_find_id(&set, "") == -1, "and neither does an empty one");
    check(halyard_pairing_set_find_id(&set, NULL) == -1, "nor no id at all");

    /* Its lease moved. Nothing about the console changed, so nothing but the address should. */
    check(halyard_pairing_file_readdress(g_dir, "AABBCCDDEEFF", "10.0.0.77") == 1,
          "the record is re-addressed");
    check(halyard_pairing_file_load_set(g_dir, &set) == 2, "and still holds both consoles");
    check(strcmp(set.console[0].host, "10.0.0.77") == 0, "the moved one points at where it answered");
    check(strcmp(set.console[0].name, "Front room") == 0, "and kept its name");
    check(set.console[0].registkey_length == 8u,
          "and its KEYS, which belong to the console and not to where it was sitting");
    check(strcmp(set.console[1].host, "10.0.0.2") == 0, "the other console was not touched");

    check(halyard_pairing_file_readdress(g_dir, "AABBCCDDEEFF", "10.0.0.77") == 0,
          "re-addressing to where it already is writes nothing");
    check(halyard_pairing_file_readdress(g_dir, "NOSUCHID", "10.0.0.9") == 0,
          "and an id we do not hold changes nothing");
    check(halyard_pairing_file_readdress(g_dir, "AABBCCDDEEFF", "") == 0, "nor does an empty address");
}

static void test_remove(void)
{
    halyard_pairing_set set;

    write_file("\n[console]\nhost=10.0.0.1\nregistkey=" FAKE_REGISTKEY "\ncompanion=" FAKE_COMPANION "\n"
               "\n[console]\nhost=10.0.0.2\nregistkey=" FAKE_REGISTKEY "\ncompanion=" FAKE_COMPANION "\n"
               "\n[console]\nhost=10.0.0.3\nregistkey=" FAKE_REGISTKEY "\ncompanion=" FAKE_COMPANION "\n");
    check(halyard_pairing_file_load_set(g_dir, &set) == 3, "three consoles");

    halyard_pairing_set_select(&set, 2);
    check(halyard_pairing_set_remove(&set, 0) == 1, "the first is removed");
    check(set.count == 2, "leaving two");
    check(strcmp(set.console[0].host, "10.0.0.2") == 0, "the rest closed up");
    check(set.selected == 1,
          "and the selection FOLLOWED its console rather than staying on the number");

    halyard_pairing_set_select(&set, 1);
    check(halyard_pairing_set_remove(&set, 1) == 1, "removing the selected one works");
    check(set.selected == 0, "and the selection moves to what is left");

    check(halyard_pairing_set_remove(&set, 0) == 1, "and the last one");
    check(set.count == 0 && set.selected == -1, "an empty set selects nothing");
    check(halyard_pairing_set_selected(&set) == NULL, "and offers no record");

    check(halyard_pairing_set_remove(&set, 0) == 0, "removing from an empty set says so");
    check(halyard_pairing_set_remove(&set, -1) == 0, "so does a negative index");
    check(halyard_pairing_set_remove(NULL, 0) == 0, "so does no set at all");
}

static void test_incomplete_entries_are_dropped_not_offered(void)
{
    halyard_pairing_set set;

    /* The middle one has no companion key: it cannot open a session, so it must not reach a menu. */
    write_file("\n[console]\nhost=10.0.0.1\nregistkey=" FAKE_REGISTKEY "\ncompanion=" FAKE_COMPANION "\n"
               "\n[console]\nhost=10.0.0.2\nregistkey=" FAKE_REGISTKEY "\n"
               "\n[console]\nhost=10.0.0.3\nregistkey=" FAKE_REGISTKEY "\ncompanion=" FAKE_COMPANION "\n");

    check(halyard_pairing_file_load_set(g_dir, &set) == 2, "the unusable entry is not counted");
    check(strcmp(set.console[0].host, "10.0.0.1") == 0, "the good ones close up");
    check(strcmp(set.console[1].host, "10.0.0.3") == 0, "in order");
}

static void test_a_full_set_refuses_rather_than_dropping_somebody(void)
{
    halyard_pairing_set set;
    halyard_pairing_record rec;
    int i;

    memset(&set, 0, sizeof(set));
    set.selected = -1;
    memset(&rec, 0, sizeof(rec));
    memset(rec.registkey, 0xcd, sizeof(rec.registkey));
    rec.registkey_length = sizeof(rec.registkey);

    for (i = 0; i < HALYARD_PAIRING_MAX_CONSOLES; i++) {
        snprintf(rec.host, sizeof(rec.host), "10.0.1.%d", i);
        check(halyard_pairing_set_upsert(&set, &rec) == i, "consoles fit up to the maximum");
    }
    snprintf(rec.host, sizeof(rec.host), "%s", "10.0.2.1");
    check(halyard_pairing_set_upsert(&set, &rec) == -1, "and one more is refused");
    check(set.count == HALYARD_PAIRING_MAX_CONSOLES, "without evicting anybody");

    /* Replacing one that is already there still works when the set is full. */
    snprintf(rec.host, sizeof(rec.host), "%s", "10.0.1.0");
    check(halyard_pairing_set_upsert(&set, &rec) == 0, "a re-pair of a known console still fits");
}

static void test_no_file_and_junk(void)
{
    halyard_pairing_set set;
    char path[320];

    snprintf(path, sizeof(path), "%spairing.txt", g_dir);
    (void)remove(path);
    check(halyard_pairing_file_load_set(g_dir, &set) == 0, "a missing file loads nothing");
    check(set.count == 0 && set.selected == -1, "and reports an empty set");
    check(set.console[0].fps > 0, "with defaults in place, so a caller still has somewhere to start");

    write_file("this file is not a pairing record at all\n[console]\n\n\n");
    check(halyard_pairing_file_load_set(g_dir, &set) == 0, "a section with nothing in it is not usable");

    check(halyard_pairing_file_load_set(g_dir, NULL) == 0, "no output means no work");
    check(halyard_pairing_file_save_set(g_dir, NULL) == 0, "and nothing to save is not saved");
}

static void test_selected_out_of_range_is_clamped(void)
{
    halyard_pairing_set set;

    write_file("selected=7\n"
               "\n[console]\nhost=10.0.0.1\nregistkey=" FAKE_REGISTKEY "\ncompanion=" FAKE_COMPANION "\n");
    check(halyard_pairing_file_load_set(g_dir, &set) == 1, "one console");
    check(set.selected == 0, "a stored selection past the end falls back to the first");

    halyard_pairing_set_select(&set, 99);
    check(set.selected == -1, "selecting out of range selects nothing rather than something wrong");
    halyard_pairing_set_select(&set, 0);
    check(set.selected == 0, "and a valid index still works");
}

int main(int argc, char **argv)
{
    if (argc > 1)
        snprintf(g_dir, sizeof(g_dir), "%s/", argv[1]);
    else
        snprintf(g_dir, sizeof(g_dir), "%s", "./");

    printf("pairing set: keeping what cannot be regenerated\n");
    test_a_file_from_before_sections_still_loads();
    test_several_consoles();
    test_pairing_a_second_console_keeps_the_first();
    test_upsert_replaces_rather_than_duplicates();
    test_a_settings_change_reaches_every_console();
    test_settings_can_be_applied_from_outside_the_set();
    test_a_console_that_moved_is_found_by_its_own_id();
    test_remove();
    test_incomplete_entries_are_dropped_not_offered();
    test_a_full_set_refuses_rather_than_dropping_somebody();
    test_no_file_and_junk();
    test_selected_out_of_range_is_clamped();

    {
        char path[320];

        snprintf(path, sizeof(path), "%spairing.txt", g_dir);
        (void)remove(path);
    }

    if (g_failures != 0) {
        printf("pairing set: %d failed\n", g_failures);
        return 1;
    }
    printf("pairing set: ok\n");
    return 0;
}
