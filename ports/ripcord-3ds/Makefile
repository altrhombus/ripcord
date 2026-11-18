# ripcord-3ds - the 3DS build.
#
# Produces ripcord-3ds.3dsx (Homebrew Launcher). Requires devkitPro with devkitARM and libctru:
# https://devkitpro.org/wiki/Getting_Started. On Windows that means running this from the devkitPro MSYS2
# shell, which is where `make` and the ARM toolchain live.
#
#     make                build every .3dsx
#     make constants      regenerate the interop constants only
#     make crosscheck     compile the stream-plane core modules to objects, linking nothing (see CROSSCHECK_SOURCES)
#     make clean
#     make test           run the HOST-side known-answer tests (no 3DS involved) - see libripcord/tests/Makefile
#
# NOT IN THE SOLUTION FILE, AND NOT BUILDABLE BY dotnet. Ripcord.slnx is MSBuild driving .NET and
# C++/WinRT; this is a Makefile driving a cross-compiler for a different CPU and a different OS entirely.
# Sharing a repository with Ripcord buys shared specs, shared constants and shared test vectors - it does
# not and should not mean sharing a build system.
#
# What each program has and has not been RUN against is recorded in BRINGUP.md, not here.

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

# The portable protocol core, shared with every other Ripcord port. See libripcord/platform/
# rc_platform.h for the seam this build implements in source/platform/rc_platform_3ds.c.
CORE    := ../../libripcord

TARGET    := ripcord-3ds
LINKTEST  := ripcord-3ds-linktest
DISCOVERY := ripcord-3ds-discovery
SESSION   := ripcord-3ds-session
TAKION    := ripcord-3ds-takion
CONNECT   := ripcord-3ds-connect
MVDREPLAY := ripcord-3ds-mvdreplay
BUILD     := build

REPO_ROOT := ../..
BUNDLE    := $(REPO_ROOT)/src/Ripcord.Protocol.Halyard/Data/halyard-v1-constants.json
GENERATED := $(BUILD)/halyard_v1_constants.g.c

# ARM11 MPCore: ARMv6K, hard-float, soft thread pointer. These are the standard devkitARM 3DS flags; they
# are not negotiable in the way ordinary optimisation flags are, because libctru is built with them and a
# mismatched float ABI links cleanly and then misbehaves at runtime.
ARCH    := -march=armv6k -mtune=mpcore -mfloat-abi=hard -mtp=soft

# -Wframe-larger-than is not a style preference here, it is a crash the hardware already found.
#
# libctru's default __stacksize__ for a .3dsx is 0x8000 - 32 KB, for the WHOLE main thread. This port's
# protocol state does not fit in that: takion_reliable_channel is 49.6 KB (32 unacked 1500-byte packets)
# and stream_demux is 513 KB. Declaring either as an ordinary local overflows the stack in the function
# PROLOGUE, before a single line of the function body runs - which on 2026-08-12 presented as a data
# abort writing 12 bytes below SP, with a log file that stopped after the banner.
#
# 8 KB per frame leaves room for a reasonable call depth inside 32 KB. Anything bigger belongs in .bss
# (file-scope or `static`) or on the heap; with -Werror below, exceeding it fails the build.
CFLAGS  := $(ARCH) -std=c99 -O2 -g \
           -Wall -Wextra -Werror -Wshadow -Wconversion -Wsign-conversion \
           -Wframe-larger-than=8192 \
           -ffunction-sections -fdata-sections \
           -D__3DS__ \
           -isystem $(CTRULIB)/include -I$(CORE) -I$(CORE)/crypto -I$(CORE)/halyard -I$(CORE)/platform

LDFLAGS := $(ARCH) -specs=3dsx.specs -Wl,--gc-sections -L$(CTRULIB)/lib
LIBS    := -lctru -lm

# mbedtls, the ECDH backend: the only cryptographic primitive this port does not implement itself - see
# libripcord/crypto/rc_ecdh.h for that argument. Install with:
#
#     dkp-pacman -S 3ds-mbedtls
#
# Set ECDH_BACKEND=none to build without it; rc_ecdh.c then compiles to functions that fail cleanly
# rather than to a stub that fakes a key agreement.
PORTLIBS := $(DEVKITPRO)/portlibs/3ds

