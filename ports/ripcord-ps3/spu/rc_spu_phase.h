/*
 * ripcord-ps3 - the one value the SPE and the PPE both have to agree about.
 *
 * Shared by spu/rc_spu_probe.c (compiled by spu-gcc, for the SPE) and source/spu/rc_spu.c (compiled by
 * ppu-gcc, for the PPE). It contains no code and includes nothing, which is what makes it safe to share
 * between two architectures with two toolchains.
 *
 * The values are a progress report written by DMA into a word the PPE polls, so that a run that does
 * not finish still says how far it got. What the PPE sees on a timeout distinguishes:
 *
 *   NOTHING   the word is still zero. The SPE never reached its first instruction, or never managed a
 *             DMA write at all - suspect the image, the thread setup, or the address handed to it.
 *   ENTERED   the SPE ran and wrote this, then stalled or died before finishing - suspect the copy
 *             loop, the addresses it was given, or an MFC alignment fault.
 *   DONE      the job finished.
 */
#ifndef RC_SPU_PHASE_H
#define RC_SPU_PHASE_H

#include <stdint.h>

/* Not 1 and 2. Distinctive values so that a word which happens to hold a small integer for some other
 * reason cannot be mistaken for a report from the SPE. */
#define RC_SPU_PHASE_NOTHING 0x00000000u
#define RC_SPU_PHASE_ENTERED 0x5350454eu   /* 'SPEN' */
#define RC_SPU_PHASE_DONE    0x53504f4bu   /* 'SPOK' */

/*
 * THE HEARTBEAT. The phase word is delivered by DMA, so when the PPE sees NOTHING it cannot tell an SPE
 * that never executed an instruction from one whose DMA never landed.
 *
 * The heartbeat is an ordinary variable in the SPE's local store, written by an ordinary store
 * instruction, and the PPE reads it with sysSpuThreadReadLocalStorage - a syscall that reaches into the
 * SPE's memory from outside without the SPE participating. Nothing between the two is shared except
 * the address, which the Makefile extracts from the linked SPU image with spu-nm.
 *
 *   RUNNING  the SPE executed instructions. If the phase is still NOTHING, the fault is in the DMA or
 *            in the addresses it was handed - not in getting the program to run.
 *   LOADED   the image reached local store but main() never ran. Suspect the entry point, the thread
 *            setup, or the group never actually scheduling.
 *   neither  local store does not contain our program at all. Suspect the import.
 */
#define RC_SPU_HEARTBEAT_LOADED  0x4c4f4144u   /* 'LOAD' - the initialiser in the image */
#define RC_SPU_HEARTBEAT_RUNNING 0x52554e21u   /* 'RUN!' - written by main's first statement */
#define RC_SPU_HEARTBEAT_GOTJOB  0x4a4f4221u   /* 'JOB!' - the job block arrived by DMA */
#define RC_SPU_HEARTBEAT_PASTDMA 0x444d4131u   /* 'DMA1' - the first mfc_put and its wait returned */

/*
 * PASTDMA is the one that splits the case b9 left open. b9 established RUNNING with no phase word: the
 * SPE executed instructions and the PPE never saw its DMA. Two things still fit that.
 *
 *   stuck at RUNNING - the SPE died at or inside the transfer. An MFC fault stops the thread, so it
 *                      never reaches the next statement. Suspect the address it was handed, or the
 *                      alignment rules for a 4-byte put.
 *   reached PASTDMA  - the transfer completed as far as the SPE is concerned, and the PPE still cannot
 *                      see the value. Then the bytes went somewhere other than where the PPE is
 *                      looking, which makes it a question about the address rather than the transfer.
 *
 * Whichever it is, the argument dump below says what address was actually used, which is the other half
 * of the answer.
 */

/* PPE-side only; the SPE never writes these. Distinguishing "we did not look" from "we looked and the
 * syscall refused" matters, because the second is itself a finding about the thread's state. */
#define RC_SPU_HEARTBEAT_UNREAD     0xffffffffu
#define RC_SPU_HEARTBEAT_UNREADABLE 0xfffffffeu

/*
 * THE ARGUMENTS AS THE SPE RECEIVED THEM, mirrored into local store so the PPE can read them back and
 * compare against what it passed. Four u64s, in main's parameter order.
 *
 * This exists because "the DMA did not land" has two halves and only one of them is about DMA. If the
 * effective address the SPE was handed is not the address the PPE is watching, the transfer is working
 * perfectly and pointing at the wrong place - and no amount of staring at alignment rules will show
 * that. Comparing the two sides directly is cheaper than reasoning about the argument-passing path.
 */
#define RC_SPU_ARG_COUNT 4

/*
 * THE JOB BLOCK, and why the arguments are not passed as arguments.
 *
 * MEASURED, b10: of the four values sysSpuThreadInitialize accepts, lv2 delivered arg0, arg1 and arg2
 * intact and arg3 as ZERO. The SPE then DMA'd to effective address 0, the MFC faulted, and the thread
 * died before its next instruction. PSL1GHT's own samples all declare an SPU main taking four u64s, and
 * not one of them populates arg2 or arg3. [X] Whether arg3 is reserved by lv2 for some purpose or simply
 * not delivered is unknown and does not matter here.
 *
 * So a single effective address is passed, pointing at this structure in main memory, which the SPE
 * fetches by DMA before doing anything else. The decoder will need this shape regardless: a decode job
 * has far more than four parameters.
 *
 * 32 BYTES. The MFC rejects any transfer whose size is not 0, 1, 2, 4, 8 or a multiple of 16 - IBM's
 * Cell Broadband Engine Programmers Guide has a worked example of a 24-byte control block that fails
 * for exactly this reason. Four u64s come to 32. Anything added here must keep it a multiple of 16, and
 * the guide's advice is to pad explicitly rather than to count.
 *
 * Both copies are 128-byte aligned, which the same guide names as the alignment at which DMA reaches
 * peak performance rather than merely working (16 is the minimum for transfers of 16 bytes or more).
 */
typedef struct {
    uint64_t src_ea;
    uint64_t dst_ea;
    uint64_t size;
    uint64_t done_ea;
} rc_spu_job;

#endif /* RC_SPU_PHASE_H */
