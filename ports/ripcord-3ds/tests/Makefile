# ripcord-3ds - host-side known-answer tests.
#
# Builds for THIS machine, not the 3DS: plain C99, no devkitPro, no libctru, no console. The crypto in
# source/ is portable C and there is no reason to need a handheld to find out it is wrong.
#
#     make            build and run the vector runner
#     make build      build only
#     make clean
#
# The vector file itself comes from the .NET side and is not checked in:
#
#     dotnet run --project tools/Ripcord.ProtocolLab -- vectors
#
# Override the compiler the usual way (CC=clang, CC=gcc-13, ...).

CC      ?= cc
PYTHON  ?= python3

# -Werror on purpose. This is a small, self-contained tree with no third-party sources compiled into it,
# so there is no legitimate warning to tolerate, and the target audience is a 128 MB handheld where an
# ignored -Wconversion is a real bug rather than a style note.
CFLAGS  ?= -std=c99 -O2 -g -Wall -Wextra -Werror -Wshadow -Wconversion -Wsign-conversion

PORT_ROOT := ..
REPO_ROOT := ../../..

BUNDLE    := $(REPO_ROOT)/src/Ripcord.Protocol.Halyard/Data/halyard-v1-constants.json
VECTORS   := vectors/control-crypto.kat

BUILD     := build
GENERATED := $(BUILD)/halyard_v1_constants.g.c
TARGET    := $(BUILD)/vector_runner
DISCOVERY_TARGET := $(BUILD)/discovery_test

INCLUDES  := -I$(PORT_ROOT)/source/crypto -I$(PORT_ROOT)/source/halyard

SOURCES := \
	$(PORT_ROOT)/source/crypto/rc_aes.c \
	$(PORT_ROOT)/source/crypto/rc_sha256.c \
	$(PORT_ROOT)/source/crypto/rc_modes.c \
	$(PORT_ROOT)/source/halyard/halyard_control_crypto.c \
	vector_runner.c

# Phase 3: the SRCH discovery parser. No vector file and no .NET codegen step - the wire format is plain
# ASCII transcribed straight from docs/protocol/ps5-local-discovery.md, so discovery_test.c's own literal
# strings ARE the vectors. See its header for why that is a legitimate substitute here and would not be
# for the crypto above.
DISCOVERY_SOURCES := \
	$(PORT_ROOT)/source/discovery/halyard_discovery.c \
	discovery_test.c

.PHONY: all check build clean constants

all: check

check: $(TARGET) $(VECTORS) $(DISCOVERY_TARGET)
	./$(TARGET) $(VECTORS)
	./$(DISCOVERY_TARGET)

build: $(TARGET) $(DISCOVERY_TARGET)

constants: $(GENERATED)

# Each recipe below creates its own output directory directly rather than depending on a separate
# directory target: with two build products both sitting under build/, an order-only prerequisite on a
# shared "build" target makes GNU Make's dependency graph see a cycle (a real one hit while adding the
# second target here) and it silently drops the mkdir side effect instead of erroring, so the link step
# fails with "No such file or directory" for a directory that was never created. `@mkdir -p $(@D)` has no
# such graph to get confused about.

# The interop constants are generated from the one committed JSON bundle rather than copied into this
# port - see tools/gen_constants.py for why that distinction matters legally as well as practically.
$(GENERATED): $(BUNDLE) $(PORT_ROOT)/tools/gen_constants.py
	@mkdir -p $(@D)
	$(PYTHON) $(PORT_ROOT)/tools/gen_constants.py $(BUNDLE) $@

$(TARGET): $(SOURCES) $(GENERATED)
	@mkdir -p $(@D)
	$(CC) $(CFLAGS) $(INCLUDES) -o $@ $(SOURCES) $(GENERATED)

$(DISCOVERY_TARGET): $(DISCOVERY_SOURCES)
	@mkdir -p $(@D)
	$(CC) $(CFLAGS) -o $@ $(DISCOVERY_SOURCES)

# Not a rule that generates it: the emitter is a .NET project and quietly shelling out to `dotnet` from a
# C makefile would be a surprising amount of machinery to hide behind `make`. Say what to run instead.
$(VECTORS):
	@echo "missing $(VECTORS)"
	@echo "generate it from the repository root with:"
	@echo "    dotnet run --project tools/Ripcord.ProtocolLab -- vectors"
	@false

clean:
	rm -rf $(BUILD)