# Opus, for the audio path. Independent of the ECDH backend on purpose: audio and key agreement are
# unrelated, and tying them together would make "built without mbedtls" silently mean "built without
# sound". -isystem for the same reason libctru gets it - this project's -Werror -Wconversion policy has
# no business being applied to headers it does not own.
AUDIO_CFLAGS := -isystem $(PORTLIBS)/include
AUDIO_LIBS   := -L$(PORTLIBS)/lib -lopus

ECDH_BACKEND ?= mbedtls

ifeq ($(ECDH_BACKEND),mbedtls)
ECDH_CFLAGS := -DRC_CRYPTO_MBEDTLS -isystem $(PORTLIBS)/include
ECDH_LIBS   := -L$(PORTLIBS)/lib -lmbedcrypto
else
ECDH_CFLAGS :=
ECDH_LIBS   :=
endif

SOURCES := \
	$(CORE)/crypto/rc_aes.c \
	$(CORE)/crypto/rc_sha256.c \
	$(CORE)/crypto/rc_modes.c \
	$(CORE)/halyard/halyard_control_crypto.c \
	$(CORE)/util/rc_log.c \
	source/util/rc_stack.c \
	$(CORE)/util/rc_program_dir.c \
	source/app/main.c

# The UDP link test. Links no crypto or protocol code from the core - it answers a network question, not
# a protocol one, and building it standalone keeps that true rather than assumed. rc_log, rc_stack and
# rc_program_dir are shared with the crypto smoke test above.
LINKTEST_SOURCES := \
	source/net/rc_soc.c \
	$(CORE)/util/rc_log.c \
	source/util/rc_stack.c \
	$(CORE)/util/rc_program_dir.c \
	source/linktest/main.c

# LAN discovery. Links no crypto either - see libripcord/discovery/halyard_discovery.h for why the parser
# itself has no socket dependency, and source/discovery/main.c for where sockets and parsing meet.
# $(CORE)/util/rc_text.c is the trim/case-insensitive-match helper shared with the session probe's
# HTTP-like parsing below.
DISCOVERY_SOURCES := \
	source/net/rc_soc.c \
	$(CORE)/util/rc_log.c \
	source/util/rc_stack.c \
	$(CORE)/util/rc_program_dir.c \
	$(CORE)/util/rc_text.c \
	$(CORE)/discovery/halyard_discovery.c \
	source/discovery/main.c

# The /sess/init -> /sess/ctrl exchange. Unlike discovery and linktest, this needs $(CORE)/halyard (the
# control-field cipher, established from /sess/init's RP-Nonce) and therefore $(CORE)/crypto beneath it.
SESSION_SOURCES := \
	$(CORE)/crypto/rc_aes.c \
	$(CORE)/crypto/rc_sha256.c \
	$(CORE)/crypto/rc_modes.c \
	$(CORE)/halyard/halyard_control_crypto.c \
	source/net/rc_soc.c \
	$(CORE)/net/rc_tcp.c \
	source/platform/rc_platform_3ds.c \
	$(CORE)/util/rc_log.c \
	source/util/rc_stack.c \
	$(CORE)/util/rc_base64.c \
	$(CORE)/util/rc_hex.c \
	$(CORE)/util/rc_program_dir.c \
	$(CORE)/util/rc_text.c \
	$(CORE)/session/halyard_control_arm.c \
	$(CORE)/session/halyard_ctrl_message.c \
	$(CORE)/session/halyard_pairing_file.c \
	$(CORE)/session/halyard_sess_fields.c \
	$(CORE)/session/halyard_sess_request.c \
	source/session/main.c

