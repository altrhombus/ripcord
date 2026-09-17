/* See rc_account_ps3.h. */
#include "rc_account_ps3.h"

#include <stdio.h>
#include <string.h>

/*
 * Scoped to these includes rather than relaxed in the Makefile: the conversions are PSL1GHT's own inline
 * lv2 wrappers turning ints into u64 syscall arguments, there is nothing at our call sites to fix, and a
 * flag dropped globally to get one file to build is how a real narrowing bug gets through later in a port
 * that also parses bitstreams. Same shape and same reason as source/app/main.c's.
 */
#pragma GCC diagnostic push
#pragma GCC diagnostic ignored "-Wsign-conversion"
#include <lv2/sysfs.h>
#include <sys/file.h>
#pragma GCC diagnostic pop

#include <sysutil/sysutil.h>

#include "rc_log.h"

/*
 * THE SIZE CAP, and why a skipped file is counted rather than ignored.
 *
 * Four megabytes reads in a fraction of a second and covers everything a per-user directory holds. What
 * it does not cover is a save file or a cached asset, and if the answer turned out to be inside one of
 * those, a probe that silently passed over it would report "not found anywhere" - which is the opposite
 * of the truth and unfalsifiable from the log. So the count is reported.
 */
#define RC_ACC_MAX_FILE   (4u * 1024u * 1024u)
#define RC_ACC_CHUNK      16384u
#define RC_ACC_MAX_DEPTH  3
#define RC_ACC_MAX_FILES  400

typedef struct {
    unsigned char be[8];
    unsigned char le[8];
    rc_account_probe *out;
    int files;
} rc_acc_search;

/* CELL_FS_TYPE_DIRECTORY. d_type rather than a stat per entry - one syscall a file adds up over a
 * few hundred of them, and this runs while somebody is waiting. */
#define RC_ACC_DT_DIR 1

static int parse_account_id(const char *text, unsigned char *be, unsigned char *le)
{
    unsigned long long value = 0ull;
    const char *p;
    int digits = 0;
    int i;

    if (text == NULL || text[0] == '\0')
        return 0;
    for (p = text; *p != '\0'; p++) {
        if (*p < '0' || *p > '9')
            return 0;
        value = value * 10ull + (unsigned long long)(*p - '0');
        digits++;
    }
    if (digits == 0 || digits > 20)
        return 0;

    for (i = 0; i < 8; i++) {
        be[i] = (unsigned char)(value >> ((7 - i) * 8));
        le[i] = (unsigned char)(value >> (i * 8));
    }
    /*
     * A ZERO ID WOULD MATCH EVERYTHING. Eight zero bytes occur in almost every file on the machine, so a
     * search for them would report a hit in the first thing it opened and mean nothing at all.
     */
    return value != 0ull;
}

/* Scans one file. Returns 1 if the needle was found, and fills in where. */
static int scan_file(const char *path, const char *report_as, rc_acc_search *s)
{
    static unsigned char buf[RC_ACC_CHUNK + 7u];
    rc_account_probe *out = s->out;
    sysFSStat st;
    s32 fd = -1;
    u64 offset = 0u;
    u64 carry = 0u;
    int found = 0;

    if (sysLv2FsStat(path, &st) != 0)
        return 0;
    if ((u64)st.st_size < 8u)
        return 0;
    if ((u64)st.st_size > (u64)RC_ACC_MAX_FILE) {
        out->files_too_big++;
        return 0;
    }
    if (sysLv2FsOpen(path, SYS_O_RDONLY, &fd, 0, NULL, 0) != 0) {
        out->read_errors++;
        return 0;
    }
    out->files_scanned++;

    /*
     * OVERLAPPED CHUNKS. The value can straddle a chunk boundary, and a scan that restarts cleanly at
     * each one would miss it seven times out of eight for no reason anyone would ever see - the log
     * would simply say "not found". The last seven bytes of each chunk are carried into the next.
     */
    while (!found) {
        u64 got = 0u;
        u64 have;
        u64 i;

        if (sysLv2FsRead(fd, buf + carry, (u64)RC_ACC_CHUNK, &got) != 0) {
            out->read_errors++;
            break;
        }
        if (got == 0u)
            break;
        have = carry + got;
        if (have < 8u)
            break;

        for (i = 0; i + 8u <= have; i++) {
            int big = (memcmp(buf + i, s->be, 8) == 0);

            if (big || memcmp(buf + i, s->le, 8) == 0) {
                out->hits++;
                if (out->hits == 1) {
                    snprintf(out->hit_path, sizeof(out->hit_path), "%s", report_as);
                    out->hit_offset = (unsigned)(offset + i);
                    out->hit_big_endian = big;
                    out->hit_file_size = (unsigned)st.st_size;
                }
                found = 1;
                break;
            }
        }
        if (found)
            break;

        offset += got;
        /* Keep the tail so a value split across the join is still seen. */
        carry = (have >= 7u) ? 7u : have;
        memmove(buf, buf + (have - carry), (size_t)carry);
    }

    (void)sysLv2FsClose(fd);
    return found;
}

