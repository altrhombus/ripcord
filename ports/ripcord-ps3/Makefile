# ripcord-ps3 - the PlayStation 3 build.
#
# Produces ripcord-ps3.self, runnable from a homebrew-enabled console. Requires PSL1GHT and the ps3dev
# PPU toolchain: https://github.com/ps3dev/PSL1GHT - set $PS3DEV and $PSL1GHT.
#
#     make            build the bring-up program
#     make clean
#     make test       run the HOST-side tests (no PS3 involved) - tests/Makefile, and ports/common/tests
#
# STATUS: this builds AND the result runs on a real PS3. Built against ps3dev nightly-2026-07-26
# (GCC 7.2.0, PSL1GHT f649a08) on Ubuntu 26.04; the bring-up program ran on hardware on 2026-09-11 with
# every check passing, measuring the PPE time base at 79,800,986 Hz against an expected 79,800,000.
#
# Getting there took five hardware runs, four of which produced no output at all, and BOTH causes were in
# this file rather than in the program: a missing sprxlinker step and a stack size given in the wrong
# units. SETUP.md section 6 is the post-mortem. If you are editing the packaging rules below, read it
# first - the failure mode is a console that launches the program and returns to the XMB in silence.
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

# Packaging tools, all from $(PS3DEV)/bin. Only `make pkg` uses these; a plain `make` needs fself alone.
# sprxlinker. Not optional - see the .ready.elf rule below for what leaving it out does.
SPRX           := $(PS3DEV)/bin/sprxlinker
FSELF          := $(PS3DEV)/bin/fself
SELF_NPDRM     := $(PS3DEV)/bin/make_self_npdrm
SFO            := $(PS3DEV)/bin/sfo
PKG            := $(PS3DEV)/bin/pkg
PKG_FINALIZE   := $(PS3DEV)/bin/package_finalize
SFOXML         := $(PS3DEV)/bin/sfo.xml
ICON0          := $(PS3DEV)/bin/ICON0.PNG

# The identity the console files this under. APPID is what creates /dev_hdd0/game/RIPC00003/ on install;
# CONTENTID follows the UP0001-<appid>_00-<16 chars> shape that $(PSL1GHT)/ppu_rules uses for its own
# default, and make_self_npdrm warns that the OUTPUT FILENAME matters to NPDRM - hence EBOOT.BIN exactly.
# THE BUILD NUMBER, AND WHY A BRING-UP PROGRAM HAS ONE.
#
# Five hardware runs were needed to get this program to start, and for all five the only evidence of
# WHICH binary had actually executed was the byte size of the installed EBOOT - inferred from the far
# end of an FTP listing. That is a coincidence away from being wrong, and a wrong answer there sends you
# debugging code that never ran. A stale install and a broken program look identical.
#
# So every build gets a number, and it appears in three places that have to agree: the XMB title, the
# beacon file, and the first line of both logs. If they do not match what `make` last printed, stop -
# the console is running something else and nothing else you are looking at means anything.
#
# The counter lives in .buildno at the port root, NOT under build/, so that `make clean` does not reset
# it and start handing out numbers that have already been used. It is gitignored: it is per-machine
# state about how many times somebody pressed make, which is not a fact about the project.
BUILDNO_FILE := .buildno
BUILD_NO      = $(shell cat $(BUILDNO_FILE) 2>/dev/null || echo 0)

# Recursively expanded (=, not :=) on purpose: the number is read when the packaging recipe RUNS, which
# is after the compile step has bumped it. A := here would bake in the previous build's number and
# produce exactly the mismatch this exists to detect.
TITLE     = Ripcord bring-up b$(BUILD_NO)
APPID     := RIPC00003
CONTENTID := UP0001-$(APPID)_00-0000000000000000

# The portable protocol core, shared with every other Ripcord port. See ports/common/platform/
# rc_platform.h for the seam this build implements in source/platform/rc_platform_ps3.c.
CORE  := ../common
BUILD := build
APP   := ripcord-ps3