# The Takion transport probe (handshake, DATA/SACK, reassembly). Links no crypto: this program does not
# seal its packets.
TAKION_SOURCES := \
	source/net/rc_soc.c \
	$(CORE)/util/rc_log.c \
	source/util/rc_stack.c \
	$(CORE)/util/rc_program_dir.c \
	$(CORE)/takion/takion_message.c \
	$(CORE)/takion/takion_handshake.c \
	$(CORE)/takion/takion_data_chunk.c \
	$(CORE)/takion/takion_sack_chunk.c \
	$(CORE)/takion/takion_reassembler.c \
	$(CORE)/takion/takion_reliable_channel.c \
	source/platform/rc_platform_3ds.c \
	source/takion/main.c

# The connect flow - the only program that carries a real control session all the way to derived stream
# keys and on into A/V, and therefore the only way to test Takion or the ECDH agreement against a console
# at all (see source/connect/main.c on why the standalone Takion probe cannot work). It links BOTH the
# control plane and the stream plane, and is the only target needing mbedtls and Opus.
CONNECT_SOURCES := \
	$(CORE)/crypto/rc_aes.c \
	$(CORE)/crypto/rc_sha256.c \
	$(CORE)/crypto/rc_modes.c \
	$(CORE)/crypto/rc_ecdh.c \
	$(CORE)/halyard/halyard_control_crypto.c \
	source/net/rc_soc.c \
	$(CORE)/net/rc_tcp.c \
	source/platform/rc_platform_3ds.c \
	$(CORE)/crypto/rc_gcm.c \
	$(CORE)/stream/stream_key_schedule.c \
	$(CORE)/stream/stream_demux.c \
	$(CORE)/stream/fec_galois.c \
	$(CORE)/stream/fec_reed_solomon.c \
	$(CORE)/stream/stream_header.c \
	$(CORE)/stream/stream_packet_crypto.c \
	$(CORE)/util/rc_log.c \
	source/util/rc_stack.c \
	$(CORE)/util/rc_base64.c \
	$(CORE)/util/rc_hex.c \
	$(CORE)/util/rc_program_dir.c \
	source/util/rc_profile.c \
	source/util/rc_random.c \
	$(CORE)/util/rc_text.c \
	$(CORE)/session/halyard_control_arm.c \
	$(CORE)/session/halyard_control_session.c \
	$(CORE)/session/halyard_ctrl_message.c \
	$(CORE)/session/halyard_launch_spec.c \
	$(CORE)/session/halyard_pairing_file.c \
	$(CORE)/session/halyard_sess_fields.c \
	$(CORE)/session/halyard_sess_request.c \
	$(CORE)/takion/takion_message.c \
	$(CORE)/takion/takion_handshake.c \
	$(CORE)/takion/takion_data_chunk.c \
	$(CORE)/takion/takion_sack_chunk.c \
	$(CORE)/takion/takion_reassembler.c \
	$(CORE)/takion/takion_reliable_channel.c \
	$(CORE)/takion/takion_control_proto.c \
	$(CORE)/takion/senkusha_echo.c \
	$(CORE)/input/halyard_input.c \
	$(CORE)/takion/takion_session_negotiator.c \
	source/media/rc_mvd.c \
	source/media/rc_audio.c \
	source/connect/main.c

# Stream-plane core modules, each compiled to an object with nothing linked: a check that they
# cross-compile clean for ARM11, which is exactly as far as the check goes. The connect target links all
# of them as well. Some files are listed more than once and are compiled once per listing.
CROSSCHECK_SOURCES := \
	$(CORE)/stream/stream_demux.c \
	$(CORE)/stream/fec_galois.c \
	$(CORE)/stream/fec_reed_solomon.c \
	$(CORE)/stream/stream_header.c \
	$(CORE)/stream/fec_galois.c \
	$(CORE)/stream/fec_reed_solomon.c \
	$(CORE)/stream/stream_key_schedule.c \
	$(CORE)/stream/stream_demux.c \
	$(CORE)/stream/fec_galois.c \
	$(CORE)/stream/fec_reed_solomon.c \
	$(CORE)/stream/stream_header.c \
	$(CORE)/stream/stream_packet_crypto.c \
	$(CORE)/stream/stream_demux.c \
	$(CORE)/crypto/rc_ecdh.c \
	$(CORE)/takion/takion_control_proto.c \
	$(CORE)/takion/senkusha_echo.c \
	$(CORE)/input/halyard_input.c \
	$(CORE)/takion/takion_session_negotiator.c

