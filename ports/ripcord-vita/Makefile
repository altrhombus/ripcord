# ripcord-vita - the PS Vita build.
#
# Produces .vpk files installable with VitaShell on HENkaku/h-encore hardware. Requires vitasdk:
# https://vitasdk.org/ - set $VITASDK and put $VITASDK/bin on PATH.
#
#     make                build every .vpk
#     make mbedtls        cross-build the ECDH backend only (ports/common/tools/build-mbedtls.sh)
#     make constants      regenerate the interop constants only
#     make clean          remove build output, KEEPING the fetched mbedtls
#     make distclean      remove build output including mbedtls
#     make test           run the HOST-side known-answer tests (no Vita involved) - ports/common/tests
#
# NOT IN THE SOLUTION FILE, AND NOT BUILDABLE BY dotnet, for the same reason the 3DS port is not:
# Ripcord.slnx is MSBuild driving .NET and C++/WinRT, and this is a Makefile driving a cross-compiler
# for a different CPU and a different OS.
#
# STATUS: the toolchain, the portable core and the platform seam all compile; the packaging chain
# produces a .vpk. NOTHING HAS BEEN RUN ON HARDWARE. What each program has and has not been run against
# belongs in README.md, not here.

ifeq ($(strip $(VITASDK)),)
$(error VITASDK is not set. Install vitasdk and export VITASDK - see the URL above)
endif

PYTHON ?= python3

PREFIX := arm-vita-eabi-
CC     := $(PREFIX)gcc
STRIP  := $(PREFIX)strip

# The portable protocol core, shared with every other Ripcord port. See ports/common/platform/
# rc_platform.h for the seam this build implements in source/platform/.
CORE      := ../common
REPO_ROOT := ../..
BUILD     := build

BUNDLE    := $(REPO_ROOT)/src/Ripcord.Protocol.Halyard/Data/halyard-v1-constants.json
GENERATED := $(BUILD)/halyard_v1_constants.g.c

TITLEID_APP := RIPC00001
TITLE_APP   := Ripcord Smoke Test
APP         := ripcord-vita

# NO ARCHITECTURE FLAGS, DELIBERATELY - and this is the exact inverse of the 3DS port, so it is worth
# stating rather than leaving as an absence.
#
# devkitARM needs -march=armv6k -mtune=mpcore -mfloat-abi=hard -mtp=soft spelled out, because libctru is
# built with them and a mismatched float ABI links cleanly and then misbehaves at runtime. vitasdk is the
# opposite: `arm-vita-eabi-gcc -Q --help=target` already reports armv7-a+simd / cortex-a9 / neon /
# mfloat-abi=hard as its DEFAULTS, newlib here is built hard-float (Tag_ABI_VFP_args: VFP registers), and
# vitasdk's own share/vita.toolchain.cmake sets no arch flags whatsoever - only -Wl,-q.
#
# So specifying them is how you break it. An earlier revision of this file passed
# -march=armv7-a -mtune=cortex-a9 -mfpu=neon -mfloat-abi=softfp, which looks like a faithful description
# of the hardware and is not: softfp is a different CALLING CONVENTION from hard, and the link failed
# with "uses VFP register arguments, <obj> does not" on every translation unit. The hardware description
# was right and the ABI was wrong.
ARCH   :=

# -Wl,-q is REQUIRED, not optional. It keeps the relocation sections that vita-elf-create needs to build
# a VELF; without it the link succeeds and vita-elf-create then fails on the result.

# -Wframe-larger-than is carried over from the 3DS port, where it was not a style preference but a crash
# the hardware had already found twice. The Vita's default main-thread stack is reported as 4 KiB - an
# order of magnitude tighter than the 32 KB that crashed the 3DS - so the rule matters more here, not
# less. source/platform/rc_stack_vita.c raises the stack; this keeps individual frames honest anyway.
CFLAGS := $(ARCH) -std=c99 -O2 -g \
          -Wall -Wextra -Werror -Wshadow -Wconversion -Wsign-conversion \
          -Wframe-larger-than=8192 \
          -ffunction-sections -fdata-sections \
          -DRC_PROGRAM_DIR_FALLBACK='"ux0:data/ripcord/"' \
          -I$(CORE) -I$(CORE)/crypto -I$(CORE)/halyard -I$(CORE)/platform

# --undefined keeps the loader-read stack-size symbol reachable; see source/platform/rc_stack_vita.c.
LDFLAGS := $(ARCH) -Wl,-q -Wl,--gc-sections -Wl,--undefined=sceUserMainThreadStackSize

# The ECDH backend - the one primitive ports/common does not implement itself (rc_ecdh.h says why).
#
# Unlike the 3DS, where devkitPro packages `3ds-mbedtls` and the Makefile just links it, vitasdk
# packages no mbedtls at all. tools/build-mbedtls.sh cross-builds the ECP/MPI layer only - five
# translation units, ~23 KB of text - from a pinned, hash-verified upstream release, into a gitignored
# build/mbedtls. The version is pinned to 2.28.8 to MATCH the 3DS's package, so one rc_ecdh.c serves
# both ports.
#
# Set ECDH_BACKEND=none to build without it; rc_ecdh.c then compiles to functions that fail cleanly and
# rc_ecdh_available() returns 0, rather than to a stub that fakes a key agreement.
ECDH_BACKEND ?= mbedtls
MBEDTLS_PREFIX := $(BUILD)/mbedtls

