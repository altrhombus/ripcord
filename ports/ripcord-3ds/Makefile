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

TARGET  := ripcord-3ds
BUILD   := build

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
	source/app/main.c

.PHONY: all constants clean test

all: $(TARGET).3dsx

# The interop constants are generated from the single committed JSON bundle rather than duplicated into
# this port. tools/gen_constants.py explains why that is a deliberate constraint and not just tidiness.
constants: $(GENERATED)

$(BUILD):
	mkdir -p $(BUILD)

$(GENERATED): $(BUNDLE) tools/gen_constants.py | $(BUILD)
	$(PYTHON) tools/gen_constants.py $(BUNDLE) $@

$(BUILD)/$(TARGET).elf: $(SOURCES) $(GENERATED) | $(BUILD)
	$(CC) $(CFLAGS) $(LDFLAGS) -o $@ $(SOURCES) $(GENERATED) $(LIBS)

$(TARGET).3dsx: $(BUILD)/$(TARGET).elf
	3dsxtool $< $@

# Convenience passthrough. The host tests are the ones that actually verify the crypto, so make them easy
# to reach from the directory people will be standing in.
test:
	$(MAKE) -C tests

clean:
	rm -rf $(BUILD) $(TARGET).3dsx
	$(MAKE) -C tests clean
