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

# make takes the FIRST EXPLICIT TARGET as its default goal, and the rules below are no longer in an
# order where that is $(APP).self - the generated-constants rule comes earlier. ports/common/tests
# pinned its own goal for exactly this reason and said so; the trap is that nothing fails, `make`
# simply builds one file and stops, and it looks like the tree is up to date.
.DEFAULT_GOAL := all

PREFIX := $(PS3DEV)/ppu/bin/powerpc64-ps3-elf-
CC     := $(PREFIX)gcc
CXX    := $(PREFIX)g++
AS     := $(PREFIX)as
STRIP  := $(PREFIX)strip

# THE SECOND COMPILER. The SPEs are a different architecture with a different instruction set and their
# own toolchain - this is the thing README.md means when it says the PS3 costs strictly more toolchain
# surface than either port before it. spu/ is built by SPU_CC into an ELF, that ELF becomes a binary
# blob, and the blob is assembled into the PPU binary by the PPU assembler. Three tools, one program.
SPU_PREFIX := $(PS3DEV)/spu/bin/spu-
SPU_CC     := $(SPU_PREFIX)gcc
BIN2S      := $(PS3DEV)/bin/bin2s

# Packaging tools, all from $(PS3DEV)/bin. Only `make pkg` uses these; a plain `make` needs fself alone.
# sprxlinker. Not optional - see the .ready.elf rule below for what leaving it out does.
SPRX           := $(PS3DEV)/bin/sprxlinker
SELF           := $(PS3DEV)/bin/make_self
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
# The lesson both taught is the same: GET THIS FROM THE SDK, NOT FROM THE HARDWARE - and getting it from
# the SDK means reading what the SDK's projects actually compile with, not just the variable it defines.
#
# An earlier revision guessed -mcpu=cell from the hardware, and a later one replaced it with MACHDEP from
# $(PSL1GHT)/ppu_rules on the grounds that the SDK should decide. Both were half right. Twenty-five of
# PSL1GHT's sample Makefiles, and bucanero's apollo-ps3, all write:
#
#     CFLAGS = -O2 -Wall -mcpu=cell $(MACHDEP) $(INCLUDE)
#
# MACHDEP AND -mcpu=cell TOGETHER. MACHDEP is the float ABI and scheduling flags every PSL1GHT library is
# built with, which is the part that has to agree; -mcpu=cell is tuning for the processor the code will
# actually run on, which the SDK leaves to the project and every project sets. The -f*-sections half of
# MACHDEP is omitted here only because CFLAGS below already carries it.
ARCH := -mcpu=cell -mhard-float -fmodulo-sched

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

# THE NETWORK LOG. Every line the bring-up program prints is also sent as a UDP datagram here, which is
# the only channel that needs no write permission on the console - see source/net/rc_netlog.h. Override
# on the command line: make RC_NETLOG_HOST=192.168.1.10
#
# Listen for it with:  nc -u -l -p 9000      (or: socat -u UDP-RECV:9000 -)
RC_NETLOG_HOST ?= 192.168.1.10
RC_NETLOG_PORT ?= 9000

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
# HEADER DEPENDENCIES, and this Makefile had none until a hardware run went looking for a bug that was
# not there. Editing a header rebuilt nothing that included it, so a struct that gained a field left every
# object compiled before the edit reading the OLD layout while the ones after it read the new. What that
# looked like from the console was one core suite failing for four days, on big-endian, in the crypto -
# and it was two objects disagreeing about where a field lived.
#
# -MMD writes a .d beside each .o listing the headers it used; -MP adds a phony target for each of those,
# so deleting or renaming a header does not wedge make with "no rule to make target". The .d files are
# included at the bottom, after the goals they might otherwise become.
DEPFLAGS := -MMD -MP

CFLAGS := $(ARCH) -std=gnu99 -O2 -g \
          -Wall -Wextra -Werror -Wshadow -Wconversion -Wsign-conversion \
          -Wframe-larger-than=8192 \
          -ffunction-sections -fdata-sections \
          -DRC_PROGRAM_DIR_FALLBACK='"$(LOG_DIR)"' \
          -DRC_PS3_APPID='"$(APPID)"' \
          -DRC_NETLOG_HOST='"$(RC_NETLOG_HOST)"' -DRC_NETLOG_PORT=$(RC_NETLOG_PORT) \
          -I$(CORE) -I$(BUILD) -Isource/platform -Isource/video -Isource/spu -Isource/net -Isource/media -Isource/coretest \
          -Isource/discovery -Isource/connect -I$(CORE)/discovery -I$(CORE)/session \
          -I$(CORE)/halyard -I$(CORE)/crypto -I$(CORE)/net -I$(CORE)/takion -I$(CORE)/stream -I$(CORE)/util -I$(CORE)/platform -Ispu \
          -I$(PSL1GHT)/ppu/include

