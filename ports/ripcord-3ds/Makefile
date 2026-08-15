# ripcord-3ds - the 3DS build.
#
# Produces ripcord-3ds.3dsx (Homebrew Launcher). Requires devkitPro with devkitARM and libctru:
# https://devkitpro.org/wiki/Getting_Started. On Windows that means running this from the devkitPro MSYS2
# shell, which is where `make` and the ARM toolchain live.
#
#     make                build every .3dsx
#     make constants      regenerate the interop constants only
#     make crosscheck     compile the modules that have no .3dsx of their own yet (see CROSSCHECK_SOURCES)
#     make clean
#     make test           run the HOST-side known-answer tests (no 3DS involved) - see tests/Makefile
#
# NOT IN THE SOLUTION FILE, AND NOT BUILDABLE BY dotnet. Ripcord.slnx is MSBuild driving .NET and
# C++/WinRT; this is a Makefile driving a cross-compiler for a different CPU and a different OS entirely.
# Sharing a repository with Ripcord buys shared specs, shared constants and shared test vectors - it does
# not and should not mean sharing a build system.
#
# STATUS: these flags are known-good, not provisional - all five .3dsx targets build against a real
# devkitPro installation, and two of them (ripcord-3ds, ripcord-3ds-linktest) have booted on real New 3DS
# hardware. What each program has and has not been RUN against is tracked in README.md, not here.

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
# abort writing 12 bytes below SP, with a log file that stopped after the banner and gave no hint which
# function was even entered. Diagnosing that from the dump took considerably longer than this flag.
#
# 8 KB per frame leaves room for a reasonable call depth inside 32 KB. Anything bigger belongs in .bss
# (file-scope or `static`) or on the heap; with -Werror below, exceeding it fails the build instead of
# the handheld.
CFLAGS  := $(ARCH) -std=c99 -O2 -g \
           -Wall -Wextra -Werror -Wshadow -Wconversion -Wsign-conversion \
           -Wframe-larger-than=8192 \
           -ffunction-sections -fdata-sections \
           -D__3DS__ \
           -isystem $(CTRULIB)/include -Isource/crypto -Isource/halyard

LDFLAGS := $(ARCH) -specs=3dsx.specs -Wl,--gc-sections -L$(CTRULIB)/lib
LIBS    := -lctru -lm

# Phase 6a: the ECDH backend. The only third-party library this port links, and the only primitive it
# does not implement itself - see source/crypto/rc_ecdh.h for that argument. Install with:
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
	source/session/halyard_pairing_file.c \
	source/session/halyard_sess_fields.c \
	source/session/halyard_sess_request.c \
	source/session/main.c

# Phase 5 (SETUP.md): the Takion transport (handshake, DATA/SACK, reassembly). Deliberately has no
# dependency on source/crypto or source/halyard - GMAC sealing needs the stream cipher, a separate,
# later phase; this is pure transport.
TAKION_SOURCES := \
	source/net/rc_soc.c \
	source/util/rc_log.c \
	source/util/rc_program_dir.c \
	source/takion/takion_message.c \
	source/takion/takion_handshake.c \
	source/takion/takion_data_chunk.c \
	source/takion/takion_sack_chunk.c \
	source/takion/takion_reassembler.c \
	source/takion/takion_reliable_channel.c \
	source/takion/main.c

# Phase 6b: the combined connect flow - the only program that carries a real control session all the way
# to derived stream keys, and therefore the only way to test Takion or the ECDH agreement against a
# console at all (see source/connect/main.c on why the standalone Takion probe cannot work). This is the
# first target to link BOTH the control plane and the stream plane, and the only one needing mbedtls.
CONNECT_SOURCES := \
	source/crypto/rc_aes.c \
	source/crypto/rc_sha256.c \
	source/crypto/rc_modes.c \
	source/crypto/rc_ecdh.c \
	source/halyard/halyard_control_crypto.c \
	source/net/rc_soc.c \
	source/net/rc_tcp.c \
	source/crypto/rc_gcm.c \
	source/stream/stream_key_schedule.c \
	source/stream/stream_demux.c \
	source/stream/fec_galois.c \
	source/stream/fec_reed_solomon.c \
	source/stream/stream_header.c \
	source/stream/stream_packet_crypto.c \
	source/util/rc_log.c \
	source/util/rc_base64.c \
	source/util/rc_hex.c \
	source/util/rc_program_dir.c \
	source/util/rc_profile.c \
	source/util/rc_random.c \
	source/util/rc_text.c \
	source/session/halyard_control_arm.c \
	source/session/halyard_control_session.c \
	source/session/halyard_ctrl_message.c \
	source/session/halyard_launch_spec.c \
	source/session/halyard_pairing_file.c \
	source/session/halyard_sess_fields.c \
	source/session/halyard_sess_request.c \
	source/takion/takion_message.c \
	source/takion/takion_handshake.c \
	source/takion/takion_data_chunk.c \
	source/takion/takion_sack_chunk.c \
	source/takion/takion_reassembler.c \
	source/takion/takion_reliable_channel.c \
	source/takion/takion_control_proto.c \
	source/takion/senkusha_echo.c \
	source/input/halyard_input.c \
	source/takion/takion_session_negotiator.c \
	source/media/rc_mvd.c \
	source/media/rc_audio.c \
	source/connect/main.c

# Modules that have no on-device app of their own yet: Phase 5.5's stream framing/FEC/demux (nothing real
# to decrypt until a stream key exists) and Phase 6a's ECDH seam + control-plane protobuf (no connect
# flow wired through them yet). Both phases are documented as "cross-compiles clean for ARM11", and this
# target is what makes that claim checkable rather than remembered - it compiles each to an object and
# links nothing, which is exactly as far as the claim goes.
CROSSCHECK_SOURCES := \
	source/stream/stream_demux.c \
	source/stream/fec_galois.c \
	source/stream/fec_reed_solomon.c \
	source/stream/stream_header.c \
	source/stream/fec_galois.c \
	source/stream/fec_reed_solomon.c \
	source/stream/stream_key_schedule.c \
	source/stream/stream_demux.c \
	source/stream/fec_galois.c \
	source/stream/fec_reed_solomon.c \
	source/stream/stream_header.c \
	source/stream/stream_packet_crypto.c \
	source/stream/stream_demux.c \
	source/crypto/rc_ecdh.c \
	source/takion/takion_control_proto.c \
	source/takion/senkusha_echo.c \
	source/input/halyard_input.c \
	source/takion/takion_session_negotiator.c

.PHONY: all constants clean test linktest discovery session takion connect crosscheck

all: $(TARGET).3dsx $(LINKTEST).3dsx $(DISCOVERY).3dsx $(SESSION).3dsx $(TAKION).3dsx $(CONNECT).3dsx $(MVDREPLAY).3dsx

linktest: $(LINKTEST).3dsx

discovery: $(DISCOVERY).3dsx

session: $(SESSION).3dsx

takion: $(TAKION).3dsx

connect: $(CONNECT).3dsx

mvdreplay: $(MVDREPLAY).3dsx

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
	source/util/rc_log.c \
	source/util/rc_program_dir.c \
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
	$(MAKE) -C tests

clean:
	rm -rf $(BUILD) $(TARGET).3dsx $(LINKTEST).3dsx $(DISCOVERY).3dsx $(SESSION).3dsx $(TAKION).3dsx $(CONNECT).3dsx $(MVDREPLAY).3dsx
	$(MAKE) -C tests clean
