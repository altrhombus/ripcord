# ripcord-ps3 - the PlayStation 3 build.
#
# Produces ripcord-ps3.self, runnable from a homebrew-enabled console. Requires PSL1GHT and the ps3dev
# PPU toolchain: https://github.com/ps3dev/PSL1GHT - set $PS3DEV and $PSL1GHT.
#
#     make            build ripcord-ps3.self
#     make clean
#     make test       run the HOST-side tests (no PS3 involved) - tests/Makefile, and libripcord/tests
#
# Written against ps3dev nightly-2026-07-26 (GCC 7.2.0, PSL1GHT f649a08) on Ubuntu 26.04. Measured on
# hardware 2026-09-11: the PPE time base at 79,800,986 Hz against an expected 79,800,000.
#
# Before editing the packaging rules below, read SETUP.md section 6: a missing sprxlinker step or a stack
# size in the wrong units gives a console that launches the program and returns to the XMB in silence.
#
# Not in Ripcord.slnx and not buildable by dotnet: this is a Makefile driving a cross-compiler for a
# different CPU and a different OS.

ifeq ($(strip $(PS3DEV)),)
$(error PS3DEV is not set. Install the ps3dev toolchain and export PS3DEV - see the URL above)
endif
ifeq ($(strip $(PSL1GHT)),)
$(error PSL1GHT is not set. Install PSL1GHT and export PSL1GHT - see the URL above)
endif

# make takes the FIRST EXPLICIT TARGET as its default goal. Pinned so that the order of the rules below
# cannot change it: with a generated-file rule first nothing fails, `make` builds one file and stops.
.DEFAULT_GOAL := all

PREFIX := $(PS3DEV)/ppu/bin/powerpc64-ps3-elf-
CC     := $(PREFIX)gcc
CXX    := $(PREFIX)g++
AS     := $(PREFIX)as
STRIP  := $(PREFIX)strip

# THE SECOND COMPILER. The SPEs are a different architecture with a different instruction set and their
# own toolchain - this is the thing BRINGUP.md means when it says the PS3 costs strictly more toolchain
# surface than either port before it. spu/ is built by SPU_CC into an ELF, that ELF becomes a binary
# blob, and the blob is assembled into the PPU binary by the PPU assembler. Three tools, one program.
SPU_PREFIX := $(PS3DEV)/spu/bin/spu-
SPU_CC     := $(SPU_PREFIX)gcc
BIN2S      := $(PS3DEV)/bin/bin2s

# Packaging tools, all from $(PS3DEV)/bin. A plain `make` needs sprxlinker, make_self and fself; `make
# pkg` uses the rest. sprxlinker is not optional - see the .ready.elf rule below for what leaving it out
# does.
SPRX           := $(PS3DEV)/bin/sprxlinker
SELF           := $(PS3DEV)/bin/make_self
FSELF          := $(PS3DEV)/bin/fself
SELF_NPDRM     := $(PS3DEV)/bin/make_self_npdrm
SFO            := $(PS3DEV)/bin/sfo
PKG            := $(PS3DEV)/bin/pkg
PKG_FINALIZE   := $(PS3DEV)/bin/package_finalize
SFOXML         := $(PS3DEV)/bin/sfo.xml

# The icon the XMB shows, drawn from brand/ripcord-ground.svg and brand/ripcord-mark-ondark.svg by
# tools/make-icon.py (python3 and nothing else; its header says why not brand/generate-assets.ps1).
# $(PS3DEV)/bin/ICON0.PNG is PSL1GHT's placeholder and says PSL1GHT.
# Deferred (=) rather than immediate: $(BUILD) is defined further down, and := here expands it to
# nothing, which makes the target /ICON0.PNG.
ICON0           = $(BUILD)/ICON0.PNG

# The identity the console files this under. APPID is what creates /dev_hdd0/game/RIPC00003/ on install;
# CONTENTID follows the UP0001-<appid>_00-<16 chars> shape that $(PSL1GHT)/ppu_rules uses for its own
# default, and make_self_npdrm warns that the OUTPUT FILENAME matters to NPDRM - hence EBOOT.BIN exactly.
#
# The build number. A stale install and a broken program look identical, so every build gets a number,
# and it appears in three places that have to agree: the version the XMB shows, the beacon file, and the
# first line of both logs. If they do not match what `make` last printed, the console is running
# something else.
#
# The counter lives in .buildno at the port root, NOT under build/, so that `make clean` does not reset
# it and hand out numbers that have already been used. It is gitignored: per-machine state.
BUILDNO_FILE := .buildno
BUILD_NO      = $(shell cat $(BUILDNO_FILE) 2>/dev/null || echo 0)

