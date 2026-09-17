/*
 * ripcord-ps3 - can this console tell us its own PSN account id?
 *
 * WHY THE QUESTION MATTERS. Registration is the one exchange that needs the account id, and nothing in
 * this port knows it: rc_pair_run asks for it on an on-screen keyboard, which is a nineteen-digit number
 * entered with a controller. It is remembered afterwards, so it is typed once - but "once" is still the
 * worst step in the only flow a new user has to get through, and this PS3 is signed in to the very
 * account the number belongs to.
 *
 * WHY THIS IS A SEARCH RATHER THAN A READ. PSL1GHT has no NP bindings at all - `SYSMODULE_SYSUTIL_NP`
 * exists as a module id and there is no header and no stub library to call into it - so
 * sceNpManagerGetAccountId is not reachable from here without resolving a PRX export by hand. The other
 * route is the filesystem, and there the honest position is that this port does not know which file or
 * which offset, and guessing at one would produce a value that cannot be told apart from a wrong one.
 *
 * SO IT LOOKS FOR A NUMBER IT ALREADY HAS. A paired console has the account id in its pairing record,
 * typed by the person who owns it. Given that, finding where the console keeps its own copy is a
 * substring search over a few small files - which needs no hypothesis about any file format, confirms
 * itself by construction, and reports an offset that the next build can read directly.
 *
 * NOTHING HERE LOGS THE VALUE, IN ANY FORM. Not the account id, not the bytes around it, not the
 * filenames - an exdata filename carries a content id, which names something somebody bought. What is
 * reported is a directory, an index, an offset and a byte order. That is everything needed to write the
 * reader and nothing that identifies anyone.
 */
#ifndef RC_ACCOUNT_PS3_H
#define RC_ACCOUNT_PS3_H

#include <stddef.h>

#define RC_ACCOUNT_HIT_PATH_MAX 96

typedef struct {
    /*
     * SYSUTIL_SYSTEMPARAM_ID_CURRENT_USER_HAS_NP_ACCOUNT. Not the id - it is a yes or no - but it is the
     * one question the documented API does answer, and a console that says no explains every empty
     * result below without any searching.
     */
    int has_np_account;      /* 1, 0, or -1 when the call itself failed */
    int has_np_account_rc;

    int searched;            /* the probe ran - a known account id was supplied */
    int home_users;          /* how many local users /dev_hdd0/home holds */
    int dirs_walked;
    int files_scanned;
    int files_too_big;       /* skipped by the size cap, so a miss is not mistaken for absence */
    int read_errors;

    int hits;                /* files carrying the account id */

    /* The first one, which is what a reader would be written against. */
    char     hit_path[RC_ACCOUNT_HIT_PATH_MAX];  /* directory and an INDEX, never a filename */
    unsigned hit_offset;
    int      hit_big_endian; /* 1 = stored big-endian, 0 = little */
    unsigned hit_file_size;
} rc_account_probe;

/*
 * Walks the console's own storage looking for `known_account_id` - the decimal string from the pairing
 * record - as an eight-byte integer in either byte order. `out` is filled either way; a run that finds
 * nothing still reports how far it got, because "the console does not keep it anywhere we can read" and
 * "the search never got started" are different findings.
 */
void rc_account_probe_run(const char *known_account_id, rc_account_probe *out);

#endif /* RC_ACCOUNT_PS3_H */