LDFLAGS := $(ARCH) -Wl,--gc-sections -L$(PSL1GHT)/ppu/lib

# [X] The minimum PSL1GHT libraries for a program that does nothing but keep time and write a file.
# Sockets, video and the SPU job manager are added when the phase that needs them arrives, deliberately
# rather than pre-emptively - an unnecessary library here is a link error nobody can explain later.
# -lnet joined this list when the UDP log channel did. netInitialize() and the BSD socket names both
# come from it, and it is the first PS3 code in this port to open a socket at all.
# -lsysmodule is not optional beside -lnet: netInitialize() calls sysModuleLoad, and without it the link
# fails on that symbol rather than on anything mentioning networking. PSL1GHT's network samples all pair
# the two - `LIBS := -lnet -lsysmodule` - which is the fastest place to have learned it.
LIBS := -lrsx -lgcm_sys -lsysutil -lvdec -lnet -lsysmodule -lpthread -lrt -llv2

# The platform seam plus the two core utilities the bring-up program needs. rc_random_ps3.c joined this
# list on 2026-09-11, when the CSPRNG call was confirmed against real headers; before that its ABSENCE
# was the point, so that anything asking for key material failed to link rather than got a stub. The
# file itself records what is confirmed and what is still [X].
SOURCES := \
	$(CORE)/util/rc_log.c \
	$(CORE)/util/rc_program_dir.c \
	source/platform/rc_platform_ps3.c \
	source/platform/rc_random_ps3.c \
	source/platform/rc_stack_ps3.c \
	source/video/rc_video_ps3.c \
	source/spu/rc_spu.c \
	source/spu/rc_spu_yuv.c \
	source/net/rc_netlog.c \
	source/media/rc_h264_annexb.c \
	source/media/rc_h264_bits.c \
	source/media/rc_h264_params.c \
	source/media/rc_vdec_probe.c \
	source/discovery/rc_discover.c \
	source/connect/rc_connect.c \
	$(CORE)/discovery/halyard_discovery.c \
	$(CORE)/discovery/halyard_wake.c \
	$(CORE)/session/halyard_pairing_file.c \
	$(CORE)/session/halyard_control_session.c \
	$(CORE)/net/rc_tcp.c \
	$(CORE)/net/rc_udp.c \
	$(CORE)/takion/takion_message.c \
	$(CORE)/takion/takion_handshake.c \
	$(CORE)/takion/takion_data_chunk.c \
	$(CORE)/takion/takion_sack_chunk.c \
	$(CORE)/takion/takion_reassembler.c \
	$(CORE)/takion/takion_reliable_channel.c \
	$(CORE)/takion/takion_control_proto.c \
	$(CORE)/takion/takion_session_negotiator.c \
	$(CORE)/takion/senkusha_echo.c \
	$(CORE)/takion/takion_control_sealer.c \
	$(CORE)/stream/stream_packet_crypto.c \
	$(CORE)/stream/stream_demux.c \
	$(CORE)/stream/stream_header.c \
	$(CORE)/stream/fec_galois.c \
	$(CORE)/stream/fec_reed_solomon.c \
	$(CORE)/session/halyard_launch_spec.c \
	$(CORE)/stream/stream_key_schedule.c \
	$(CORE)/crypto/rc_ecdh.c \
	$(CORE)/util/rc_base64.c \
	$(CORE)/util/rc_hex.c \
	$(CORE)/util/rc_text.c \
	source/app/main.c

