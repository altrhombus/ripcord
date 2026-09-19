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
	$(PORT_ROOT)/source/media/rc_h264_annexb.c \
	h264_test.c

# The CSPRNG seam's platform-independent half - chunking, partial-failure handling, the init gate, and
# the mbedtls inversion. rc_random_ps3.c is built UNMODIFIED against stub-sdk/, which stands in for
# PSL1GHT's <lv2/system.h>; random_test.c supplies sysGetRandomNumber itself so every failure a console
# could produce can be asked for on demand. What this does NOT test is lv2, and the file says so at
# length - the two [X] facts about the real call are settled by the bring-up program on hardware.
#
# -I$(CORE) reaches ports/common for util/rc_random.h, the contract both this and the 3DS implement.
RANDOM_TARGET := $(BUILD)/random_test
RANDOM_SOURCES := \
	$(PORT_ROOT)/source/platform/rc_random_ps3.c \
	random_test.c

CORE := $(PORT_ROOT)/../common

# THE BACKGROUND'S TWELVE COLOURS. rc_wave.c is plain arithmetic over a pixel buffer - no PSL1GHT, no
# display - so the one piece of this shell nobody can review by looking at it on any given day can be
# asserted here instead. It needs the host platform seam ports/common/tests already has, for the clock
# rc_wave times itself with, and a stub for rc_log because nothing here wants the output.
WAVE_TARGET := $(BUILD)/wave_test
WAVE_SOURCES := \
	$(PORT_ROOT)/source/ui/rc_wave.c \
	$(CORE)/tests/rc_platform_host.c \
	wave_test.c

CHORD_TARGET := $(BUILD)/chord_test
CHORD_SOURCES := \
	$(PORT_ROOT)/source/input/rc_chord.c \
	chord_test.c

.PHONY: all check build clean

all: check

check: $(H264_TARGET) $(RANDOM_TARGET) $(WAVE_TARGET) $(CHORD_TARGET)
	./$(H264_TARGET)
	./$(RANDOM_TARGET)
	./$(WAVE_TARGET)
	./$(CHORD_TARGET)

build: $(H264_TARGET) $(RANDOM_TARGET) $(WAVE_TARGET) $(CHORD_TARGET)

$(H264_TARGET): $(H264_SOURCES)
	@mkdir -p $(@D)
	$(CC) $(CFLAGS) -o $@ $(H264_SOURCES)

$(RANDOM_TARGET): $(RANDOM_SOURCES) stub-sdk/lv2/system.h
	@mkdir -p $(@D)
	$(CC) $(CFLAGS) -I stub-sdk -I $(CORE) -o $@ $(RANDOM_SOURCES)

$(WAVE_TARGET): $(WAVE_SOURCES)
	@mkdir -p $(@D)
	$(CC) $(CFLAGS) -I $(PORT_ROOT)/source/ui -I $(CORE)/util -I $(CORE)/platform \
		-o $@ $(WAVE_SOURCES) -lm

$(CHORD_TARGET): $(CHORD_SOURCES)
	@mkdir -p $(@D)
	$(CC) $(CFLAGS) -I $(PORT_ROOT)/source/input -I $(CORE)/input -o $@ $(CHORD_SOURCES)

clean:
	rm -rf $(BUILD)
