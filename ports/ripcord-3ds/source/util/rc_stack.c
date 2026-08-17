/*
 * ripcord-3ds - the main-thread stack size, and why the default is not enough.
 *
 * libctru defines `__stacksize__` as a WEAK symbol with the value 0x8000 - 32 KB for the whole main
 * thread. A strong definition here overrides it. This port creates no threads of its own during a
 * session (see connect/main.c), so *every* stage - receive, GMAC verify, decrypt, demux, MVD feed,
 * render, frame scale and Opus decode - shares that one stack.
 *
 * WHAT 32 KB ACTUALLY COST, TWICE.
 *
 * 2026-08-12: a data abort writing 12 bytes below SP, with a log that stopped after the banner. That one
 * was a single oversized local (takion_reliable_channel, 49.6 KB) blowing the frame in the PROLOGUE, and
 * the fix was -Wframe-larger-than=8192 in the Makefile, which fails the build instead of the handheld.
 *
 * 2026-08-13, crash_dump_00000009: a data abort writing 40 bytes below SP - a plain
 * `push {r4-r11, ip, lr}` register save area that could not be stored. -Wframe-larger-than cannot catch
 * this one and never could: 40 bytes is a *small* frame. The stack was exhausted by CUMULATIVE CALL
 * DEPTH, with every individual frame under the limit. Measured from the linked binary, the deepest live
 * path is the audio loss-concealment chain:
 *
 *     run_media 2216 -> takion_channel_poll 1632 -> ingest_audio 2088 -> rc_audio_submit 1960
 *         -> opus_decode -> celt_decode_lost 4520                            = ~12.4 KB in five frames
 *
 * celt_decode_lost is the tell: it runs only when a packet is LOST, so the deepest the stack ever gets
 * is a path that ordinary testing does not take. The session that produced the dump logged 477 loss
 * events. Stack an rc_log on top of that (_vfprintf_r 776 + __sbprintf 1168, and float conversion is why
 * the teardown summary was the trigger) and 32 KB is simply gone. Fifteen functions in the connect
 * binary use >= 1 KB each and sum to 28.4 KB on their own.
 *
 * WHY 128 KB. Four times the measured worst path, which leaves room for the frames below it that were
 * never measured (libctru, newlib, mbedtls) without pretending the measurement was exhaustive. It is
 * bought from a heap that reports ~29 MB free at the point the MVD buffers are already allocated, so the
 * trade is not close. This is headroom, NOT a licence to put big objects on the stack: the
 * -Wframe-larger-than=8192 rule in the Makefile still stands, and anything large still belongs in .bss
 * or on the heap.
 */

unsigned int __stacksize__ = 128 * 1024;