# THE DECODER, and the one C++ translation unit in this port. openh264's ISVCDecoder is an abstract class
# rather than a C handle, so the file that drives it has to be C++; everything it exposes is extern "C"
# and the rest of the port stays C99. tools/build-openh264.sh produces the library - run it once.
# NOT UNDER $(BUILD). `make clean` wiped it once, and rebuilding costs a 57 MB fetch and 35 compiles -
# a clean of this port's own objects has no business destroying a verified third-party dependency. It
# lives beside them instead, gitignored, and `make distclean` is what removes it deliberately.
OPENH264_DIR ?= third-party/openh264
OPENH264_LIB := $(OPENH264_DIR)/libopenh264dec.a
OPENH264_SRC := $(OPENH264_DIR)/openh264-2.6.0
OPENH264_INC := -I$(OPENH264_SRC)/codec/api/wels -I$(OPENH264_SRC)/codec/api

CXXSOURCES := source/media/rc_decode_probe.cpp

# ---------------------------------------------------------------------------------------------------
# ports/common's OWN TEST SUITES, cross-compiled and run on the console. See source/coretest/ for why.
#
# Seven of the eleven runners - the ones needing no external .kat file - plus the core sources they
# exercise. Each runner keeps its own main(), renamed at compile time so seven of them can share one
# binary; that is the only modification, and it is a -D rather than an edit.
CORE_TEST_DIR := $(CORE)/tests
# The seven that carry their own expectations, and the four that check against .kat vectors emitted by
# the .NET side. vector_runner is named for its file rather than its subject - it is the control crypto.
CORE_TEST_RUNNERS := discovery session takion fec stream_header stream_demux input \
                     stream_crypto ecdh control_proto
CORE_TEST_EXTRA_OBJS := $(BUILD)/ct_vector_runner.o

# Taken from ports/common/tests' own variables rather than assembled by hand - an earlier revision
# guessed from a grep, missed halyard_sess_request.c and stream_demux.c, and got a page of undefined
# references for its trouble.
# NOTE what is NOT here: halyard_discovery.c and rc_text.c. The application links those itself now that
# source/discovery/ uses them, and compiling a translation unit twice into one binary is a duplicate
# symbol rather than a convenience. The test objects use the application's copies.
# WHAT IS *NOT* HERE: the five Takion sources the application itself now links - data_chunk, handshake,
# message, reassembler and sack_chunk. They moved to SOURCES when the connect probe grew a real Takion
# stage, and a source cannot be in both lists: each list compiles it into its own object, and two objects
# defining the same symbols do not link.
#
# The application's copy wins rather than this one, and that direction matters. These objects are built
# with CORE_TEST_CFLAGS, which is deliberately relaxed - it exists so the core's test harness need not
# satisfy a warning set written for this port. Shipping the relaxed build inside the application would
# quietly exempt real application code from the port's own flags. The test runners link against the strict
# objects instead, which is the same code either way.
CORE_TEST_SUPPORT := \
	$(CORE)/crypto/rc_aes.c $(CORE)/crypto/rc_gcm.c $(CORE)/crypto/rc_modes.c \
	$(CORE)/crypto/rc_sha256.c \
	$(CORE)/halyard/halyard_control_crypto.c \
	$(CORE)/input/halyard_input.c \
	$(CORE)/session/halyard_control_arm.c $(CORE)/session/halyard_ctrl_message.c \
	$(CORE)/session/halyard_sess_fields.c $(CORE)/session/halyard_sess_request.c \


# ECDH delegates elliptic-curve arithmetic to mbedtls - rc_ecdh.h argues at length that a hand-written
# constant-time bigint is the last thing this project should write twice. The same script the Vita port
# and the host suite use cross-builds it here, unmodified, with CROSS set:
#
#     PREFIX=third-party/mbedtls CROSS=powerpc64-ps3-elf- sh ../common/tools/build-mbedtls.sh
MBEDTLS_PREFIX := third-party/mbedtls
MBEDTLS_LIB    := $(MBEDTLS_PREFIX)/lib/libmbedcrypto.a
ECDH_CFLAGS    := -DRC_CRYPTO_MBEDTLS -isystem $(MBEDTLS_PREFIX)/include \
                  -DMBEDTLS_CONFIG_FILE='<ripcord-mbedtls-config.h>'

# APPENDED HERE, not written into CFLAGS above, and the order is the whole point: CFLAGS is a `:=`
# assignment several hundred lines earlier, so a reference to ECDH_CFLAGS up there expands to nothing at
# all - silently, since an empty expansion is not an error. The application links rc_ecdh.c now, so it
# needs these, and the only safe place to say so is after ECDH_CFLAGS exists.
#
# CFLAGS_SDK_SOCKETS derives from CFLAGS and is defined below this line, so it inherits them too.
CFLAGS += $(ECDH_CFLAGS)