# ARCHITECTURE FLAGS - AND THE RULE MATTERS MORE THAN THE VALUE, because the two ports that came before
# were bitten by this in OPPOSITE directions.
#
# The 3DS needs -march=armv6k -mtune=mpcore -mfloat-abi=hard -mtp=soft spelled out, because libctru is
# built with them and a mismatched float ABI links cleanly and then misbehaves at runtime. The Vita needs
# NONE, because vitasdk's defaults are already correct and an earlier revision that passed a faithful-
# looking description of the hardware (-mfloat-abi=softfp) failed the link on every translation unit -
# the hardware description was right and the calling convention was wrong.
#
# The lesson both taught is the same: GET THIS FROM THE SDK, NOT FROM THE HARDWARE. This line is now the
# SDK's own answer, copied from MACHDEP in $(PSL1GHT)/ppu_rules, which is the variable PSL1GHT's own
# samples put into their CFLAGS - minus the two -f*-sections flags, which CFLAGS below already carries.
# An earlier revision guessed -mcpu=cell from the hardware. It does compile and link, so this was a
# correction made on the rule rather than on a failure; the rule is what has a track record.
ARCH := -mhard-float -fmodulo-sched

# WHERE THE LOG GOES, and it is /dev_hdd0/ rather than this program's own install directory on purpose.
#
# main.c calls rc_log_open(NULL, ...) - it is `int main(void)`, so there is no argv[0] to derive a
# directory from and rc_program_dir falls back to this string every time. The first version of that
# string was /dev_hdd0/game/RIPC00003/USRDIR/, which is where `make pkg` installs and is therefore right
# for a packaged build and WRONG for every other way of starting this program. A .self run through
# ps3load, or copied somewhere by hand, would find no such directory, fail to open the log, and report
# it to a TTY nobody is reading. The only symptom is a program that appears to do nothing.
#
# /dev_hdd0/ exists and is writable on every PS3 regardless of how the program was started, and the log
# is one FTP fetch away. The bring-up program's entire output is this file; a path that works only under
# one launch method is not a reasonable thing to bet the run on.
#
# The real client, when it has settings to store, should use its own install directory and derive it
# from argv[0] - that is what the seam is for. This is a bring-up program with one job.
LOG_DIR := /dev_hdd0/

# -Wframe-larger-than is inherited from both prior ports, where it was not a style preference but a crash
# the hardware had already found. The deepest live path either port measured was the audio loss-
# concealment chain at ~12.4 KB across five frames, every individual frame under the limit - cumulative
# call depth rather than one oversized local. That chain is ports/common code and Opus, so it will exist
# here too. See "the main thread's stack" in README.md for the part of this that is still an open
# question on PS3.

# -std=gnu99, NOT -std=c99, and this is not a style preference. PSL1GHT's syscall wrappers are built on
# `register u64 p1 asm("3")` in ppu-lv2.h, and in strict ISO mode `asm` is not a keyword - so the first
# thing to include <sys/systime.h> under -std=c99 gets "expected '=', ',', ';', 'asm' or '__attribute__'
# before 'asm'" pointing into the SDK's own header, which reads like a broken installation and is not
# one. Anything that includes a PSL1GHT header needs GNU mode. tests/Makefile stays on -std=c99 on
# purpose: nothing it builds touches the SDK, so it keeps the stricter setting.
CFLAGS := $(ARCH) -std=gnu99 -O2 -g \
          -Wall -Wextra -Werror -Wshadow -Wconversion -Wsign-conversion \
          -Wframe-larger-than=8192 \
          -ffunction-sections -fdata-sections \
          -DRC_PROGRAM_DIR_FALLBACK='"$(LOG_DIR)"' \
          -DRC_PS3_APPID='"$(APPID)"' \
          -I$(CORE) -I$(BUILD) -I$(PSL1GHT)/ppu/include

LDFLAGS := $(ARCH) -Wl,--gc-sections -L$(PSL1GHT)/ppu/lib

# [X] The minimum PSL1GHT libraries for a program that does nothing but keep time and write a file.
# Sockets, video and the SPU job manager are added when the phase that needs them arrives, deliberately
# rather than pre-emptively - an unnecessary library here is a link error nobody can explain later.
LIBS := -lrt -llv2

# The platform seam plus the two core utilities the bring-up program needs. rc_random_ps3.c joined this
# list on 2026-09-11, when the CSPRNG call was confirmed against real headers; before that its ABSENCE
# was the point, so that anything asking for key material failed to link rather than got a stub. The
# file itself records what is confirmed and what is still [X].
SOURCES := \
	$(CORE)/util/rc_log.c \
	$(CORE)/util/rc_program_dir.c \
	source/platform/rc_platform_ps3.c \
	source/platform/rc_random_ps3.c \
	source/app/main.c

.PHONY: all clean test pkg

all: $(APP).self

