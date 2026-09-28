/*
 * ripcord - the menu model, on a host, with no screen attached.
 *
 * WHAT IS WORTH TESTING HERE is not that a list holds strings. It is the cursor: every bug this model
 * can have is a cursor that ends up somewhere the person looking at the screen did not put it, and every
 * one of them is invisible in a screenshot and obvious in front of a television. A row that cannot be
 * chosen but takes the highlight reads as a broken Cross button. A wrap that skips the last row hides an
 * option. A list where nothing can be chosen must not loop forever looking for something that can - on a
 * console that is a hang at the menu, with no terminal to find out why.
 *
 * So the cases below are the cursor's, not the container's.
 */
#include "../ui/rc_menu.h"

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

enum { ID_A = 10, ID_B = 20, ID_C = 30 };

static void build(rc_menu *menu)
{
    rc_menu_reset(menu, "Ripcord", "three rows");
    (void)rc_menu_add(menu, ID_A, "first", NULL, NULL);
    (void)rc_menu_add(menu, ID_B, "second", "on", "a note");
    (void)rc_menu_add(menu, ID_C, "third", NULL, NULL);
}

static void test_add_and_select(void)
{
    rc_menu menu;

    memset(&menu, 0, sizeof(menu));
    build(&menu);
    check(menu.count == 3, "three rows were added");
    check(menu.selected == 0, "the first row is selected to begin with");
    check(rc_menu_selected_id(&menu) == ID_A, "and it reports its id, not its index");
    check(rc_menu_selected(&menu) != NULL && strcmp(rc_menu_selected(&menu)->label, "first") == 0,
          "the selected row is the one that was added first");
    check(strcmp(menu.item[1].value, "on") == 0, "a value came through");
    check(strcmp(menu.item[1].note, "a note") == 0, "so did a note");
}

static void test_move_wraps(void)
{
    rc_menu menu;

    memset(&menu, 0, sizeof(menu));
    build(&menu);
    check(rc_menu_move(&menu, 1) == 1 && menu.selected == 1, "down moves one row");
    check(rc_menu_move(&menu, 1) == 1 && menu.selected == 2, "and again");
    check(rc_menu_move(&menu, 1) == 1 && menu.selected == 0, "down from the last row wraps to the first");
    check(rc_menu_move(&menu, -1) == 1 && menu.selected == 2, "up from the first wraps to the LAST");
    check(rc_menu_move(&menu, 0) == 0, "a move of zero does nothing");
}

static void test_move_skips_disabled(void)
{
    rc_menu menu;

    memset(&menu, 0, sizeof(menu));
    build(&menu);
    rc_menu_set_enabled(&menu, 1, 0);
    check(menu.selected == 0, "disabling a row that was not selected leaves the cursor alone");
    check(rc_menu_move(&menu, 1) == 1 && menu.selected == 2,
          "the cursor steps OVER a disabled row rather than landing on it");
    check(rc_menu_move(&menu, 1) == 1 && menu.selected == 0, "and wraps past it too");
    check(rc_menu_move(&menu, -1) == 1 && menu.selected == 2, "upwards as well");
}

static void test_disabling_the_selected_row_moves_the_cursor(void)
{
    rc_menu menu;

    memset(&menu, 0, sizeof(menu));
    build(&menu);
    rc_menu_set_enabled(&menu, 0, 0);
    check(menu.selected == 1, "the cursor moves off a row that has just been disabled");
    check(rc_menu_selected_id(&menu) == ID_B, "to the next one along, not back to the top");
}

static void test_every_row_disabled(void)
{
    rc_menu menu;
    int i;

    memset(&menu, 0, sizeof(menu));
    build(&menu);
    for (i = 0; i < 3; i++)
        rc_menu_set_enabled(&menu, i, 0);

    check(menu.selected == -1, "a list where nothing can be chosen selects nothing");
    check(rc_menu_selected(&menu) == NULL, "and offers no row to a renderer");
    check(rc_menu_selected_id(&menu) == -1, "and no id");
    /* The one that would hang a console: this must RETURN. */
    check(rc_menu_move(&menu, 1) == 0, "moving down finds nowhere to go and says so");
    check(rc_menu_move(&menu, -1) == 0, "and so does moving up");

    rc_menu_set_enabled(&menu, 2, 1);
    check(menu.selected == 2, "re-enabling a row when nothing was selected puts the cursor on it");
}

