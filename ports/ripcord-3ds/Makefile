# ripcord-3ds - the 3DS build.
#
# Produces ripcord-3ds.3dsx (Homebrew Launcher). Requires devkitPro with devkitARM and libctru:
# https://devkitpro.org/wiki/Getting_Started. On Windows that means running this from the devkitPro MSYS2
# shell, which is where `make` and the ARM toolchain live.
#
#     make                build ripcord-3ds.3dsx
#     make constants      regenerate the interop constants only
#     make clean
#     make test           run the HOST-side known-answer tests (no 3DS involved) - see tests/Makefile
#
# NOT IN THE SOLUTION FILE, AND NOT BUILDABLE BY dotnet. Ripcord.slnx is MSBuild driving .NET and
# C++/WinRT; this is a Makefile driving a cross-compiler for a different CPU and a different OS entirely.
# Sharing a repository with Ripcord buys shared specs, shared constants and shared test vectors - it does
# not and should not mean sharing a build system.
#
# STATUS: the host-side tests in tests/ are the verified part. This makefile has not yet been run against
# a real devkitPro installation; treat the flags below as a starting point rather than a known-good
# configuration, and fix them in place when they turn out to be wrong.

ifeq ($(strip $(DEVKITPRO)),)
$(error DEVKITPRO is not set. Install devkitPro and run this from its shell - see the URL above)
endif
ifeq ($(strip $(DEVKITARM)),)
$(error DEVKITARM is not set. Install the devkitARM toolchain - see the URL above)
endif

PYTHON ?= python3

PREFIX  := $(DEVKITARM)/bin/arm-none-eabi-
CC      := $(PREFIX)gcc
OBJCOPY := $(PREFIX)objcopy

CTRULIB := $(DEVKITPRO)/libctru

TARGET    := ripcord-3ds
LINKTEST  := ripcord-3ds-linktest
DISCOVERY := ripcord-3ds-discovery
SESSION   := ripcord-3ds-session
BUILD     := build

REPO_ROOT := ../..
BUNDLE    := $(REPO_ROOT)/src/Ripcord.Protocol.Halyard/Data/halyard-v1-constants.json
GENERATED := $(BUILD)/halyard_v1_constants.g.c

# ARM11 MPCore: ARMv6K, hard-float, soft thread pointer. These are the standard devkitARM 3DS flags; they
# are not negotiable in the way ordinary optimisation flags are, because libctru is built with them and a
# mismatched float ABI links cleanly and then misbehaves at runtime.
ARCH    := -march=armv6k -mtune=mpcore -mfloat-abi=hard -mtp=soft

CFLAGS  := $(ARCH) -std=c99 -O2 -g \
           -Wall -Wextra -Werror -Wshadow -Wconversion -Wsign-conversion \
           -ffunction-sections -fdata-sections \
           -D__3DS__ \
           -isystem $(CTRULIB)/include -Isource/crypto -Isource/halyard

LDFLAGS := $(ARCH) -specs=3dsx.specs -Wl,--gc-sections -L$(CTRULIB)/lib
LIBS    := -lctru -lm

SOURCES := \
	source/crypto/rc_aes.c \
	source/crypto/rc_sha256.c \
	source/crypto/rc_modes.c \
	source/halyard/halyard_control_crypto.c \
	source/util/rc_log.c \
	source/util/rc_program_dir.c \
	source/app/main.c

# Phase 2 (SETUP.md): the UDP link test. Deliberately has no dependency on source/crypto or
# source/halyard - it answers a network question, not a protocol one, and building it standalone keeps
# that true rather than assumed. source/util is shared with the crypto smoke test above.
LINKTEST_SOURCES := \
	source/net/rc_soc.c \
	source/util/rc_log.c \
	source/util/rc_program_dir.c \
	source/linktest/main.c

# Phase 3 (SETUP.md): LAN discovery. source/discovery has no dependency on source/crypto or
# source/halyard either - see halyard_discovery.h for why the parser itself has no socket dependency in
# turn, and source/discovery/main.c for where sockets and parsing meet. source/util/rc_text.c is the
# trim/case-insensitive-match helper shared with source/session's HTTP-like parsing below.
DISCOVERY_SOURCES := \
	source/net/rc_soc.c \
	source/util/rc_log.c \
	source/util/rc_program_dir.c \
	source/util/rc_text.c \
	source/discovery/halyard_discovery.c \
	source/discovery/main.c