/*
 * ONE FRAME PER DEPTH, IN BSS RATHER THAN ON THE STACK.
 *
 * sysFSDirent carries MAXPATHLEN+1 bytes of name - a kilobyte - and a path built from it needs another.
 * Three of those nested is nine kilobytes of stack, and this port builds with -Wframe-larger-than=8192
 * precisely because a PS3 thread's stack is not generous and a blown one here is a silent death rather
 * than a diagnosable one. The recursion is bounded at RC_ACC_MAX_DEPTH, so the frames can be too.
 */
typedef struct {
    sysFSDirent entry;
    char child[1024];
    char report[RC_ACCOUNT_HIT_PATH_MAX];
} rc_acc_frame;

static rc_acc_frame s_frame[RC_ACC_MAX_DEPTH + 2];

static void walk(const char *path, const char *report_as, int depth, rc_acc_search *s)
{
    rc_acc_frame *f;
    s32 dir = -1;
    int index = 0;

    if (depth < 0 || depth > RC_ACC_MAX_DEPTH || s->files >= RC_ACC_MAX_FILES)
        return;
    if (sysLv2FsOpenDir(path, &dir) != 0)
        return;
    s->out->dirs_walked++;
    f = &s_frame[depth];

    for (;;) {
        u64 read = 0u;
        const char *name = f->entry.d_name;

        if (sysLv2FsReadDir(dir, &f->entry, &read) != 0 || read == 0u)
            break;
        f->entry.d_name[sizeof(f->entry.d_name) - 1u] = '\0';
        if (name[0] == '\0' || strcmp(name, ".") == 0 || strcmp(name, "..") == 0)
            continue;
        if (s->files >= RC_ACC_MAX_FILES)
            break;

        if (snprintf(f->child, sizeof(f->child), "%s/%s", path, name) >= (int)sizeof(f->child))
            continue;   /* a path this long is not one of ours */

        if (f->entry.d_type == RC_ACC_DT_DIR) {
            /*
             * A DIRECTORY NAME IS REPORTED, A FILE NAME IS NOT. "exdata" says nothing about anyone; the
             * files inside it are named after content ids, which name things somebody bought.
             */
            snprintf(f->report, sizeof(f->report), "%s/%.24s", report_as, name);
            walk(f->child, f->report, depth + 1, s);
            f = &s_frame[depth];   /* the recursion used the frames below this one, not this one */
        } else {
            const char *dot = strrchr(name, '.');

            snprintf(f->report, sizeof(f->report), "%s/[%d]%.8s", report_as, index,
                     (dot != NULL) ? dot : "");
            s->files++;
            (void)scan_file(f->child, f->report, s);
            index++;
        }
    }
    (void)sysLv2FsCloseDir(dir);
}