# Where this build came from, appended to the build id. Empty for a developer build; CI passes
# BUILD_ORIGIN=ci.
#
# The two counters are independent: $(BUILDNO_FILE) counts one developer's iterations, and CI stamps
# github.run_number, which climbs far faster. Both print the same "bNNN" shape, and the bNNN numbers
# this repository's docs and journal cite are local builds. The suffix keeps "b35ci" and "b35" distinct;
# the id is a string and unbounded, so no partition of the NN.NN field is needed.
#
# The suffix does not reach the XMB: it shows APP_VER, which is digits and a dot and cannot carry a
# letter, so a CI b35 and a local b35 both read 00.35 there. The log's first line is the authoritative
# answer.
# A release build does not touch this counter at all - see RELEASE_TAG below. It is identified by its
# tag.
BUILD_ORIGIN ?=

# Recursively expanded (=, not :=) on purpose: the number is read when the packaging recipe RUNS, which
# is after the compile step has bumped it. A := here would bake in the previous build's number.
#
# The name on the XMB is the name of the program, and the build number is its version, which the console
# shows in the same panel. APP_VER is an NN.NN string: take the dot out of the version and you have the
# build, so b432 is 04.32. It is monotonic, and it holds to b9999.
TITLE     = Ripcord

# A release is versioned by its tag, not by either counter. Pass RELEASE_TAG=ps3-v1.0 and the package
# carries 01.00; leave it unset and APP_VER comes from the build counter.
#
# Both counters - a developer's .buildno and CI's github.run_number - say which iteration a machine
# built, not which version a person is installing. APP_VER is the console's field for the second.
#
# It also takes released packages out of the NN.NN ceiling. A counter climbs to 9999 and then produces a
# package that installs, refuses to launch, and blocks every later install until the title is deleted by
# hand - established on hardware.
#
# The PS3 port versions independently of the dotnet client, which is why the tag has its own namespace.
# ps3-v1.0 says nothing about ROADMAP.md's 1.0, whose criteria are written about the app.
#
# Two digits each: ps3-v1.0 -> 01.00, ps3-v1.1 -> 01.01, ps3-v2.0 -> 02.00. A tag that does not parse is
# an error rather than a silent 00.00.
RELEASE_TAG ?=
ifneq ($(strip $(RELEASE_TAG)),)
RELEASE_MAJOR := $(shell printf '%s' '$(RELEASE_TAG)' | sed -n 's/^ps3-v\([0-9][0-9]*\)\.\([0-9][0-9]*\)$$/\1/p')
RELEASE_MINOR := $(shell printf '%s' '$(RELEASE_TAG)' | sed -n 's/^ps3-v\([0-9][0-9]*\)\.\([0-9][0-9]*\)$$/\2/p')
ifeq ($(strip $(RELEASE_MAJOR)),)
$(error RELEASE_TAG='$(RELEASE_TAG)' is not of the form ps3-v<major>.<minor>, e.g. ps3-v1.0)
endif
APP_VER    = $(shell printf '%02d.%02d' $(RELEASE_MAJOR) $(RELEASE_MINOR))
RELEASE_ID := v$(RELEASE_MAJOR).$(RELEASE_MINOR)
else
APP_VER    = $(shell printf '%02d.%02d' $$(( $(BUILD_NO) / 100 )) $$(( $(BUILD_NO) % 100 )))
RELEASE_ID :=
endif
APPID     := RIPC00003
CONTENTID := UP0001-$(APPID)_00-0000000000000000

# The portable protocol core, shared with every other Ripcord port. See libripcord/platform/
# rc_platform.h for the seam this build implements in source/platform/rc_platform_ps3.c.
CORE  := ../../libripcord
BUILD := build
APP   := ripcord-ps3
NOTICES := $(BUILD)/THIRD-PARTY-NOTICES.txt