# Phase 4 (SETUP.md): the /sess/init -> /sess/ctrl exchange. Unlike discovery/linktest, this genuinely
# needs source/halyard (the control-field cipher, established from /sess/init's RP-Nonce) and therefore
# source/crypto beneath it - this is that layer's first real end-to-end use outside a test harness.
SESSION_SOURCES := \
	source/crypto/rc_aes.c \
	source/crypto/rc_sha256.c \
	source/crypto/rc_modes.c \
	source/halyard/halyard_control_crypto.c \
	source/net/rc_soc.c \
	source/net/rc_tcp.c \
	source/util/rc_log.c \
	source/util/rc_base64.c \
	source/util/rc_hex.c \
	source/util/rc_program_dir.c \
	source/util/rc_text.c \
	source/session/halyard_control_arm.c \
	source/session/halyard_ctrl_message.c \
	source/session/halyard_sess_fields.c \
	source/session/halyard_sess_request.c \
	source/session/main.c

.PHONY: all constants clean test linktest discovery session

all: $(TARGET).3dsx $(LINKTEST).3dsx $(DISCOVERY).3dsx $(SESSION).3dsx

linktest: $(LINKTEST).3dsx

discovery: $(DISCOVERY).3dsx

session: $(SESSION).3dsx

# The interop constants are generated from the single committed JSON bundle rather than duplicated into
# this port. tools/gen_constants.py explains why that is a deliberate constraint and not just tidiness.
constants: $(GENERATED)

# Each recipe creates its own output directory directly rather than depending on a separate directory
# target: with three build products under build/, an order-only prerequisite on a shared "build" target
# makes GNU Make's dependency graph see a cycle - confirmed the hard way in tests/Makefile once it grew a
# second target - and it silently drops the mkdir side effect instead of erroring, so the link step fails
# for a directory that was never created. `@mkdir -p $(@D)` has no such graph to get confused about.

$(GENERATED): $(BUNDLE) tools/gen_constants.py
	@mkdir -p $(@D)
	$(PYTHON) tools/gen_constants.py $(BUNDLE) $@

$(BUILD)/$(TARGET).elf: $(SOURCES) $(GENERATED)
	@mkdir -p $(@D)
	$(CC) $(CFLAGS) $(LDFLAGS) -o $@ $(SOURCES) $(GENERATED) $(LIBS)

$(TARGET).3dsx: $(BUILD)/$(TARGET).elf
	3dsxtool $< $@

$(BUILD)/$(LINKTEST).elf: $(LINKTEST_SOURCES)
	@mkdir -p $(@D)
	$(CC) $(CFLAGS) $(LDFLAGS) -o $@ $(LINKTEST_SOURCES) $(LIBS)

$(LINKTEST).3dsx: $(BUILD)/$(LINKTEST).elf
	3dsxtool $< $@

$(BUILD)/$(DISCOVERY).elf: $(DISCOVERY_SOURCES)
	@mkdir -p $(@D)
	$(CC) $(CFLAGS) $(LDFLAGS) -o $@ $(DISCOVERY_SOURCES) $(LIBS)

$(DISCOVERY).3dsx: $(BUILD)/$(DISCOVERY).elf
	3dsxtool $< $@

$(BUILD)/$(SESSION).elf: $(SESSION_SOURCES) $(GENERATED)
	@mkdir -p $(@D)
	$(CC) $(CFLAGS) $(LDFLAGS) -o $@ $(SESSION_SOURCES) $(GENERATED) $(LIBS)

$(SESSION).3dsx: $(BUILD)/$(SESSION).elf
	3dsxtool $< $@

# Convenience passthrough. The host tests are the ones that actually verify the crypto, so make them easy
# to reach from the directory people will be standing in.
test:
	$(MAKE) -C tests

clean:
	rm -rf $(BUILD) $(TARGET).3dsx $(LINKTEST).3dsx $(DISCOVERY).3dsx $(SESSION).3dsx
	$(MAKE) -C tests clean