CORE_TEST_OBJS := $(BUILD)/ct_halyard_v1_constants.g.o \
                  $(addprefix $(BUILD)/ct_,$(notdir $(CORE_TEST_SUPPORT:.c=.o))) \
                  $(addprefix $(BUILD)/ct_,$(addsuffix _test.o,$(CORE_TEST_RUNNERS))) \
                  $(CORE_TEST_EXTRA_OBJS) \
                  $(BUILD)/rc_core_tests.o

# The runners are -Wno-conversion because they are the CORE's tests built for a target they were not
# written for, and this port's flag set is stricter than the one ports/common/tests uses. Relaxing it
# for the test sources only is honest: what is under examination is whether the CORE is byte-order
# clean on this hardware, not whether its test harness satisfies a warning set from another Makefile.
# The per-directory includes mirror ports/common/tests' own INCLUDES: the core's sources include each
# other by bare filename rather than by path, so the directories have to be on the search path.
CORE_TEST_CFLAGS := $(ARCH) -std=gnu99 -O2 -g -Wall -I$(CORE) -I$(CORE_TEST_DIR) \
                    -I$(CORE)/crypto -I$(CORE)/halyard -I$(CORE)/platform \
                    -Isource/coretest -I$(PSL1GHT)/ppu/include $(ECDH_CFLAGS)

# ---------------------------------------------------------------------------------------------------
# The SPE program. Step 6 of README.md's order of work.
#
# spu_rules' MACHDEP for the SPU is -mdual-nops -fmodulo-sched -ffunction-sections -fdata-sections, and
# it is taken from there rather than described from the hardware - the same rule the PPU's ARCH follows
# and for the same reason. -lsputhread is what provides spu_thread_exit.
#
# Warnings are the port's usual set MINUS -Wconversion and -Wsign-conversion. That is not a double
# standard: spu_mfcio.h's intrinsics mix widths by design - a DMA size is a u32, an effective address a
# u64, and a tag a u32 packed into a channel write - and the conversions are the SDK's, in inline code,
# exactly as on the PPU side where main.c has to pragma them away. The PPU half keeps the strict set.
SPU_ARCH   := -mdual-nops -fmodulo-sched -ffunction-sections -fdata-sections
SPU_CFLAGS := $(SPU_ARCH) -std=gnu99 -O2 -Wall -Wextra -Werror -Wshadow \
              -Ispu -I$(PSL1GHT)/spu/include
SPU_LDFLAGS := $(SPU_ARCH) -L$(PSL1GHT)/spu/lib
SPU_LIBS    := -lsputhread

SPU_SOURCES := spu/rc_spu_probe.c
SPU_NAME    := rc_spu_probe

# THE SECOND SPE IMAGE. The probe measures dispatch and DMA; this one does the colour conversion, and
# they are separate programs rather than one with a mode switch because they have nothing in common but
# the architecture - the probe is a one-shot that exits, this one waits on a mailbox forever.
SPU2_SOURCES := spu/rc_spu_yuv.c
SPU2_NAME    := rc_spu_yuv

.PHONY: all clean distclean test pkg run

all: $(APP).self

# SPE ELF -> raw blob -> assembled object, plus the header bin2s's naming convention implies.
# bin2s derives its symbol names from the FILENAME (rc_spu_probe.bin -> rc_spu_probe_bin), which is why
# source/spu/rc_spu.c includes a header it did not write and cannot rename.
$(BUILD)/$(SPU_NAME).elf: $(SPU_SOURCES)
	@mkdir -p $(@D)
	@test -x "$(SPU_CC)" || { echo "ERROR: spu-gcc not found at $(SPU_CC) - the SPU half of the toolchain is missing"; exit 1; }
	$(SPU_CC) $(SPU_CFLAGS) $(SPU_LDFLAGS) -o $@ $(SPU_SOURCES) $(SPU_LIBS)