# Architecture flags. Get them from the SDK, not from the hardware, and from what the SDK's projects
# actually compile with, not just the variable it defines. (The 3DS has to spell out -march=armv6k
# -mtune=mpcore -mfloat-abi=hard -mtp=soft to match libctru; the Vita needs none, and passing
# -mfloat-abi=softfp there failed the link on every translation unit.)
#
# Twenty-five of PSL1GHT's sample Makefiles, and bucanero's apollo-ps3, all write:
#
#     CFLAGS = -O2 -Wall -mcpu=cell $(MACHDEP) $(INCLUDE)
#
# MACHDEP and -mcpu=cell together. MACHDEP is the float ABI and scheduling flags every PSL1GHT library is
# built with, which is the part that has to agree; -mcpu=cell is tuning, which the SDK leaves to the
# project. The -f*-sections half of MACHDEP is omitted here only because CFLAGS below already carries it.
ARCH := -mcpu=cell -mhard-float -fmodulo-sched

# The log directory of last resort: /dev_hdd0/ rather than this program's own install directory, on
# purpose.
#
# main.c tries a list of directories (RC_PS3_LOG_DIRS) and uses the first that accepts a file. Only when
# none does, it calls rc_log_open(NULL, ...) - it is `int main(void)`, so there is no argv[0] to derive
# a directory from - and rc_program_dir falls back to this string. /dev_hdd0/game/RIPC00003/USRDIR/
# exists only for a `make pkg` install; /dev_hdd0/ exists on every PS3 however the program was started.
LOG_DIR := /dev_hdd0/

# THE NETWORK LOG. Every line the program logs is also sent as a UDP datagram here, which is
# the only channel that needs no write permission on the console - see source/net/rc_netlog.h. Override
# on the command line: make RC_NETLOG_HOST=192.168.1.10
#
# Listen for it with:  nc -u -l -p 9000      (or: socat -u UDP-RECV:9000 -)
RC_NETLOG_HOST ?= 192.168.1.10
RC_NETLOG_PORT ?= 9000

# -Wframe-larger-than is inherited from both prior ports, where the hardware had already found the crash.
# The deepest live path either port measured was the audio loss-concealment chain at ~12.4 KB across
# five frames, every individual frame under the limit - cumulative call depth rather than one oversized
# local. That chain is libripcord code and Opus, so it exists here too. See "The main thread's stack" in
# BRINGUP.md for what this console's stack measured.

# -std=gnu99, NOT -std=c99. PSL1GHT's syscall wrappers are built on `register u64 p1 asm("3")` in
# ppu-lv2.h, and in strict ISO mode `asm` is not a keyword - so the first thing to include
# <sys/systime.h> under -std=c99 gets "expected '=', ',', ';', 'asm' or '__attribute__' before 'asm'"
# pointing into the SDK's own header. Anything that includes a PSL1GHT header needs GNU mode.
# tests/Makefile stays on -std=c99 on purpose: nothing it builds touches the SDK.
#
# Header dependencies. Without them, editing a header rebuilds nothing that includes it, so objects
# compiled before and after a struct gains a field disagree about its layout.
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
          -Isource/discovery -Isource/connect -Isource/input -Isource/ui -I$(CORE)/discovery -I$(CORE)/session -I$(CORE)/input -I$(CORE)/ui -I$(CORE)/net \
          -I$(CORE)/halyard -I$(CORE)/crypto -I$(CORE)/net -I$(CORE)/takion -I$(CORE)/stream -I$(CORE)/util -I$(CORE)/platform -Ispu \
          -I$(PSL1GHT)/ppu/include

# ps3dev's portlibs carry a standalone FreeType. It is used to read the console's OWN typefaces out of
# /dev_flash/data/font at runtime - see source/video/rc_sysfont.c for why that is the route rather than
# cellFont, and note that no font data is redistributed: the files stay on the console.
PORTLIBS := $(PS3DEV)/portlibs/ppu

LDFLAGS := $(ARCH) -Wl,--gc-sections -L$(PSL1GHT)/ppu/lib -L$(PORTLIBS)/lib

# What the program links from PSL1GHT and the portlibs: FreeType and zlib for the console's typefaces,
# the pad, the RSX, sysutil, the console's video decoder, audio, the network, pthreads for openh264, and
# lv2.
# -lnet: netInitialize() and the BSD socket names both come from it.
# -lsysmodule is not optional beside -lnet: netInitialize() calls sysModuleLoad, and without it the link
# fails on that symbol. PSL1GHT's network samples all pair the two: `LIBS := -lnetctl -lnet -lsysmodule`.
LIBS := -lfreetype -lz -lio -lrsx -lgcm_sys -lsysutil -lvdec -laudio -lnetctl -lnet -lsysmodule -lpthread -lrt -llv2