void rc_account_probe_run(const char *known_account_id, rc_account_probe *out)
{
    rc_acc_search search;
    s32 value = 0;
    s32 rc;

    memset(out, 0, sizeof(*out));
    out->has_np_account = -1;

    rc = sysUtilGetSystemParamInt(SYSUTIL_SYSTEMPARAM_ID_CURRENT_USER_HAS_NP_ACCOUNT, &value);
    out->has_np_account_rc = (int)rc;
    if (rc == 0)
        out->has_np_account = (value != 0);

    rc_log("acct:  this console %s a PSN account linked%s\n",
           (out->has_np_account == 1) ? "HAS" : (out->has_np_account == 0) ? "has NO" : "may have",
           (out->has_np_account < 0) ? " (the system parameter refused)" : "");

    memset(&search, 0, sizeof(search));
    search.out = out;
    if (!parse_account_id(known_account_id, search.be, search.le)) {
        /*
         * NOT A FAILURE, AND SAID AS SUCH. The search needs a known-good value to look for, which comes
         * from a pairing record. An unpaired console has none, and that is the ordinary state on a first
         * run rather than something going wrong.
         */
        rc_log("acct:  no account id in the pairing record yet - nothing to search for\n");
        return;
    }
    out->searched = 1;

    /* How many local users there are, for orientation: a console with three of them may keep the value
     * under one we do not look at. */
    {
        s32 dir = -1;

        if (sysLv2FsOpenDir("/dev_hdd0/home", &dir) == 0) {
            for (;;) {
                sysFSDirent entry;
                u64 read = 0u;

                if (sysLv2FsReadDir(dir, &entry, &read) != 0 || read == 0u)
                    break;
                if (entry.d_type == RC_ACC_DT_DIR && entry.d_name[0] >= '0' &&
                    entry.d_name[0] <= '9')
                    out->home_users++;
            }
            (void)sysLv2FsCloseDir(dir);
        }
    }

    rc_log("acct:  searching - %d local user(s) on this console\n", out->home_users);
    walk("/dev_hdd0/home", "home", 0, &search);

    /*
     * The system registry, which is where per-console settings live and is the other plausible home for
     * this. Scanned second because it is one large file rather than many small ones, so a hit in the
     * per-user data is the more useful answer and is worth finding first.
     */
    if (out->hits == 0)
        walk("/dev_flash2/etc", "flash2/etc", RC_ACC_MAX_DEPTH - 1, &search);

    if (out->hits > 0)
        rc_log("acct:  FOUND in %s at offset %u of %u bytes, stored %s-endian (%d file(s) carry it)\n",
               out->hit_path, out->hit_offset, out->hit_file_size,
               out->hit_big_endian ? "big" : "little", out->hits);
    else
        rc_log("acct:  not found in %d file(s) across %d director(ies)\n",
               out->files_scanned, out->dirs_walked);

    if (out->files_too_big > 0 || out->read_errors > 0)
        rc_log("acct:  %d file(s) were over the size cap and %d could not be read - a miss above is\n"
               "       'not in what was searched', not 'not on this console'\n",
               out->files_too_big, out->read_errors);
}

/* ------------------------------------------------------------------------------------------------
 * The reader, now that the search has answered where to look.
 */

/*
 * A DEGENERATE-VALUE GUARD, DELIBERATELY NOT A FORMAT CLAIM.
 *
 * [X] Every account id this project has seen is nineteen decimal digits, but that is an observation over
 * a handful of accounts and is documented nowhere - so the bound here is set far below it rather than at
 * it. What it has to catch is the file being absent in all but name: zeroed after a sign-out, or some
 * other 248 bytes entirely. What it must NOT do is reject a real id that happens to be shorter than the
 * ones seen so far, because a wrong rejection sends somebody back to typing nineteen digits with no
 * explanation. Sixteen digits is comfortably under any observed id and comfortably over any counter.
 *
 * A rejection is reported, not swallowed - rc_account_status_text names this case - so if the bound is
 * ever wrong it says so on the screen instead of looking like the console has no account.
 */
#define RC_ACC_MIN_PLAUSIBLE 1000000000000000ull

const char *rc_account_status_text(rc_account_status status)
{
    switch (status) {
    case RC_ACCOUNT_OK:          return "read from this console";
    case RC_ACCOUNT_NO_NP:       return "this PS3 is not signed in to PlayStation Network";
    case RC_ACCOUNT_NO_USER:     return "the signed-in user's storage was not found";
    case RC_ACCOUNT_NO_FILE:     return "this PS3 has no cached account id";
    case RC_ACCOUNT_IMPLAUSIBLE: return "what was there does not look like an account id";
    }
    return "?";
}

/* Reads a whole small file. Returns the byte count, 0 on any failure. */
static size_t read_small(const char *path, unsigned char *buf, size_t size)
{
    sysFSStat st;
    s32 fd = -1;
    u64 got = 0u;

    if (sysLv2FsStat(path, &st) != 0 || (u64)st.st_size == 0u || (u64)st.st_size > (u64)size)
        return 0;
    if (sysLv2FsOpen(path, SYS_O_RDONLY, &fd, 0, NULL, 0) != 0)
        return 0;
    if (sysLv2FsRead(fd, buf, (u64)st.st_size, &got) != 0)
        got = 0u;
    (void)sysLv2FsClose(fd);
    return (size_t)got;
}

/* Trailing whitespace and NULs off a stored name, so a comparison is about the name and not about how
 * the file happened to be terminated. */
static void trim(char *text)
{
    size_t n = strlen(text);

    while (n > 0u && (text[n - 1u] == '\n' || text[n - 1u] == '\r' || text[n - 1u] == ' '))
        text[--n] = '\0';
}

