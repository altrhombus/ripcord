/*
 * ripcord-ps3 - a stand-in for <sys/sysctl.h>, which this target does not have.
 *
 * openh264's WelsThreadLib.cpp includes it to ask the operating system how many logical processors it
 * has. The include is guarded for Linux, Android, Emscripten and Fuchsia, and a bare newlib target is
 * none of those, so it falls through to the BSD path and fails to compile on a missing header.
 *
 * The honest answer for this port is not a number obtained some other way: it is that openh264 should
 * not be sizing a thread pool here at all. The PPE runs the decoder single-threaded and the parallelism
 * goes to the SPEs, which openh264 knows nothing about. So this declares the call and the build supplies
 * one that fails, and openh264's own error path - `pInfo->ProcessorCount = 1` - produces exactly the
 * answer this port wants, by its own logic rather than by a patch to its source.
 *
 * Nothing is modified in openh264. That matters for a dependency fetched and verified at build time:
 * a patch would have to be carried, rebased and justified forever, and a shim on the include path is
 * visible, local, and costs nothing when the dependency moves.
 */
#ifndef RC_PS3_SHIM_SYS_SYSCTL_H
#define RC_PS3_SHIM_SYS_SYSCTL_H

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Always fails. See above: the failure is the point. */
int sysctlbyname(const char *name, void *oldp, size_t *oldlenp, void *newp, size_t newlen);

#ifdef __cplusplus
}
#endif

#endif /* RC_PS3_SHIM_SYS_SYSCTL_H */