# Everything the application compiles as C: this port's own sources and the libripcord sources they
# call. rc_random_ps3.c is the only implementation of the CSPRNG seam here, so without it anything asking
# for key material fails to link rather than gets a stub. The file itself records what is confirmed and
# what is still [X].
SOURCES := \
	$(CORE)/util/rc_log.c \
	$(CORE)/util/rc_program_dir.c \
	source/platform/rc_platform_ps3.c \
	source/platform/rc_thermal.c \
	source/input/rc_pad_ps3.c \
	source/input/rc_chord.c \
	source/input/rc_osk_ps3.c \
	$(CORE)/ui/rc_session_state.c \
	$(CORE)/ui/rc_menu.c \
	source/ui/rc_shell.c \
	source/ui/rc_wave.c \
	source/platform/rc_random_ps3.c \
	source/platform/rc_stack_ps3.c \
	source/video/rc_video_ps3.c \
	source/video/rc_overlay.c \
	source/video/rc_status_screen.c \
	source/video/rc_sysfont.c \
	source/spu/rc_spu.c \
	source/spu/rc_spu_yuv.c \
	source/net/rc_netlog.c \
	source/media/rc_h264_annexb.c \
	source/media/rc_h264_bits.c \
	source/media/rc_h264_params.c \
	source/media/rc_vdec_probe.c \
	source/media/rc_decode_vdec.c \
	source/media/rc_decode_live.c \
	source/media/rc_audio_ps3.c \
	source/discovery/rc_discover.c \
	source/connect/rc_connect.c \
	source/connect/rc_pair_ps3.c \
	source/connect/rc_account_ps3.c \
	$(CORE)/discovery/halyard_discovery.c \
	$(CORE)/discovery/halyard_wake.c \
	$(CORE)/session/halyard_pairing_file.c \
	$(CORE)/session/halyard_control_probe.c \
	$(CORE)/session/halyard_regist_flow.c \
	$(CORE)/session/halyard_regist_message.c \
	$(CORE)/session/halyard_account_id.c \
	$(CORE)/halyard/halyard_registration.c \
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

# OPENH264, and the one C++ translation unit in this port. openh264's ISVCDecoder is an abstract class
# rather than a C handle, so the file that drives it has to be C++; everything it exposes is extern "C"
# and the rest of the port stays C99. tools/build-openh264.sh produces the library - run it once.
# Not under $(BUILD): rebuilding costs a 57 MB fetch and 35 compiles, so `make clean` leaves it alone. It
# lives beside them, gitignored, and `make distclean` is what removes it deliberately.
# OPUS, the audio decoder, on the same terms: fetched and built by tools/build-opus.sh into a gitignored
# directory, never vendored. BSD-3-Clause, which keeps the permissive-only line DECODE.md section 1 sets
# out. ps3dev's portlibs carry libogg, FLAC and two SDL mixers and no Opus, so it has to be built.
OPUS_DIR ?= third-party/opus
OPUS_LIB := $(OPUS_DIR)/lib/libopus.a
OPUS_INC := -I$(OPUS_DIR)/include/opus

OPENH264_DIR ?= third-party/openh264
OPENH264_LIB := $(OPENH264_DIR)/libopenh264dec.a
OPENH264_SRC := $(OPENH264_DIR)/openh264-2.6.0
OPENH264_INC := -I$(OPENH264_SRC)/codec/api/wels -I$(OPENH264_SRC)/codec/api

CXXSOURCES := source/media/rc_decode_probe.cpp

# ---------------------------------------------------------------------------------------------------
# libripcord's OWN TEST SUITES, cross-compiled and run on the console. See source/coretest/ for why.
#
# Eleven runners, plus the core sources they exercise. Each runner keeps its own main(), renamed at
# compile time so they can share one binary; that is the only modification, and it is a -D rather than
# an edit.
CORE_TEST_DIR := $(CORE)/tests
# The seven that carry their own expectations, and the four that check against .kat vectors emitted by
# the .NET side: three in the list, and vector_runner, which is named for its file rather than its
# subject - it is the control crypto.
CORE_TEST_RUNNERS := discovery session takion fec stream_header stream_demux input \
                     stream_crypto ecdh control_proto
