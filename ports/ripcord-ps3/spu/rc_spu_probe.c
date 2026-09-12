/*
 * ripcord-ps3 - the SPU side of step 6: one SPE, doing the most trivial useful thing, so the cost of
 * asking it can be measured.
 *
 * This runs on an SPE, not on the PPE. Different compiler (spu-gcc), different instruction set, no
 * access to main memory except by DMA, and 256 KB of local store for code, data and stack together.
 * Nothing in ports/common can be included here and nothing here may call into the PPU side.
 *
 * WHAT IT DOES, AND WHY IT IS DELIBERATELY NOT A DECODER. It copies `size` bytes from one main-memory
 * address to another, 16 KB at a time, then writes a flag the PPE is watching. That is all. The point of
 * step 6 is not to do work on an SPE, it is to find out what it COSTS to hand an SPE a job at all -
 * because that number decides the shape of the decoder, and no amount of reasoning about the Cell
 * substitutes for it. If per-job overhead is large, the decoder must batch whole slices; if it is small,
 * it can pipeline at macroblock-row granularity. README.md's step 7 cannot be designed without it.
 *
 * SINGLE-BUFFERED ON PURPOSE. Issue a get, wait, issue a put, wait. Double-buffering - fetching chunk
 * n+1 while chunk n is being written - is the obvious optimisation and roughly doubles the achievable
 * rate, which is exactly why it is not here. This measurement is meant to be the honest floor that a
 * later one can be compared against, not the best number the hardware can produce.
 *
 * THE 16 KB CHUNK IS THE HARDWARE'S LIMIT, not a tuning choice: a single MFC transfer tops out there.
 * Both effective addresses and the local-store buffer must share 16-byte alignment, and sizes must be a
 * multiple of 16 - the PPU side guarantees all of that and says so.
 */
#include <spu_intrinsics.h>
#include <spu_mfcio.h>

#include <sys/spu_thread.h>

#include "rc_spu_phase.h"

#define TAG 1

/* The MFC's maximum single transfer. Not adjustable upward; the loop exists because of it. */
#define RC_SPU_CHUNK 16384

/* In local store, which is all this program has. 16 KB of the 256 KB budget. */
static unsigned char ls_buffer[RC_SPU_CHUNK] __attribute__((aligned(128)));

/*
 * In local store, read from outside by the PPE. Deliberately NOT static: the Makefile finds its address
 * with spu-nm after linking, and a local symbol would still be findable but is a worse thing to depend
 * on. See rc_spu_phase.h for what its two values distinguish.
 */
volatile uint32_t g_rc_spu_heartbeat = RC_SPU_HEARTBEAT_LOADED;

/* The four effective addresses main() was handed, mirrored where the PPE can read them. See the header
 * for why this is worth four stores. Aligned so the PPE's 8-byte local-store reads are natural. */
volatile uint64_t g_rc_spu_args[RC_SPU_ARG_COUNT] __attribute__((aligned(16)));

static void wait_for_dma(void)
{
    mfc_write_tag_mask(1u << TAG);
    (void)spu_mfcstat(MFC_TAG_UPDATE_ALL);
}

/*
 * Arguments arrive as four 64-bit values from sysSpuThreadInitialize. They are effective addresses in
 * the PPE's address space, which is the only way this program can refer to main memory at all.
 *
 * A size of zero is legitimate and is how the PPU side measures pure overhead: the loop does not run,
 * the flag is still written, and what the PPE times is start-to-finish with no DMA in it.
 */
/* The job block, fetched into local store. 128-byte aligned so the MFC is happy with it either way. */
static rc_spu_job job __attribute__((aligned(128)));

/*
 * ONE ARGUMENT, and it is an address. See rc_spu_phase.h: lv2 delivered arg3 as zero on b10, and the
 * SDK's own samples never populate anything past arg1, so only the first slot has evidence behind it.
 * Everything else arrives in the block this points at.
 */
int main(uint64_t job_ea, uint64_t unused1, uint64_t unused2, uint64_t unused3)
{
    uint64_t moved = 0u;
    uint64_t src_ea, dst_ea, size, done_ea;

    (void)unused1;
    (void)unused2;
    (void)unused3;

    /* Aligned because the MFC requires it of a 4-byte transfer's source as well as its destination. */
    uint32_t phase __attribute__((aligned(16))) = RC_SPU_PHASE_ENTERED;

    /*
     * FIRST INSTRUCTION OF ANY CONSEQUENCE, and it touches nothing but local store. If the PPE reads
     * RUNNING out of this address, the SPE executed code - whatever else went wrong afterwards. It is
     * ahead of the DMA below on purpose: the DMA is what is under suspicion.
     */
    g_rc_spu_heartbeat = RC_SPU_HEARTBEAT_RUNNING;

    /* The address we were given, before anything can go wrong with it. */
    g_rc_spu_args[0] = job_ea;

    /* Fetch the job. This is now the first DMA the program performs, so a fault here means the one
     * argument that IS delivered did not arrive either - a different and much worse finding. */
    mfc_get(&job, job_ea, (uint32_t)sizeof(job), TAG, 0, 0);
    wait_for_dma();

    g_rc_spu_heartbeat = RC_SPU_HEARTBEAT_GOTJOB;

    src_ea  = job.src_ea;
    dst_ea  = job.dst_ea;
    size    = job.size;
    done_ea = job.done_ea;

    /* Mirrored so the PPE can confirm the block survived the round trip, exactly as it confirmed the
     * arguments before. The check that found this bug is worth keeping pointed at its replacement. */
    g_rc_spu_args[1] = src_ea;
    g_rc_spu_args[2] = size;
    g_rc_spu_args[3] = done_ea;

    /*
     * A PROGRESS MARKER, WRITTEN BEFORE ANY WORK. The first version of this program signalled only at
     * the end, and the PPE spun on that signal with no timeout - so when the SPE did not finish, the
     * console hung on a blank screen and the run produced no information at all beyond "something in
     * here". One flag with two values costs one extra DMA and splits that into two very different
     * findings: nothing at all means the SPE never got here, and ENTERED means it ran and then died or
     * stalled in the copy. Those point at completely different bugs.
     */
    mfc_put(&phase, done_ea, 4u, TAG, 0, 0);
    wait_for_dma();

    /* If the PPE reads this, the transfer above returned. An MFC fault stops the SPE, so not reaching
     * this line is itself the finding - see the header. */
    g_rc_spu_heartbeat = RC_SPU_HEARTBEAT_PASTDMA;

    while (moved < size) {
        uint64_t remaining = size - moved;
        uint32_t n = (remaining > (uint64_t)RC_SPU_CHUNK)
                     ? (uint32_t)RC_SPU_CHUNK
                     : (uint32_t)remaining;

        mfc_get(ls_buffer, src_ea + moved, n, TAG, 0, 0);
        wait_for_dma();
        mfc_put(ls_buffer, dst_ea + moved, n, TAG, 0, 0);
        wait_for_dma();

        moved += n;
    }

    /*
     * The completion signal, and it must be the LAST thing: the PPE is watching this word, so writing it
     * before the copy has landed would report a job done that is not. The wait above is what makes that
     * ordering real rather than hoped for - MFC transfers are not ordered against each other without it.
     */
    phase = RC_SPU_PHASE_DONE;
    mfc_put(&phase, done_ea, 4u, TAG, 0, 0);
    wait_for_dma();

    spu_thread_exit(0);
    return 0;
}
