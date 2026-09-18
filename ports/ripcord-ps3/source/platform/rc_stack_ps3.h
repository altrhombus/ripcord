/*
 * How much PPU stack this thread has, and how much of it is gone.
 *
 * This exists because a deep call started reading a caller's local through a pointer that had gone bad,
 * and because adding one more read-only call frame changed the result. Both are what running out of
 * stack looks like from above, and neither is something to argue about when lv2 will simply say.
 *
 * SYS_PROCESS_PARAM is the reason it is in doubt. PSL1GHT's header defines the stack size as an ENUM -
 * 0x20 for 64K, 0x70 for 1M - while its own samples pass a raw byte count like 0x100000, and this port
 * passes the byte count. If lv2 wants the enum, 0x100000 is not one, and what the process actually got
 * is whatever lv2 does with a value it does not recognise. sysThreadGetStackInformation answers that
 * with a number instead of a reading of the documentation.
 */
#ifndef RC_STACK_PS3_H
#define RC_STACK_PS3_H

#include <stddef.h>

/* Call once, early, from the thread whose stack is in question. */
void rc_stack_probe_init(void);

/* Bytes of stack this thread was given, and bytes used at the point of the call. Either may be 0 if
 * lv2 declined to say. `headroom` is what is left, which is the number that decides anything. */
void rc_stack_probe(unsigned long *out_size, unsigned long *out_used, unsigned long *out_headroom);

#endif /* RC_STACK_PS3_H */