CORE_TEST_EXTRA_OBJS := $(BUILD)/ct_vector_runner.o

# The core sources the runners need, following libripcord/tests' own source lists, minus every one that
# SOURCES already compiles. The application links those itself, and a source cannot be in both lists:
# each list compiles it into its own object, and two objects defining the same symbols do not link.
#
# The application's copy wins, and that direction matters. These objects are built with CORE_TEST_CFLAGS,
# which is deliberately relaxed; shipping the relaxed build inside the application would exempt real
# application code from the port's own flags. The test runners link against the strict objects instead.
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
#     PREFIX=third-party/mbedtls CROSS=powerpc64-ps3-elf- sh ../../libripcord/tools/build-mbedtls.sh
MBEDTLS_PREFIX := third-party/mbedtls
MBEDTLS_LIB    := $(MBEDTLS_PREFIX)/lib/libmbedcrypto.a
ECDH_CFLAGS    := -DRC_CRYPTO_MBEDTLS -isystem $(MBEDTLS_PREFIX)/include \
                  -DMBEDTLS_CONFIG_FILE='<ripcord-mbedtls-config.h>'

# Appended here, not written into CFLAGS above: CFLAGS is a `:=` assignment made before ECDH_CFLAGS
# exists, so a reference to it up there expands to nothing, silently. The application links rc_ecdh.c,
# so it needs these.
#
# CFLAGS_SDK_SOCKETS derives from CFLAGS and is defined below this line, so it inherits them too.
CFLAGS += $(ECDH_CFLAGS) $(OPUS_INC) -isystem $(PORTLIBS)/include -isystem $(PORTLIBS)/include/freetype2

CORE_TEST_OBJS := $(BUILD)/ct_halyard_v1_constants.g.o \
                  $(addprefix $(BUILD)/ct_,$(notdir $(CORE_TEST_SUPPORT:.c=.o))) \
                  $(addprefix $(BUILD)/ct_,$(addsuffix _test.o,$(CORE_TEST_RUNNERS))) \
                  $(CORE_TEST_EXTRA_OBJS) \
                  $(BUILD)/rc_core_tests.o

# The runners are built with -Wall alone because they are the CORE's tests, and this port's flag set is
# stricter than the one libripcord/tests uses. What is under examination is whether the CORE is
# byte-order clean on this hardware, not whether its test harness satisfies a warning set from another
# Makefile.
# The per-directory includes mirror libripcord/tests' own INCLUDES: the core's sources include each
# other by bare filename rather than by path, so the directories have to be on the search path.
CORE_TEST_CFLAGS := $(ARCH) -std=gnu99 -O2 -g -Wall -I$(CORE) -I$(CORE_TEST_DIR) \
                    -I$(CORE)/crypto -I$(CORE)/halyard -I$(CORE)/platform \
                    -Isource/coretest -I$(PSL1GHT)/ppu/include $(ECDH_CFLAGS)

# ---------------------------------------------------------------------------------------------------
# The SPE program. Step 6 of BRINGUP.md's order of work.
#
# SPU_ARCH is spu_rules' MACHDEP for the SPU, taken from the SDK rather than described from the hardware,
# the same rule the PPU's ARCH follows. -lsputhread is what provides spu_thread_exit.
#
# Warnings are the port's usual set MINUS -Wconversion and -Wsign-conversion: spu_mfcio.h's inline
# intrinsics mix widths by design - a DMA size is a u32, an effective address a u64, and a tag a u32
# packed into a channel write. The PPU half keeps the strict set.
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
# The SPU sources count too: without them a change to an SPE kernel produces a new binary under an OLD
# build id (b185 was uploaded labelled b184).
$(BUILD)/rc_build_id.h: $(SOURCES) $(SPU_SOURCES) $(SPU2_SOURCES) $(CXXSOURCES)
	@mkdir -p $(@D)
	@if [ -n '$(RELEASE_ID)' ]; then \
	   id='$(RELEASE_ID)'; ver='$(RELEASE_MAJOR).$(RELEASE_MINOR)'; \
	 else \
	   n=$$(( $$(cat $(BUILDNO_FILE) 2>/dev/null || echo 0) + 1 )); \
	   echo $$n > $(BUILDNO_FILE); \
	   id="b$${n}$(BUILD_ORIGIN)"; \
	   ver=$$(printf '%d.%02d' $$(( $$n / 100 )) $$(( $$n % 100 ))); \
	 fi; \
	 printf '/* Generated by the Makefile. Not checked in. */\n' > $@; \
	 printf '#define RC_PS3_BUILD_ID "%s"\n' "$$id" >> $@; \
	 printf '#define RC_PS3_VERSION "%s"\n' "$$ver" >> $@; \
	 echo "  build id: $$id"