# The heartbeat's local-store address, lifted out of the linked SPU image. The PPE reads that address
# with sysSpuThreadReadLocalStorage when a job does not complete; see spu/rc_spu_phase.h. Extracting it
# rather than fixing it with a linker script keeps the SPU source free of layout constraints - the
# address is whatever the linker chose, and this just reports it.
$(BUILD)/rc_spu_symbols.h: $(BUILD)/$(SPU_NAME).elf
	@addr=$$($(SPU_PREFIX)nm $< | awk '$$3 == "g_rc_spu_heartbeat" { print $$1 }'); \
	 args=$$($(SPU_PREFIX)nm $< | awk '$$3 == "g_rc_spu_args" { print $$1 }'); \
	 if [ -z "$$addr" ] || [ -z "$$args" ]; then echo "ERROR: probe symbols not found in $<"; exit 1; fi; \
	 entry=$$($(SPU_PREFIX)readelf -h $< | awk '/Entry point/ { print $$4 }'); \
	 printf '/* Generated by the Makefile from the linked SPU image. Not checked in. */\n' > $@; \
	 printf '#define RC_SPU_HEARTBEAT_ADDR 0x%s\n#define RC_SPU_ARGS_ADDR 0x%s\n#define RC_SPU_IMAGE_ENTRY %s\n' \
	        "$$addr" "$$args" "$$entry" >> $@; \
	 echo "  spu heartbeat 0x$$addr, args 0x$$args, entry $$entry"

$(BUILD)/$(SPU_NAME).bin: $(BUILD)/$(SPU_NAME).elf
	cp $< $@

$(BUILD)/$(SPU2_NAME).elf: $(SPU2_SOURCES)
	@mkdir -p $(@D)
	@test -x "$(SPU_CC)" || { echo "ERROR: spu-gcc not found at $(SPU_CC)"; exit 1; }
	$(SPU_CC) $(SPU_CFLAGS) $(SPU_LDFLAGS) -o $@ $(SPU2_SOURCES) $(SPU_LIBS)

$(BUILD)/$(SPU2_NAME).bin: $(BUILD)/$(SPU2_NAME).elf
	cp $< $@

$(BUILD)/$(SPU2_NAME)_bin.o: $(BUILD)/$(SPU2_NAME).bin
	$(BIN2S) -a 64 $< | $(AS) -o $@
	@printf 'extern const unsigned char %s_bin_end[];\nextern const unsigned char %s_bin[];\nextern const unsigned int %s_bin_size;\n' \
	        $(SPU2_NAME) $(SPU2_NAME) $(SPU2_NAME) > $(BUILD)/$(SPU2_NAME)_bin.h

$(BUILD)/$(SPU_NAME)_bin.o: $(BUILD)/$(SPU_NAME).bin
	$(BIN2S) -a 64 $< | $(AS) -o $@
	@printf 'extern const unsigned char %s_bin_end[];\nextern const unsigned char %s_bin[];\nextern const unsigned int %s_bin_size;\n' \
	        $(SPU_NAME) $(SPU_NAME) $(SPU_NAME) > $(BUILD)/$(SPU_NAME)_bin.h

# Regenerated, and the counter bumped, whenever a source file changes - so an unchanged tree rebuilt
# twice keeps its number rather than inflating it.
$(BUILD)/rc_build_id.h: $(SOURCES)
	@mkdir -p $(@D)
	@n=$$(( $$(cat $(BUILDNO_FILE) 2>/dev/null || echo 0) + 1 )); \
	 echo $$n > $(BUILDNO_FILE); \
	 printf '/* Generated by the Makefile. Not checked in. */\n#define RC_PS3_BUILD_ID "b%s"\n' "$$n" > $@; \
	 echo "  build id: b$$n"

# THE INTEROP CONSTANTS, generated rather than committed as C. CLAUDE.md's bounded exception 1 keeps
# ~4 KB of generic, every-console interop constants in one committed JSON file, and every port turns
# that into a translation unit at build time rather than carrying a second copy.
# halyard_control_crypto.c needs them; the control-session and discovery suites link it.
#
# DEFINED ABOVE THE RULE THAT USES THEM, and that is not a stylistic preference. Make expands a
# prerequisite list when it reads the file, so a variable defined further down yields an EMPTY
# prerequisite - the rule then runs with $< unset and gcc reports "no input files", naming neither the
# variable nor the rule. ports/common/tests/Makefile documents the same trap for its own MBEDTLS_PREFIX.
#
# It bit here because an earlier edit deleted these two lines while rewriting the block below them, and
# nothing failed: build/halyard_v1_constants.g.c already existed, so every incremental build stayed
# green and only `make clean` exposed it.
BUNDLE    := ../../src/Ripcord.Protocol.Halyard/Data/halyard-v1-constants.json
GENERATED := $(BUILD)/halyard_v1_constants.g.c
PYTHON    ?= python3

