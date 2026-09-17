/*
 * ripcord-ps3 - the handful of things that are true of this port's seam and of nothing else.
 *
 * ports/common/platform/rc_platform.h is the seam the portable core sees, and it is deliberately
 * platform-free. This header is the other side of that: PS3-only facts that a PS3-only program - the
 * bring-up in source/app/ - has a legitimate reason to read. Nothing in ports/common may include it.
 *
 * It exists to stop one specific thing: a second copy of the expected time base frequency. main.c wants
 * to cross-check what lv2 reports against what this port expects, and the alternative to declaring it
 * here was to write 79,800,000 in two files and hope they stayed together. They do not; that is why
 * this project does not keep second copies of anything.
 */
#ifndef RC_PLATFORM_PS3_H
#define RC_PLATFORM_PS3_H

#include <stdint.h>

/*
 * The PPE time base frequency this port EXPECTS - 3.2 GHz over 40 - as opposed to rc_tick_hz(), which
 * reports what lv2 actually says. They should agree. If they ever do not, the kernel is right and this
 * figure is the one to correct; see rc_platform_ps3.c for why the authority runs that way round.
 */
uint64_t rc_ps3_timebase_hz_expected(void);

/*
 * THE XMB CAN ASK THIS PROGRAM TO STOP, AND IGNORING IT IS NOT A NO-OP.
 *
 * Pressing PS and choosing Quit does not kill the process. lv2 raises SYSUTIL_EXIT_GAME through
 * whatever callback the program registered, waits for it to leave of its own accord, and force-
 * terminates it when it does not. This program registered nothing, so every XMB quit took the second
 * path: the RSX still holding a context, an SPU thread group still running - and that group is
 * deliberately never destroyed, see rc_spu_yuv.h on the two builds where destroying it locked the
 * console. What that looks like from the sofa is three beeps and a reboot.
 *
 * `rc_ps3_exit_watch` registers the handler once, at start-up. `rc_ps3_exit_requested` is what every
 * loop that calls sysUtilCheckCallback should be testing, so the request is answered by unwinding to
 * main() and running the ordinary teardown - which closes the logs first, for the reason main.c gives.
 *
 * The slot matters: the on-screen keyboard uses SYSUTIL_EVENT_SLOT0 while it is up, so this takes
 * another one rather than fighting it for the same one.
 */
void rc_ps3_exit_watch(void);
int  rc_ps3_exit_requested(void);

#endif /* RC_PLATFORM_PS3_H */