# THE INTEROP CONSTANTS, generated rather than committed as C. NOTICE's interoperability-constants exception keeps
# ~4 KB of generic, every-console interop constants in one committed JSON file, and every port turns
# that into a translation unit at build time rather than carrying a second copy.
# halyard_control_crypto.c needs them; the control-session and discovery suites link it.
#
# Defined above the rule that uses them, and that is required. Make expands a prerequisite list when it
# reads the file, so a variable defined further down yields an EMPTY prerequisite - the rule then runs
# with $< unset and gcc reports "no input files", naming neither the variable nor the rule. Incremental
# builds stay green while build/halyard_v1_constants.g.c exists; only `make clean` exposes it.
# libripcord/tests/Makefile documents the same trap for its own MBEDTLS_PREFIX.
BUNDLE    := ../../src/Ripcord.Protocol.Halyard/Data/halyard-v1-constants.json
GENERATED := $(BUILD)/halyard_v1_constants.g.c
PYTHON    ?= python3

$(GENERATED): $(BUNDLE) $(CORE)/tools/gen_constants.py
	@mkdir -p $(@D)
	$(PYTHON) $(CORE)/tools/gen_constants.py --registration --for ripcord-ps3 $(BUNDLE) $@

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

# Per-file objects, because the link is a C++ link over C objects and the two need different flags. The
# C sources must be compiled by $(CC) with -std=gnu99 - g++ rejects that option outright - while the
# LINK has to be $(CXX), so libstdc++ and its static initialisers come in.
#
# vpath rather than per-directory rules: the basenames across these directories are unique.
#
# EVERY source directory has to appear here. One that is added to SOURCES and forgotten here does not
# produce a compile error - make reports "No rule to make target", stops, and leaves the PREVIOUS
# package sitting in place, which then uploads and installs perfectly and runs the old build.
vpath %.c source/video $(CORE)/util $(CORE)/discovery $(CORE)/session $(CORE)/net $(CORE)/takion \
          $(CORE)/crypto $(CORE)/stream $(CORE)/halyard \
          source/platform source/spu source/net source/media source/discovery source/connect \
          source/app source/input source/ui $(CORE)/ui

COBJS := $(addprefix $(BUILD)/,$(notdir $(SOURCES:.c=.o)))

# The generated headers are order-only prerequisites: rc_build_id.h, rc_spu_symbols.h and the
# rc_spu_probe_bin.h that bin2s writes alongside its object. Order-only (after the |) because the build
# id changes on every source change by design, and making every object depend on its content would
# rebuild the world each time.
#
# Core files that open sockets - halyard_control_session.c and rc_tcp.c among them - include
# <net/socket.h>, which pulls in ppu-lv2.h, whose inline syscall wrappers convert int to u64 all over.
# source/app/main.c pragmas that away for its own includes, but libripcord files are shared by every
# port and a PSL1GHT-specific pragma has no business in a platform-neutral layer. So the relaxation
# lives here, scoped to the files that need it, and only the SDK headers are excused.
# Appended, not filtered: -Wconversion implies -Wsign-conversion for C, so removing it from the list is
# not enough. A trailing -Wno- wins because later options override earlier ones.
# -include sys/time.h, because PSL1GHT's <sys/socket.h> declares sysNetSelect taking a
# `struct timeval *` without having pulled that struct in - so the first thing to include it gets
# "declared inside parameter list will not be visible outside of this definition", which is fatal under
# -Werror. Same shape as the -include stdio.h that tools/build-openh264.sh needs, and the same reason:
# fix it from outside rather than edit code that is not ours.
CFLAGS_SDK_SOCKETS := $(CFLAGS) -Wno-sign-conversion -include sys/time.h