.PHONY: all constants clean test linktest discovery session takion connect crosscheck

all: $(TARGET).3dsx $(LINKTEST).3dsx $(DISCOVERY).3dsx $(SESSION).3dsx $(TAKION).3dsx $(CONNECT).3dsx $(MVDREPLAY).3dsx

linktest: $(LINKTEST).3dsx

discovery: $(DISCOVERY).3dsx

session: $(SESSION).3dsx

takion: $(TAKION).3dsx

connect: $(CONNECT).3dsx

mvdreplay: $(MVDREPLAY).3dsx

# The interop constants are generated from the single committed JSON bundle rather than duplicated into
# this port. libripcord/tools/gen_constants.py explains why that is a deliberate constraint and not just
# tidiness.
constants: $(GENERATED)

# Each recipe creates its own output directory directly rather than depending on a separate directory
# target: with several build products under build/, an order-only prerequisite on a shared "build" target
# makes GNU Make's dependency graph see a cycle - libripcord/tests/Makefile carries the same note -
# and it silently drops the mkdir side effect instead of erroring, so the link step fails for a directory
# that was never created. `@mkdir -p $(@D)` has no such graph to get confused about.

$(GENERATED): $(BUNDLE) $(CORE)/tools/gen_constants.py
	@mkdir -p $(@D)
	$(PYTHON) $(CORE)/tools/gen_constants.py --for ripcord-3ds $(BUNDLE) $@

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

$(BUILD)/$(TAKION).elf: $(TAKION_SOURCES)
	@mkdir -p $(@D)
	$(CC) $(CFLAGS) $(LDFLAGS) -o $@ $(TAKION_SOURCES) $(LIBS)

$(TAKION).3dsx: $(BUILD)/$(TAKION).elf
	3dsxtool $< $@

$(BUILD)/$(CONNECT).elf: $(CONNECT_SOURCES) $(GENERATED)
	@mkdir -p $(@D)
	$(CC) $(CFLAGS) $(ECDH_CFLAGS) $(AUDIO_CFLAGS) $(LDFLAGS) -o $@ $(CONNECT_SOURCES) $(GENERATED) \
		$(ECDH_LIBS) $(AUDIO_LIBS) $(LIBS)

# The replay tool needs only the decoder and its helpers - no crypto, no network, no protocol.
MVDREPLAY_SOURCES := \
	$(CORE)/util/rc_log.c \
	source/util/rc_stack.c \
	$(CORE)/util/rc_program_dir.c \
	source/util/rc_profile.c \
	source/media/rc_mvd.c \
	source/mvdreplay/main.c

$(BUILD)/$(MVDREPLAY).elf: $(MVDREPLAY_SOURCES)
	@mkdir -p $(BUILD)
	$(CC) $(CFLAGS) $(LDFLAGS) -o $@ $(MVDREPLAY_SOURCES) $(LIBS)

$(MVDREPLAY).3dsx: $(BUILD)/$(MVDREPLAY).elf
	3dsxtool $< $@

$(CONNECT).3dsx: $(BUILD)/$(CONNECT).elf
	3dsxtool $< $@

crosscheck:
	@mkdir -p $(BUILD)/crosscheck
	@for src in $(CROSSCHECK_SOURCES); do \
		echo "  CC $$src"; \
		$(CC) $(CFLAGS) $(ECDH_CFLAGS) $(AUDIO_CFLAGS) -c $$src -o $(BUILD)/crosscheck/`basename $$src .c`.o || exit 1; \
	done
	@echo "all app-less modules compile clean for ARM11"

# Convenience passthrough. The host tests are the ones that actually verify the crypto, so make them easy
# to reach from the directory people will be standing in.
test:
	$(MAKE) -C $(CORE)/tests

clean:
	rm -rf $(BUILD) $(TARGET).3dsx $(LINKTEST).3dsx $(DISCOVERY).3dsx $(SESSION).3dsx $(TAKION).3dsx $(CONNECT).3dsx $(MVDREPLAY).3dsx
	$(MAKE) -C $(CORE)/tests clean
