# ripcord-ps3 - the PlayStation 3 build.
#
# Produces ripcord-ps3.self, runnable from a homebrew-enabled console. Requires PSL1GHT and the ps3dev
# PPU toolchain: https://github.com/ps3dev/PSL1GHT - set $PS3DEV and $PSL1GHT.
#
#     make            build the bring-up program
#     make clean
#     make test       run the HOST-side tests (no PS3 involved) - tests/Makefile, and ports/common/tests
#
# *** STATUS: NEVER RUN. *** Unlike the 3DS Makefile, whose flags are known-good against a real
# devkitPro installation, and unlike the Vita's, which produces a .vpk - nothing here has been executed.
# There is no PSL1GHT on the machine this was written on. Every toolchain-facing line is [X], and the
# ones most likely to be wrong are called out where they sit rather than listed here.
#
# NOT IN THE SOLUTION FILE, AND NOT BUILDABLE BY dotnet, for the same reason neither of the other ports
# is: Ripcord.slnx is MSBuild driving .NET and C++/WinRT, and this is a Makefile driving a cross-compiler
# for a different CPU and a different OS.

ifeq ($(strip $(PS3DEV)),)
$(error PS3DEV is not set. Install the ps3dev toolchain and export PS3DEV - see the URL above)
endif
ifeq ($(strip $(PSL1GHT)),)
$(error PSL1GHT is not set. Install PSL1GHT and export PSL1GHT - see the URL above)
endif

PREFIX := $(PS3DEV)/ppu/bin/powerpc64-ps3-elf-
CC     := $(PREFIX)gcc
STRIP  := $(PREFIX)strip

# The portable protocol core, shared with every other Ripcord port. See ports/common/platform/
# rc_platform.h for the seam this build implements in source/platform/rc_platform_ps3.c.
CORE  := ../common
BUILD := build
APP   := ripcord-ps3

# [X] ARCHITECTURE FLAGS - AND THE RULE MATTERS MORE THAN THE VALUE, because the two ports that came
# before were bitten by this in OPPOSITE directions.
#
# The 3DS needs -march=armv6k -mtune=mpcore -mfloat-abi=hard -mtp=soft spelled out, because libctru is
# built with them and a mismatched float ABI links cleanly and then misbehaves at runtime. The Vita needs
# NONE, because vitasdk's defaults are already correct and an earlier revision that passed a faithful-
# looking description of the hardware (-mfloat-abi=softfp) failed the link on every translation unit -
# the hardware description was right and the calling convention was wrong.
#
# The lesson both taught is the same: GET THIS FROM THE SDK, NOT FROM THE HARDWARE. Before trusting the
# line below, read $(PSL1GHT)/ppu_rules and match whatever it compiles its own libraries with, because
# that is the only thing this has to agree with.
ARCH := -mcpu=cell

# -Wframe-larger-than is inherited from both prior ports, where it was not a style preference but a crash
# the hardware had already found. The deepest live path either port measured was the audio loss-
# concealment chain at ~12.4 KB across five frames, every individual frame under the limit - cumulative
# call depth rather than one oversized local. That chain is ports/common code and Opus, so it will exist
# here too. See "the main thread's stack" in README.md for the part of this that is still an open
# question on PS3.
CFLAGS := $(ARCH) -std=c99 -O2 -g \
          -Wall -Wextra -Werror -Wshadow -Wconversion -Wsign-conversion \
          -Wframe-larger-than=8192 \
          -ffunction-sections -fdata-sections \
          -DRC_PROGRAM_DIR_FALLBACK='"/dev_hdd0/game/RIPC00003/USRDIR/"' \
          -I$(CORE) -I$(PSL1GHT)/ppu/include

LDFLAGS := $(ARCH) -Wl,--gc-sections -L$(PSL1GHT)/ppu/lib

# [X] The minimum PSL1GHT libraries for a program that does nothing but keep time and write a file.
# Sockets, video and the SPU job manager are added when the phase that needs them arrives, deliberately
# rather than pre-emptively - an unnecessary library here is a link error nobody can explain later.
LIBS := -lrt -llv2

# The platform seam plus the two core utilities the bring-up program needs. Note what is NOT here:
# there is no rc_random_ps3.c, and that is deliberate - rc_platform_ps3.c explains at length why a
# CSPRNG this port cannot yet confirm must fail to link rather than be stubbed.
SOURCES := \
	$(CORE)/util/rc_log.c \
	$(CORE)/util/rc_program_dir.c \
	source/platform/rc_platform_ps3.c \
	source/app/main.c

.PHONY: all clean test

all: $(APP).self

$(BUILD)/$(APP).elf: $(SOURCES)
	@mkdir -p $(@D)
	$(CC) $(CFLAGS) $(LDFLAGS) -o $@ $(SOURCES) $(LIBS)

# [X] elf -> self, the PSL1GHT packaging step. `fself` produces an unsigned SELF, which is what a
# homebrew-enabled console runs; a retail-signed one is neither available nor wanted here.
$(APP).self: $(BUILD)/$(APP).elf
	$(STRIP) -g $<
	fself $< $@

# Convenience passthrough. These are the tests that actually verify anything today - the H.264 front end
# from steps 2 and 3, and the core's known-answer vectors - and both run on the host with no console.
test:
	$(MAKE) -C tests
	$(MAKE) -C $(CORE)/tests

clean:
	rm -f $(APP).self
	rm -rf $(BUILD)