# Every object that reaches a socket header, as a list with one target-specific override. A
# target-specific CFLAGS assignment overrides the value the pattern rule below expands, so the list is
# the only thing to maintain.
#
# It is a LIST and not a wildcard on purpose: the relaxation drops a real warning class, so a file joins
# it deliberately or not at all. rc_stack_ps3.o is here for a thread syscall rather than a socket one,
# and rc_audio_ps3.o for the audio ones, but the cause is identical in each case: PSL1GHT's inline lv2
# wrappers convert int to u64, and that is the SDK's code, not this port's.
SDK_SOCKET_OBJS := \
	$(BUILD)/halyard_control_session.o \
	$(BUILD)/halyard_control_probe.o \
	$(BUILD)/rc_pair_ps3.o \
	$(BUILD)/rc_tcp.o \
	$(BUILD)/rc_udp.o \
	$(BUILD)/rc_connect.o \
	$(BUILD)/takion_reliable_channel.o \
	$(BUILD)/rc_stack_ps3.o \
	$(BUILD)/rc_audio_ps3.o \
	$(BUILD)/rc_video_ps3.o \
	$(BUILD)/rc_spu_yuv.o

$(SDK_SOCKET_OBJS): CFLAGS := $(CFLAGS_SDK_SOCKETS)

$(BUILD)/%.o: %.c | $(BUILD)/rc_build_id.h $(BUILD)/rc_spu_symbols.h $(BUILD)/$(SPU_NAME)_bin.o $(BUILD)/$(SPU2_NAME)_bin.o
	@mkdir -p $(@D)
	$(CC) $(CFLAGS) $(DEPFLAGS) -c -o $@ $<

$(BUILD)/$(APP).elf: $(COBJS) $(BUILD)/$(SPU_NAME)_bin.o $(BUILD)/$(SPU2_NAME)_bin.o $(BUILD)/rc_decode_probe.o $(CORE_TEST_OBJS)
	@mkdir -p $(@D)
	$(CXX) $(LDFLAGS) -o $@ $(COBJS) $(BUILD)/$(SPU_NAME)_bin.o $(BUILD)/$(SPU2_NAME)_bin.o \
	       $(BUILD)/rc_decode_probe.o $(CORE_TEST_OBJS) $(OPENH264_LIB) $(OPUS_LIB) $(MBEDTLS_LIB) $(LIBS)

# The prepared ELF - strip, then sprxlinker. BOTH STEPS, IN THAT ORDER, AND THE SECOND ONE IS NOT
# OPTIONAL.
#
# $(PSL1GHT)/ppu_rules' own %.self rule runs $(STRIP), then $(SPRX), then the self tool. sprxlinker
# patches the PRX import stubs in .sceStub.text, and until it has run every call through a stub -
# sysGetRandomNumber here, and whatever lv2-crt0 touches on the way to main - is unresolved. The program
# dies before its first instruction and produces no output at all.
#
# A separate output file rather than in-place: sprxlinker rewrites its argument, so the raw link output
# is never touched and this is what the packagers read.
$(BUILD)/$(APP).ready.elf: $(BUILD)/$(APP).elf
	@test -x "$(SPRX)" || { echo "ERROR: sprxlinker not found at $(SPRX) - the build would silently produce an unrunnable binary. See this rule's comment."; exit 1; }
	cp $< $@
	$(STRIP) -g $@
	$(SPRX) $@

# elf -> self, following $(PSL1GHT)/ppu_rules' %.self rule: make_self produces the primary artifact and
# fself a secondary .fake.self beside it.
#
# Do not use fself for the primary. ppu_rules labels the make_self output "CEX self", makes it the
# .self, and every run: target in PSL1GHT - ten of them - hands ps3load that file. A fake SELF
# transferred to the listener perfectly well and then did not launch.
$(APP).self: $(BUILD)/$(APP).ready.elf
	$(SELF) $< $@
	$(FSELF) $< $(APP).fake.self

# The package, for launching from the XMB rather than through a loader.
#
# `make` alone gives you ripcord-ps3.self, which is what ps3load sends over the network and what most
# file-manager homebrew will run in place. `make pkg` gives you ripcord-ps3.pkg, which installs through
# the console's own package installer, appears on the XMB, and creates /dev_hdd0/game/$(APPID)/. Which
# one you want depends on how the console is set up - see SETUP.md section 6.
#
# The steps are the %.pkg rule in $(PSL1GHT)/ppu_rules, spelled out here because this Makefile
# deliberately does not include ppu_rules: including it would quietly replace the warning and
# architecture flags set above.
$(ICON0): tools/make-icon.py ../../brand/ripcord-ground.svg ../../brand/ripcord-mark-ondark.svg
	@mkdir -p $(BUILD)
	python3 tools/make-icon.py $@