$(GENERATED): $(BUNDLE) $(CORE)/tools/gen_constants.py
	@mkdir -p $(@D)
	$(PYTHON) $(CORE)/tools/gen_constants.py $(BUNDLE) $@

$(BUILD)/ct_halyard_v1_constants.g.o: $(GENERATED)
	@mkdir -p $(@D)
	$(CC) $(CORE_TEST_CFLAGS) $(DEPFLAGS) -c -o $@ $<

$(BUILD)/ct_%.o: $(CORE)/crypto/%.c
	@mkdir -p $(@D)
	$(CC) $(CORE_TEST_CFLAGS) $(DEPFLAGS) -c -o $@ $<
$(BUILD)/ct_%.o: $(CORE)/discovery/%.c
	@mkdir -p $(@D)
	$(CC) $(CORE_TEST_CFLAGS) $(DEPFLAGS) -c -o $@ $<
$(BUILD)/ct_%.o: $(CORE)/halyard/%.c
	@mkdir -p $(@D)
	$(CC) $(CORE_TEST_CFLAGS) $(DEPFLAGS) -c -o $@ $<
$(BUILD)/ct_%.o: $(CORE)/input/%.c
	@mkdir -p $(@D)
	$(CC) $(CORE_TEST_CFLAGS) $(DEPFLAGS) -c -o $@ $<
$(BUILD)/ct_%.o: $(CORE)/session/%.c
	@mkdir -p $(@D)
	$(CC) $(CORE_TEST_CFLAGS) $(DEPFLAGS) -c -o $@ $<
$(BUILD)/ct_%.o: $(CORE)/stream/%.c
	@mkdir -p $(@D)
	$(CC) $(CORE_TEST_CFLAGS) $(DEPFLAGS) -c -o $@ $<
$(BUILD)/ct_%.o: $(CORE)/takion/%.c
	@mkdir -p $(@D)
	$(CC) $(CORE_TEST_CFLAGS) $(DEPFLAGS) -c -o $@ $<
$(BUILD)/ct_%.o: $(CORE)/util/%.c
	@mkdir -p $(@D)
	$(CC) $(CORE_TEST_CFLAGS) $(DEPFLAGS) -c -o $@ $<

# The runners themselves, each with its main() renamed so they can share a binary.
$(BUILD)/ct_%_test.o: $(CORE_TEST_DIR)/%_test.c
	@mkdir -p $(@D)
	$(CC) $(CORE_TEST_CFLAGS) $(DEPFLAGS) -Dmain=$*_test_main -c -o $@ $<

# vector_runner does not follow the *_test.c naming, so it gets its own rule rather than a special case
# in the pattern above.
$(BUILD)/ct_vector_runner.o: $(CORE_TEST_DIR)/vector_runner.c
	@mkdir -p $(@D)
	$(CC) $(CORE_TEST_CFLAGS) $(DEPFLAGS) -Dmain=vector_runner_main -c -o $@ $<

$(BUILD)/rc_core_tests.o: source/coretest/rc_core_tests.c
	@mkdir -p $(@D)
	$(CC) $(CORE_TEST_CFLAGS) $(DEPFLAGS) -c -o $@ $<

$(BUILD)/rc_decode_probe.o: $(CXXSOURCES) $(OPENH264_LIB)
	@mkdir -p $(@D)
	$(CXX) -c $(ARCH) -std=gnu++11 -O2 -g -Wall -Isource/media -I$(CORE) $(OPENH264_INC) \
	       -I$(PSL1GHT)/ppu/include -o $@ $(CXXSOURCES)

# Fetched and hash-verified, not vendored. See tools/build-openh264.sh.
$(OPENH264_LIB):
	@echo "ERROR: $(OPENH264_LIB) missing - run tools/build-openh264.sh first"; exit 1

# PER-FILE OBJECTS, because the link is now a C++ link over C objects and the two need different flags.
# The C sources must be compiled by $(CC) with -std=gnu99 - g++ rejects that option outright - while the
# LINK has to be $(CXX), so libstdc++ and its static initialisers come in. Passing $(CFLAGS) to a g++
# link, which an earlier revision did, fails on the standard option alone.
#
# vpath rather than per-directory rules: the basenames across these five directories are unique, and one
# pattern rule is easier to be right about than five.
vpath %.c source/video $(CORE)/util $(CORE)/discovery $(CORE)/session $(CORE)/net $(CORE)/takion \
          $(CORE)/crypto $(CORE)/stream \
          source/platform source/spu source/net source/media source/discovery source/connect source/app

