# ripcord-ps3 - host-side self-tests.
#
# Builds for THIS machine, not the PS3: plain C99, no PSL1GHT, no SPU toolchain, no console. The
# bitstream code is portable C and there is no reason to need hardware to find out it is wrong - which
# matters more here than it did for ripcord-3ds, because there is no PS3 on hand at all yet.
#
#     make            build and run
#     make build      build only
#     make clean
#
# No vector file and no .NET codegen step. The parameter sets under test are the console's own,
# transcribed as literals in h264_test.c, and the slice headers are built by a bit writer in the same
# file - see its header for why that split is deliberate rather than lazy.
#
# Override the compiler the usual way (CC=clang, CC=gcc-13, ...).

CC     ?= cc

# -Werror on purpose, and the same set ripcord-3ds uses. -Wconversion in particular earns its place in a
# bitstream parser: nearly every line here mixes widths, and a silent narrowing is exactly the bug that
# makes a parser read the right field at the wrong offset.
CFLAGS ?= -std=c99 -O2 -g -Wall -Wextra -Werror -Wshadow -Wconversion -Wsign-conversion

PORT_ROOT := ..
BUILD     := build

H264_TARGET := $(BUILD)/h264_test
H264_SOURCES := \
	$(PORT_ROOT)/source/media/rc_h264_bits.c \
	$(PORT_ROOT)/source/media/rc_h264_params.c \
	h264_test.c

.PHONY: all check build clean

all: check

check: $(H264_TARGET)
	./$(H264_TARGET)

build: $(H264_TARGET)

$(H264_TARGET): $(H264_SOURCES)
	@mkdir -p $(@D)
	$(CC) $(CFLAGS) -o $@ $(H264_SOURCES)

clean:
	rm -rf $(BUILD)