static void test_select_by_id_survives_a_rebuild(void)
{
    rc_menu menu;

    memset(&menu, 0, sizeof(menu));
    build(&menu);
    check(rc_menu_select_id(&menu, ID_C) == 1 && menu.selected == 2, "the cursor can be put on an id");

    /* A rebuild with an extra row in FRONT - what a home screen does when another console answers. */
    rc_menu_reset(&menu, "Ripcord", "four rows");
    (void)rc_menu_add(&menu, 99, "a console that just appeared", NULL, NULL);
    (void)rc_menu_add(&menu, ID_A, "first", NULL, NULL);
    (void)rc_menu_add(&menu, ID_B, "second", NULL, NULL);
    (void)rc_menu_add(&menu, ID_C, "third", NULL, NULL);
    check(rc_menu_select_id(&menu, ID_C) == 1 && menu.selected == 3,
          "and finds it again at a different index");

    check(rc_menu_select_id(&menu, 12345) == 0, "an id that is not there does not move the cursor");
    check(menu.selected == 3, "...and really does not");

    rc_menu_set_enabled(&menu, 1, 0);
    check(rc_menu_select_id(&menu, ID_A) == 0, "a disabled row cannot be selected by id either");
}

static void test_revision_moves_only_on_change(void)
{
    rc_menu menu;
    unsigned before;

    memset(&menu, 0, sizeof(menu));
    build(&menu);

    before = menu.revision;
    check(rc_menu_move(&menu, 1) == 1 && menu.revision > before, "a move is a change");

    before = menu.revision;
    rc_menu_set_value(&menu, 1, menu.item[1].value);
    check(menu.revision == before, "writing the same value again is not");
    rc_menu_set_value(&menu, 1, "off");
    check(menu.revision > before, "writing a different one is");

    before = menu.revision;
    rc_menu_set_enabled(&menu, 2, 1);
    check(menu.revision == before, "enabling an already-enabled row is not");

    /*
     * The one that is easy to get wrong: a reset is the biggest change there is, and a renderer caching
     * on the revision must not conclude that nothing happened because the counter went back to zero.
     */
    before = menu.revision;
    rc_menu_reset(&menu, "Ripcord", NULL);
    check(menu.revision > before, "a reset does not restart the revision counter");
}

static void test_bounds(void)
{
    rc_menu menu;
    int i, added;

    memset(&menu, 0, sizeof(menu));
    rc_menu_reset(&menu, "full", NULL);
    for (i = 0; i < RC_MENU_ITEMS_MAX; i++)
        check(rc_menu_add(&menu, i, "row", NULL, NULL) == i, "rows fit up to the maximum");
    added = rc_menu_add(&menu, 999, "one too many", NULL, NULL);
    check(added == -1, "and one more is refused rather than written past the end");
    check(menu.count == RC_MENU_ITEMS_MAX, "the count did not move");

    /* Out-of-range indices are ignored rather than writing somewhere. */
    rc_menu_set_value(&menu, -1, "x");
    rc_menu_set_value(&menu, RC_MENU_ITEMS_MAX, "x");
    rc_menu_set_enabled(&menu, -1, 0);
    rc_menu_set_note(&menu, RC_MENU_ITEMS_MAX + 4, "x");
    check(menu.count == RC_MENU_ITEMS_MAX, "an out-of-range write changed nothing");

    /* NULL is a legitimate argument everywhere, because a caller that has no menu yet is a normal state. */
    rc_menu_reset(NULL, "x", NULL);
    check(rc_menu_add(NULL, 0, "x", NULL, NULL) == -1, "adding to no menu says so");
    check(rc_menu_move(NULL, 1) == 0, "moving in no menu says so");
    check(rc_menu_selected(NULL) == NULL, "and there is nothing selected in it");
    check(rc_menu_selected_id(NULL) == -1, "nor any id");
    check(rc_menu_select_id(NULL, 1) == 0, "nor anything to select");
}

static void test_long_strings_are_truncated_not_overrun(void)
{
    rc_menu menu;
    char huge[RC_MENU_NOTE_MAX * 3];

    memset(&menu, 0, sizeof(menu));
    memset(huge, 'x', sizeof(huge) - 1u);
    huge[sizeof(huge) - 1u] = '\0';

    rc_menu_reset(&menu, huge, huge);
    check(strlen(menu.title) == RC_MENU_LABEL_MAX - 1u, "an over-long title is cut to fit");
    check(strlen(menu.subtitle) == RC_MENU_NOTE_MAX - 1u, "and so is a subtitle");

    (void)rc_menu_add(&menu, 1, huge, huge, huge);
    check(strlen(menu.item[0].label) == RC_MENU_LABEL_MAX - 1u, "a label is cut to fit");
    check(strlen(menu.item[0].value) == RC_MENU_VALUE_MAX - 1u, "a value is cut to fit");
    check(strlen(menu.item[0].note) == RC_MENU_NOTE_MAX - 1u, "a note is cut to fit");
}

int main(void)
{
    printf("menu: the cursor\n");
    test_add_and_select();
    test_move_wraps();
    test_move_skips_disabled();
    test_disabling_the_selected_row_moves_the_cursor();
    test_every_row_disabled();
    test_select_by_id_survives_a_rebuild();
    test_revision_moves_only_on_change();
    test_bounds();
    test_long_strings_are_truncated_not_overrun();

    if (g_failures != 0) {
        printf("menu: %d failed\n", g_failures);
        return 1;
    }
    printf("menu: ok\n");
    return 0;
}