COBJS := $(addprefix $(BUILD)/,$(notdir $(SOURCES:.c=.o)))

# The generated headers are order-only prerequisites: rc_build_id.h, rc_spu_symbols.h and the
# rc_spu_probe_bin.h that bin2s writes alongside its object. Order-only (after the |) because their
# CONTENT does not decide whether a C file needs recompiling - the build id changes on every source
# change by design, and making every object depend on its content would rebuild the world each time.
# TWO CORE FILES OPEN SOCKETS, and PSL1GHT's headers make that expensive under this port's flag set.
# halyard_control_session.c and rc_tcp.c include <net/socket.h>, which pulls in ppu-lv2.h, whose inline
# syscall wrappers convert int to u64 all over - exactly what source/app/main.c has to pragma away for
# its own includes.
#
# The pragma trick is not available here: these are ports/common files, shared by every port, and a
# PSL1GHT-specific pragma has no business in a platform-neutral layer. So the relaxation lives in the
# Makefile of the port whose SDK causes it, scoped to the two files that need it, and nothing about the
# core's own code is excused - only the SDK headers it is compiled against.
# APPENDED, not filtered. Removing -Wsign-conversion from the list is not enough: -Wconversion implies
# it for C, so it comes straight back. A trailing -Wno- wins because later options override earlier ones.
# And -include sys/time.h, because PSL1GHT's <sys/socket.h> declares sysNetSelect taking a
# `struct timeval *` without having pulled that struct in - so the first thing to include it gets
# "declared inside parameter list will not be visible outside of this definition", which under -Werror
# is fatal and reads like the core's mistake rather than a header ordering one. Same shape as the
# -include stdio.h that tools/build-openh264.sh needs, and the same reason: fix it from outside rather
# than edit code that is not ours.
CFLAGS_SDK_SOCKETS := $(CFLAGS) -Wno-sign-conversion -include sys/time.h

# EVERY OBJECT THAT REACHES A SOCKET HEADER, as a list with one target-specific override rather than one
# hand-written rule apiece. It started as two explicit rules; adding Takion would have made it six, each a
# copy of the last, and the copy that eventually differs is the bug. A target-specific CFLAGS assignment
# overrides the value the pattern rule below expands, so the list is the only thing to maintain.
#
# It is a LIST and not a wildcard on purpose: the relaxation drops a real warning class, so a file joins
# it deliberately or not at all. rc_stack_ps3.o is here for a different header than the others - it calls
# a thread syscall rather than a socket one - but the cause is identical: PSL1GHT's inline lv2 wrappers
# convert int to u64, and that is the SDK's code, not this port's. Note rc_connect.o is here now - it always included <net/socket.h>, but it
# included <sys/time.h> first and so happened not to need this, which is a fact about the order of its
# own include block rather than anything durable.
SDK_SOCKET_OBJS := \
	$(BUILD)/halyard_control_session.o \
	$(BUILD)/rc_tcp.o \
	$(BUILD)/rc_udp.o \
	$(BUILD)/rc_connect.o \
	$(BUILD)/takion_reliable_channel.o \
	$(BUILD)/rc_stack_ps3.o \
	$(BUILD)/rc_video_ps3.o \
	$(BUILD)/rc_spu_yuv.o

$(SDK_SOCKET_OBJS): CFLAGS := $(CFLAGS_SDK_SOCKETS)

$(BUILD)/%.o: %.c | $(BUILD)/rc_build_id.h $(BUILD)/rc_spu_symbols.h $(BUILD)/$(SPU_NAME)_bin.o $(BUILD)/$(SPU2_NAME)_bin.o
	@mkdir -p $(@D)
	$(CC) $(CFLAGS) $(DEPFLAGS) -c -o $@ $<

$(BUILD)/$(APP).elf: $(COBJS) $(BUILD)/$(SPU_NAME)_bin.o $(BUILD)/$(SPU2_NAME)_bin.o $(BUILD)/rc_decode_probe.o $(CORE_TEST_OBJS)
	@mkdir -p $(@D)
	$(CXX) $(LDFLAGS) -o $@ $(COBJS) $(BUILD)/$(SPU_NAME)_bin.o $(BUILD)/$(SPU2_NAME)_bin.o \
	       $(BUILD)/rc_decode_probe.o $(CORE_TEST_OBJS) $(OPENH264_LIB) $(MBEDTLS_LIB) $(LIBS)

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

