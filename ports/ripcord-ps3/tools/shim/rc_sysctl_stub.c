/* The other half of tools/shim/sys/sysctl.h - see that file for why this always fails. */
#include <stddef.h>
#include <sys/sysctl.h>

int sysctlbyname(const char *name, void *oldp, size_t *oldlenp, void *newp, size_t newlen)
{
    (void)name; (void)oldp; (void)oldlenp; (void)newp; (void)newlen;
    return -1;
}