/* The eight bytes at the front of np_cache.dat, as a decimal string. */
static rc_account_status read_np_cache(const char *user_dir, char *out, size_t size)
{
    unsigned char raw[8];
    char path[128];
    unsigned long long value = 0ull;
    int i;

    snprintf(path, sizeof(path), "%s/np_cache.dat", user_dir);
    if (read_small(path, raw, sizeof(raw)) < sizeof(raw))
        return RC_ACCOUNT_NO_FILE;

    for (i = 0; i < 8; i++)
        value = (value << 8) | (unsigned long long)raw[i];

    if (value < RC_ACC_MIN_PLAUSIBLE)
        return RC_ACCOUNT_IMPLAUSIBLE;
    if (snprintf(out, size, "%llu", value) <= 0)
        return RC_ACCOUNT_IMPLAUSIBLE;
    return RC_ACCOUNT_OK;
}

rc_account_status rc_account_read(char *out, size_t size)
{
    char current_user[SYSUTIL_SYSTEMPARAM_CURRENT_USERNAME_SIZE + 1];
    char only_dir[64];
    rc_account_status status = RC_ACCOUNT_NO_USER;
    s32 value = 0;
    s32 dir = -1;
    int candidates = 0;

    if (out == NULL || size < 21u)
        return RC_ACCOUNT_NO_USER;
    out[0] = '\0';
    only_dir[0] = '\0';

    /*
     * ASKED FIRST, because it turns every empty result below into an explanation. A console with no
     * PSN account has no np_cache.dat and nothing is wrong with it.
     */
    if (sysUtilGetSystemParamInt(SYSUTIL_SYSTEMPARAM_ID_CURRENT_USER_HAS_NP_ACCOUNT, &value) == 0 &&
        value == 0)
        return RC_ACCOUNT_NO_NP;

    current_user[0] = '\0';
    if (sysUtilGetSystemParamString(SYSUTIL_SYSTEMPARAM_ID_CURRENT_USERNAME, current_user,
                                    (u32)sizeof(current_user) - 1u) != 0)
        current_user[0] = '\0';
    current_user[sizeof(current_user) - 1u] = '\0';
    trim(current_user);

    if (sysLv2FsOpenDir("/dev_hdd0/home", &dir) != 0)
        return RC_ACCOUNT_NO_USER;

    for (;;) {
        sysFSDirent entry;
        u64 read = 0u;
        char user_dir[64];
        char name_path[96];
        unsigned char stored[64];
        size_t got;

        if (sysLv2FsReadDir(dir, &entry, &read) != 0 || read == 0u)
            break;
        entry.d_name[sizeof(entry.d_name) - 1u] = '\0';
        if (entry.d_type != RC_ACC_DT_DIR || entry.d_name[0] < '0' || entry.d_name[0] > '9')
            continue;
        if (snprintf(user_dir, sizeof(user_dir), "/dev_hdd0/home/%.16s", entry.d_name) >=
            (int)sizeof(user_dir))
            continue;

        candidates++;
        if (only_dir[0] == '\0')
            snprintf(only_dir, sizeof(only_dir), "%s", user_dir);

        if (current_user[0] == '\0')
            continue;

        snprintf(name_path, sizeof(name_path), "%s/localusername", user_dir);
        got = read_small(name_path, stored, sizeof(stored) - 1u);
        if (got == 0u)
            continue;
        stored[got] = '\0';
        trim((char *)stored);

        if (strcmp((const char *)stored, current_user) == 0) {
            status = read_np_cache(user_dir, out, size);
            candidates = -1;   /* matched by name - the fallback below must not second-guess it */
            break;
        }
    }
    (void)sysLv2FsCloseDir(dir);

    /*
     * THE FALLBACK IS ONLY SAFE WHEN THERE IS NOTHING TO GET WRONG. One local user means the signed-in
     * one is that user, whatever the name comparison did. Two would mean guessing between accounts, and
     * a client that silently registers under the wrong person's account is not a client anyone can
     * debug - so with two it declines and says which step failed.
     */
    if (candidates == 1) {
        status = read_np_cache(only_dir, out, size);
        rc_log("acct:  one local user and no name match - read from it anyway\n");
    }

    /* The VALUE is never logged, here or anywhere. Its length is safe and says the read worked. */
    rc_log("acct:  %s%s\n", rc_account_status_text(status),
           (status == RC_ACCOUNT_OK) ? " (an account id of the expected length)" : "");
    return status;
}