# elf -> self, following $(PSL1GHT)/ppu_rules' %.self rule: make_self produces the primary artifact and
# fself a secondary .fake.self beside it.
#
# AN EARLIER REVISION USED fself FOR THE PRIMARY, on the reasoning that an unsigned SELF is what a
# homebrew-enabled console runs and a signed one was neither available nor wanted. That sounds right and
# it is not what the SDK does: ppu_rules labels the make_self output "CEX self", makes it the .self, and
# every run: target in PSL1GHT - ten of them - hands ps3load that file. A fake SELF transferred to the
# listener perfectly well and then did not launch.
#
# Third time in this file that a plausible-sounding assertion about the SDK has been wrong where the
# SDK's own rule was sitting there to be read. The rule is the specification; reasoning about what it
# must have meant is not.
$(APP).self: $(BUILD)/$(APP).ready.elf
	$(SELF) $< $@
	$(FSELF) $< $(APP).fake.self

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

# THE FAST LOOP. Sends the ELF straight to a listener on the console over TCP 4299 and relays the
# program's stdout back, so an iteration is one command with no install step and no XMB round trip.
#
# It needs a ps3load server running on the PS3 - either multiMAN, or PSL1GHT's own
# samples/network/ps3load built and installed. Step 6 took five install-and-run cycles to find a single
# wrong argument, and any one of them would have been diagnosed in seconds against live output; that is
# the whole argument for this target existing.
#
# SENDS THE .self, NOT THE ELF. The first version of this rule sent build/$(APP).ready.elf on the
# assertion that "ps3load wants an ELF rather than a SELF". That is backwards. Every run: target in
# PSL1GHT - ten of them - is `ps3load $(OUTPUT).self`, and the listener writes whatever it receives to
# /dev_hdd0/tmp/ps3load.self and launches that, so a raw ELF arrives intact and then does not start.
#
# The failure is quiet from this end: ps3load reports the transfer and exits "done." either way. It was
# caught by the build id - the console's log still said b11 while the tree was at b13 - which is exactly
# the stale-binary case that stamp was added for, arriving one commit after it was added.
#
# THE LISTENER IS ONE-SHOT. It ends with sysProcessExitSpawn2, which replaces the listener process with
# the program it just received - so it has to be launched again on the console before each run. That is
# still far cheaper than build, FTP, package manager, install, navigate, launch; it is just not zero
# touch, and a "connection refused" here usually means the last run consumed it rather than anything
# being wrong.
#
#     make run PS3LOAD=tcp:192.168.1.42
run: $(APP).self
	@test -n "$(PS3LOAD)" || { echo "ERROR: set PS3LOAD, e.g. make run PS3LOAD=tcp:<console-ip>"; exit 1; }
	PS3LOAD=$(PS3LOAD) $(PS3DEV)/bin/ps3load $<

# Convenience passthrough. These are the tests that actually verify anything today - the H.264 front end
# from steps 2 and 3, the CSPRNG seam's host-testable half, and the core's known-answer vectors. All run
# on the host with no console.
test:
	$(MAKE) -C tests
	$(MAKE) -C $(CORE)/tests

# Note what clean does NOT remove: $(BUILDNO_FILE), for the reason given on BUILD_NO, and
# $(OPENH264_DIR), for the reason given there.
clean:
	rm -f $(APP).self $(APP).fake.self $(APP).pkg $(APP).gnpdrm.pkg
	rm -rf $(BUILD)

# Everything clean removes, plus the fetched third-party tree. Separate because re-fetching and
# rebuilding openh264 is minutes, and wanting fresh objects is not wanting that.
distclean: clean
	rm -rf $(OPENH264_DIR)

# The dependency files the DEPFLAGS above produce. Wildcard rather than a derived list: an object that is
# no longer built leaves a stale .d behind, and naming them from the object list would demand it exist.
# Not included for `clean`, which would otherwise rebuild them only to delete them.
ifeq (,$(filter clean distclean,$(MAKECMDGOALS)))
-include $(wildcard $(BUILD)/*.d)
endif