# Regenerated, and the counter bumped, whenever a source file changes - so an unchanged tree rebuilt
# twice keeps its number rather than inflating it.
$(BUILD)/rc_build_id.h: $(SOURCES)
	@mkdir -p $(@D)
	@n=$$(( $$(cat $(BUILDNO_FILE) 2>/dev/null || echo 0) + 1 )); \
	 echo $$n > $(BUILDNO_FILE); \
	 printf '/* Generated by the Makefile. Not checked in. */\n#define RC_PS3_BUILD_ID "b%s"\n' "$$n" > $@; \
	 echo "  build id: b$$n"

$(BUILD)/$(APP).elf: $(SOURCES) $(BUILD)/rc_build_id.h
	@mkdir -p $(@D)
	$(CC) $(CFLAGS) $(LDFLAGS) -o $@ $(SOURCES) $(LIBS)

# THE PREPARED ELF - strip, then sprxlinker. BOTH STEPS, IN THAT ORDER, AND THE SECOND ONE IS NOT
# OPTIONAL: leaving it out is what made the first three hardware runs produce no output at all.
#
# $(PSL1GHT)/ppu_rules' own %.self rule runs $(STRIP), then $(SPRX), then the self tool. This Makefile
# was written from that rule and dropped the middle line - it looked like an optional post-processing
# pass. It is not. sprxlinker patches the PRX import stubs in .sceStub.text, and until it has run every
# call through a stub - sysGetRandomNumber here, and whatever lv2-crt0 touches on the way to main - is
# unresolved. The program died before its first instruction, which is why no amount of logging machinery
# downstream ever produced a byte, including a beacon that was one syscall with no stack cost.
#
# A SEPARATE OUTPUT FILE RATHER THAN IN-PLACE, which the SDK's rule does by stripping to $(BUILDDIR).
# sprxlinker rewrites its argument in place, and running it twice on one file is not something to find
# out about the hard way - so the raw link output is never touched and this is what the packagers read.
$(BUILD)/$(APP).ready.elf: $(BUILD)/$(APP).elf
	@test -x "$(SPRX)" || { echo "ERROR: sprxlinker not found at $(SPRX) - the build would silently produce an unrunnable binary. See this rule's comment."; exit 1; }
	cp $< $@
	$(STRIP) -g $@
	$(SPRX) $@

# elf -> self. `fself` produces an unsigned SELF, which is what a homebrew-enabled console runs; a
# retail-signed one is neither available nor wanted here.
$(APP).self: $(BUILD)/$(APP).ready.elf
	$(FSELF) $< $@

# THE PACKAGE, for launching from the XMB rather than through a loader.
#
# `make` alone gives you ripcord-ps3.self, which is what ps3load sends over the network and what most
# file-manager homebrew will run in place. `make pkg` gives you ripcord-ps3.pkg, which installs through
# the console's own package installer, appears on the XMB, and creates /dev_hdd0/game/$(APPID)/. Which
# one you want depends on how the console is set up, not on which is better - see SETUP.md section 6.
#
# The steps are PSL1GHT's own, from the %.pkg rule in $(PSL1GHT)/ppu_rules, spelled out here rather than
# inherited because this Makefile deliberately does not include ppu_rules - it sets its own warning and
# architecture flags and including that file would quietly replace them.
$(APP).pkg: $(BUILD)/$(APP).ready.elf
	@rm -rf $(BUILD)/pkg
	@mkdir -p $(BUILD)/pkg/USRDIR
	cp $(ICON0) $(BUILD)/pkg/ICON0.PNG
	$(SELF_NPDRM) $< $(BUILD)/pkg/USRDIR/EBOOT.BIN $(CONTENTID) > /dev/null
	$(SFO) --title "$(TITLE)" --appid "$(APPID)" -f $(SFOXML) $(BUILD)/pkg/PARAM.SFO
	$(PKG) --contentid $(CONTENTID) $(BUILD)/pkg/ $@ > /dev/null
	@cp $@ $(APP).gnpdrm.pkg
	$(PKG_FINALIZE) $(APP).gnpdrm.pkg

pkg: $(APP).pkg

# Convenience passthrough. These are the tests that actually verify anything today - the H.264 front end
# from steps 2 and 3, the CSPRNG seam's host-testable half, and the core's known-answer vectors. All run
# on the host with no console.
test:
	$(MAKE) -C tests
	$(MAKE) -C $(CORE)/tests

# Note what clean does NOT remove: $(BUILDNO_FILE). See the comment on BUILD_NO.
clean:
	rm -f $(APP).self $(APP).pkg $(APP).gnpdrm.pkg
	rm -rf $(BUILD)
