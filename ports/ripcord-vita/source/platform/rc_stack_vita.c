/*
 * ripcord-vita - the main-thread stack size, set before it can cost a crash dump.
 *
 * This is the Vita's counterpart to ports/ripcord-3ds/source/util/rc_stack.c, and it is written FIRST
 * rather than after a data abort, because the 3DS already paid for the lesson twice and the measurement
 * transfers.
 *
 * WHAT THE 3DS MEASURED. Its default was libctru's 32 KB for the whole main thread, and the port needed
 * 128 KB. The second crash was the instructive one: not a single oversized frame (that class is caught
 * at build time by -Wframe-larger-than=8192, which this port also sets) but CUMULATIVE CALL DEPTH, every
 * individual frame under the limit. Measured from the linked binary, the deepest live path was the
 * audio loss-concealment chain:
 *
 *     run_media 2216 -> takion_channel_poll 1632 -> ingest_audio 2088 -> rc_audio_submit 1960
 *         -> opus_decode -> celt_decode_lost 4520                            = ~12.4 KB in five frames
 *
 * celt_decode_lost is the tell: it runs only when a packet is LOST, so the deepest the stack ever gets
 * is a path ordinary testing does not take. **That entire chain is shared code and will exist here too**
 * - ports/common's takion and stream layers are the same files, and Opus is the same library.
 *
 * WHY THIS NUMBER. The Vita's default main-thread stack is reported as 4 KiB [X] - an order of magnitude
 * tighter than the 32 KB that already crashed the 3DS twice - and the deepest measured path is ~12.4 KB
 * before any libc, kernel or Opus frames underneath it. 256 KB is double what the 3DS settled on, which
 * is not extravagance: this port has 256 MB of main memory against the 3DS's 128 MB total, so the trade
 * is even less close than it was there. It is headroom, NOT a licence to put big objects on the stack -
 * -Wframe-larger-than=8192 still stands and anything large still belongs in .bss or on the heap.
 *
 * WHY `used, retain` AND NOT A BARE DEFINITION - this is not defensive decoration, it is a bug that
 * already happened here and was caught by `nm` rather than by hardware.
 *
 * Nothing in this program references this symbol; the LOADER reads it, before main() runs. With the
 * -fdata-sections this port compiles with, the definition lands in its own section
 * (.data.sceUserMainThreadStackSize), and -Wl,--gc-sections then collects it as unreachable. The build
 * succeeds, the .vpk is produced, and the stack size is silently the 4 KiB default. Verified: without
 * the attribute below the symbol is present in the object file and ABSENT from the linked ELF.
 *
 * The mechanism that actually works here is the LINKER's, not the compiler's: the Makefile passes
 * -Wl,--undefined=sceUserMainThreadStackSize, which makes the symbol a root of the reachability graph
 * that --gc-sections walks. `__attribute__((used))` alone is not enough - it stops the COMPILER
 * discarding the definition and says nothing to the linker.
 *
 * `__attribute__((retain))` (SHF_GNU_RETAIN) would be the self-contained fix and is rejected here:
 * GCC 15.2 accepts the attribute but vitasdk's binutils does not support the section flag, so it
 * warns "attribute ignored" and -Werror turns that into a build failure. Worth knowing before
 * reaching for it again.
 *
 * [X] THE MECHANISM ITSELF IS STILL UNVERIFIED. The symbol is now definitely IN the binary, which is a
 * different claim from the loader actually honouring it. No vitasdk header declares the name (it is
 * defined BY the application, not the SDK), so a typo would compile, link, and be ignored. Confirm on
 * hardware that the value takes effect before trusting it.
 */

#include <psp2/types.h>

__attribute__((used))
unsigned int sceUserMainThreadStackSize = 256 * 1024;
