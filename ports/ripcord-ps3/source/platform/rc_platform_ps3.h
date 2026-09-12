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

#endif /* RC_PLATFORM_PS3_H */