# The Makefile is a prerequisite: otherwise a change to how PARAM.SFO is written leaves make deciding
# the package is up to date, and it ships with the previous build's metadata.
# The notices are a prerequisite of the package too. openh264, Opus and Mbed TLS each require their
# licence to travel with a BINARY distribution - the BSDs' "in the documentation and/or other materials
# provided with the distribution", Apache-2.0 section 4's "give any other recipients a copy of this
# License". A .pkg that ships without them is a licence breach, so the generator exits non-zero rather
# than emitting a partial file and this rule fails with it.
$(NOTICES): tools/gen-third-party-notices.sh
	@mkdir -p $(@D)
	sh tools/gen-third-party-notices.sh $@

notices: $(NOTICES)

$(APP).pkg: $(BUILD)/$(APP).ready.elf $(ICON0) $(NOTICES) Makefile
	@rm -rf $(BUILD)/pkg
	@mkdir -p $(BUILD)/pkg/USRDIR
	cp $(ICON0) $(BUILD)/pkg/ICON0.PNG
	cp $(NOTICES) $(BUILD)/pkg/USRDIR/THIRD-PARTY-NOTICES.txt
	$(SELF_NPDRM) $< $(BUILD)/pkg/USRDIR/EBOOT.BIN $(CONTENTID) > /dev/null
	@sed 's/01\.00/$(APP_VER)/g' $(SFOXML) > $(BUILD)/sfo.xml
	$(SFO) --title "$(TITLE)" --appid "$(APPID)" -f $(BUILD)/sfo.xml $(BUILD)/pkg/PARAM.SFO
	$(PKG) --contentid $(CONTENTID) $(BUILD)/pkg/ $@ > /dev/null
	@cp $@ $(APP).gnpdrm.pkg
	$(PKG_FINALIZE) $(APP).gnpdrm.pkg

pkg: $(APP).pkg

.PHONY: notices

# The fast loop. Sends the program straight to a listener on the console over TCP 4299 and relays its
# stdout back, so an iteration is one command with no install step and no XMB round trip.
#
# It needs a ps3load server running on the PS3 - either multiMAN, or PSL1GHT's own
# samples/network/ps3load built and installed.
#
# Sends the .self, NOT the ELF. Every run: target in PSL1GHT - ten of them - is
# `ps3load $(OUTPUT).self`, and the listener writes whatever it receives to /dev_hdd0/tmp/ps3load.self
# and launches that, so a raw ELF arrives intact and then does not start. ps3load reports the transfer
# and exits "done." either way; the build id in the console's log is what shows a stale binary.
#
# The listener is one-shot. It ends with sysProcessExitSpawn2, which replaces the listener process with
# the program it just received - so it has to be launched again on the console before each run. A
# "connection refused" here usually means the last run consumed it rather than anything being wrong.
#
#     make run PS3LOAD=tcp:192.168.1.42
run: $(APP).self
	@test -n "$(PS3LOAD)" || { echo "ERROR: set PS3LOAD, e.g. make run PS3LOAD=tcp:<console-ip>"; exit 1; }
	PS3LOAD=$(PS3LOAD) $(PS3DEV)/bin/ps3load $<

# Convenience passthrough: this port's host tests - the H.264 front end, the CSPRNG seam's host-testable
# half, the shell background's colours and the pad chord - and then libripcord's own suites. All run on
# the host with no console.
test:
	$(MAKE) -C tests
	$(MAKE) -C $(CORE)/tests

# Note what clean does NOT remove: $(BUILDNO_FILE), for the reason given on BUILD_NO, and
# $(OPENH264_DIR), for the reason given there.
clean:
	rm -f $(APP).self $(APP).fake.self $(APP).pkg $(APP).gnpdrm.pkg
	rm -rf $(BUILD)

# Everything clean removes, plus the fetched openh264 tree. Separate because re-fetching and
# rebuilding openh264 is minutes, and wanting fresh objects is not wanting that.
distclean: clean
	rm -rf $(OPENH264_DIR)

# The dependency files the DEPFLAGS above produce. Wildcard rather than a derived list: naming them from
# the object list would demand that each one exist.
# Not included for `clean`, which would otherwise rebuild them only to delete them.
ifeq (,$(filter clean distclean,$(MAKECMDGOALS)))
-include $(wildcard $(BUILD)/*.d)
endif