ifeq ($(ECDH_BACKEND),mbedtls)
ECDH_CFLAGS := -DRC_CRYPTO_MBEDTLS -isystem $(MBEDTLS_PREFIX)/include \
               -DMBEDTLS_CONFIG_FILE='<ripcord-mbedtls-config.h>'
ECDH_LIBS   := $(MBEDTLS_PREFIX)/lib/libmbedcrypto.a
ECDH_DEP    := $(MBEDTLS_PREFIX)/lib/libmbedcrypto.a
else
ECDH_CFLAGS :=
ECDH_LIBS   :=
ECDH_DEP    :=
endif

# Sony's own module stubs. SceLibKernel/SceSysmodule are the baseline; the rest are added per target as
# the phases need them (SceNet for discovery, SceVideodec for decode, SceAudio, SceCtrl, SceTouch,
# SceMotion, ScePower). All 488 stub archives ship with vitasdk - nothing here needs a portlib yet.
LIBS := -lSceLibKernel_stub -lSceSysmodule_stub

# The platform seam: everything ports/common asks of an OS, plus this port's CSPRNG and the main-thread
# stack size. rc_stack_vita.c defines a symbol the loader reads before main() and is referenced by
# nothing, so it must never be dropped by --gc-sections; it is a definition in .data, which the linker
# keeps for a non-section-collected symbol, but see its own comment for why this is unverified.
PLATFORM_SOURCES := \
	source/platform/rc_platform_vita.c \
	source/platform/rc_random_vita.c \
	source/platform/rc_stack_vita.c

# Phase 1: the on-device crypto smoke test. Mirrors ports/ripcord-3ds/source/app/main.c so the timing
# number it prints is comparable with the ARM11's ~25 us.
APP_SOURCES := \
	$(CORE)/crypto/rc_aes.c \
	$(CORE)/crypto/rc_sha256.c \
	$(CORE)/crypto/rc_modes.c \
	$(CORE)/halyard/halyard_control_crypto.c \
	$(CORE)/crypto/rc_ecdh.c \
	$(CORE)/util/rc_log.c \
	$(CORE)/util/rc_program_dir.c \
	$(PLATFORM_SOURCES) \
	source/app/main.c

.PHONY: all constants clean distclean test

all: $(APP).vpk

constants: $(GENERATED)

# The interop constants are generated from the single committed JSON bundle rather than duplicated into
# this port - see $(CORE)/tools/gen_constants.py for why that is a deliberate constraint rather than
# tidiness, and the repository NOTICE for the argument the one committed copy rests on.
$(GENERATED): $(BUNDLE) $(CORE)/tools/gen_constants.py
	@mkdir -p $(@D)
	$(PYTHON) $(CORE)/tools/gen_constants.py $(BUNDLE) $@

# elf -> velf -> eboot.bin -> vpk, the standard vitasdk packaging chain.
$(MBEDTLS_PREFIX)/lib/libmbedcrypto.a: $(CORE)/tools/build-mbedtls.sh $(CORE)/tools/ripcord-mbedtls-config.h
	PREFIX=$(abspath $(MBEDTLS_PREFIX)) $(CORE)/tools/build-mbedtls.sh

.PHONY: mbedtls
mbedtls: $(MBEDTLS_PREFIX)/lib/libmbedcrypto.a

$(BUILD)/$(APP).elf: $(APP_SOURCES) $(GENERATED) $(ECDH_DEP)
	@mkdir -p $(@D)
	$(CC) $(CFLAGS) $(ECDH_CFLAGS) $(LDFLAGS) -o $@ $(APP_SOURCES) $(GENERATED) $(ECDH_LIBS) $(LIBS)

$(BUILD)/$(APP).velf: $(BUILD)/$(APP).elf
	$(STRIP) -g $<
	vita-elf-create $< $@

$(BUILD)/eboot.bin: $(BUILD)/$(APP).velf
	vita-make-fself $< $@

$(BUILD)/param.sfo:
	@mkdir -p $(@D)
	vita-mksfoex -s TITLE_ID="$(TITLEID_APP)" "$(TITLE_APP)" $@

$(APP).vpk: $(BUILD)/eboot.bin $(BUILD)/param.sfo
	vita-pack-vpk -s $(BUILD)/param.sfo -b $(BUILD)/eboot.bin $@

# Convenience passthrough. The host tests are the ones that actually verify the crypto, so make them
# easy to reach from the directory people will be standing in.
test:
	$(MAKE) -C $(CORE)/tests

# clean deliberately SPARES build/mbedtls. It is a hash-verified download plus a cross-compile, and
# making `make clean` re-fetch it over the network turns a five-second habit into a minute and a
# dependency on being online. Use distclean when you actually mean it.
clean:
	rm -f $(APP).vpk
	@find $(BUILD) -mindepth 1 -maxdepth 1 ! -name mbedtls -exec rm -rf {} + 2>/dev/null || true

distclean: clean
	rm -rf $(BUILD)
