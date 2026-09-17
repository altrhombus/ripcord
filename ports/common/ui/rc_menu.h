/*
 * ripcord - a list of things a person can choose, in a form any front end can render.
 *
 * WHY THIS IS HERE AND NOT IN THE PORT. A menu is two separate problems wearing one name. There is what
 * the list CONTAINS and which row is under the cursor - which is arithmetic, is the same on every screen
 * this project will ever draw on, and is exactly the part that gets subtly wrong (a cursor that stops on
 * a row that cannot be chosen, a wrap that skips the last item, a value edited on the row above the one
 * being looked at). And there is what it LOOKS like, which is nothing but the platform. The first is in
 * this file and is tested on a host with no console attached; the second is not, and cannot be.
 *
 * SAME RULE AS rc_session_state.h: no front end's types cross into here. No colour, no font, no pixel, no
 * button. A row has a label, a value, a note and whether it can be chosen, and a front end decides what
 * any of that looks like. A television ten feet away and a handheld's lower screen agree on the model and
 * on nothing else.
 *
 * THE CALLER OWNS THE MEANING OF `id`. Rows are addressed by an id the caller assigns rather than by
 * their index, because indices move: a home screen that lists however many consoles answered a broadcast
 * has a different index for "Settings" every time it is rebuilt, and code that remembers the number 4 is
 * code that opens the wrong screen the day a second console is switched on.
 */
#ifndef RC_MENU_H
#define RC_MENU_H

#define RC_MENU_LABEL_MAX 40
#define RC_MENU_VALUE_MAX 40
#define RC_MENU_NOTE_MAX  80
/*
 * Raised from 14 when the home screen gained a list of PAIRED consoles alongside the discovered ones:
 * eight paired, four more on the network and four commands is sixteen, and a menu that silently stops
 * adding rows loses whichever console was unlucky.
 */
#define RC_MENU_ITEMS_MAX 20

typedef struct {
    int  id;
    char label[RC_MENU_LABEL_MAX];
    char value[RC_MENU_VALUE_MAX];  /* the right-hand column; empty for a plain row */
    char note[RC_MENU_NOTE_MAX];    /* a second line under the label, for the selected row */

    /*
     * A row that cannot be chosen is still SHOWN. "Connect" with no pairing record belongs on the screen
     * greyed out, because a person looking for it needs to find it and learn why - and a menu that
     * silently omits what it cannot do leaves them looking for something that is not there.
     */
    int  enabled;

    /* Left and right change this row's value in place instead of opening something. */
    int  adjustable;
} rc_menu_item;

typedef struct {
    char title[RC_MENU_LABEL_MAX];
    char subtitle[RC_MENU_NOTE_MAX];
    rc_menu_item item[RC_MENU_ITEMS_MAX];
    int  count;

    /* -1 when nothing can be chosen - which is a state a renderer has to handle, not an impossible one. */
    int  selected;

    /* Bumped whenever something a viewer could notice moved, so a renderer redraws only when there is
     * something new. Same contract as rc_session_state's. */
    unsigned revision;
} rc_menu;

void rc_menu_reset(rc_menu *menu, const char *title, const char *subtitle);

/*
 * Appends a row and returns its index, or -1 if the menu is full or `menu` is NULL. `value` and `note`
 * may be NULL. New rows are enabled and not adjustable; the first enabled row becomes the selection.
 */
int rc_menu_add(rc_menu *menu, int id, const char *label, const char *value, const char *note);

void rc_menu_set_enabled(rc_menu *menu, int index, int enabled);
void rc_menu_set_adjustable(rc_menu *menu, int index, int adjustable);
void rc_menu_set_value(rc_menu *menu, int index, const char *value);
void rc_menu_set_note(rc_menu *menu, int index, const char *note);

/*
 * Moves the cursor by `delta` rows, skipping disabled ones and wrapping. Returns 1 if it ended up
 * somewhere new.
 *
 * WRAPPING RATHER THAN STOPPING because the lists here are short and the input is a d-pad: on a list of
 * four, pressing up from the top to reach the bottom is one press instead of three, and there is no
 * scrollbar to make the end of the list feel like a place. A menu where every row is disabled does not
 * move and does not loop forever looking for one that is not - that case is why this is tested.
 */
int rc_menu_move(rc_menu *menu, int delta);

/* The selected row's id, or -1 when nothing is selected. */
int rc_menu_selected_id(const rc_menu *menu);

/* NULL when nothing is selected. */
const rc_menu_item *rc_menu_selected(const rc_menu *menu);

/*
 * Puts the cursor on the row with this id, if it is there and enabled. Returns 1 if it moved there.
 * For rebuilding a list without losing the reader's place - see the note on ids at the top.
 */
int rc_menu_select_id(rc_menu *menu, int id);

#endif /* RC_MENU_H */
