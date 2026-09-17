/* See rc_menu.h. */
#include "rc_menu.h"

#include <stddef.h>
#include <string.h>

static void copy_field(char *dst, size_t size, const char *src)
{
    size_t n;

    if (src == NULL) {
        dst[0] = '\0';
        return;
    }
    n = strlen(src);
    if (n >= size)
        n = size - 1u;
    memcpy(dst, src, n);
    dst[n] = '\0';
}

void rc_menu_reset(rc_menu *menu, const char *title, const char *subtitle)
{
    if (menu == NULL)
        return;

    /*
     * The revision SURVIVES the reset. A rebuilt menu is a change like any other, and zeroing the
     * counter here would let a renderer that caches on it decide nothing had happened - which is the
     * bug this counter exists to prevent, arriving by the back door.
     */
    {
        unsigned revision = menu->revision;

        memset(menu, 0, sizeof(*menu));
        menu->revision = revision + 1u;
    }
    menu->selected = -1;
    copy_field(menu->title, sizeof(menu->title), title);
    copy_field(menu->subtitle, sizeof(menu->subtitle), subtitle);
}

int rc_menu_add(rc_menu *menu, int id, const char *label, const char *value, const char *note)
{
    rc_menu_item *item;
    int index;

    if (menu == NULL || menu->count >= RC_MENU_ITEMS_MAX)
        return -1;

    index = menu->count++;
    item = &menu->item[index];
    item->id = id;
    copy_field(item->label, sizeof(item->label), label);
    copy_field(item->value, sizeof(item->value), value);
    copy_field(item->note, sizeof(item->note), note);
    item->enabled = 1;
    item->adjustable = 0;

    if (menu->selected < 0)
        menu->selected = index;
    menu->revision++;
    return index;
}

static int in_range(const rc_menu *menu, int index)
{
    return menu != NULL && index >= 0 && index < menu->count;
}

void rc_menu_set_enabled(rc_menu *menu, int index, int enabled)
{
    if (!in_range(menu, index) || menu->item[index].enabled == (enabled != 0))
        return;
    menu->item[index].enabled = (enabled != 0);
    menu->revision++;

    /*
     * Disabling the selected row moves the cursor off it. Leaving it parked there would put a
     * highlight on something Cross does nothing to, which reads as the button being broken.
     */
    if (!enabled && menu->selected == index) {
        /* Forward from where it was, so the cursor lands next to what just went away rather than at
         * the top of the list. rc_menu_move cannot settle on `index` itself - it is disabled now. */
        if (!rc_menu_move(menu, 1))
            menu->selected = -1;
    } else if (enabled && menu->selected < 0) {
        menu->selected = index;
    }
}

void rc_menu_set_adjustable(rc_menu *menu, int index, int adjustable)
{
    if (!in_range(menu, index))
        return;
    menu->item[index].adjustable = (adjustable != 0);
}

void rc_menu_set_value(rc_menu *menu, int index, const char *value)
{
    if (!in_range(menu, index))
        return;
    if (value != NULL && strncmp(menu->item[index].value, value, sizeof(menu->item[index].value)) == 0)
        return;
    copy_field(menu->item[index].value, sizeof(menu->item[index].value), value);
    menu->revision++;
}

void rc_menu_set_note(rc_menu *menu, int index, const char *note)
{
    if (!in_range(menu, index))
        return;
    if (note != NULL && strncmp(menu->item[index].note, note, sizeof(menu->item[index].note)) == 0)
        return;
    copy_field(menu->item[index].note, sizeof(menu->item[index].note), note);
    menu->revision++;
}

int rc_menu_move(rc_menu *menu, int delta)
{
    int step, at, tried;

    if (menu == NULL || menu->count <= 0 || delta == 0)
        return 0;

    step = (delta > 0) ? 1 : -1;
    at = (menu->selected < 0) ? ((step > 0) ? -1 : menu->count) : menu->selected;

    /*
     * BOUNDED BY THE ROW COUNT, not by finding one. A list whose rows are all disabled has no landing
     * place, and a search that wraps until it finds one never comes back - on a console, with no
     * terminal and no debugger, as a hang at the menu.
     */
    for (tried = 0; tried < menu->count; tried++) {
        at += step;
        if (at >= menu->count)
            at = 0;
        else if (at < 0)
            at = menu->count - 1;
        if (menu->item[at].enabled) {
            if (at == menu->selected)
                return 0;
            menu->selected = at;
            menu->revision++;
            return 1;
        }
    }
    return 0;
}

int rc_menu_selected_id(const rc_menu *menu)
{
    const rc_menu_item *item = rc_menu_selected(menu);

    return (item != NULL) ? item->id : -1;
}

const rc_menu_item *rc_menu_selected(const rc_menu *menu)
{
    if (menu == NULL || menu->selected < 0 || menu->selected >= menu->count)
        return NULL;
    return &menu->item[menu->selected];
}

int rc_menu_select_id(rc_menu *menu, int id)
{
    int i;

    if (menu == NULL)
        return 0;
    for (i = 0; i < menu->count; i++) {
        if (menu->item[i].id != id || !menu->item[i].enabled)
            continue;
        if (menu->selected != i) {
            menu->selected = i;
            menu->revision++;
        }
        return 1;
    }
    return 0;
}
