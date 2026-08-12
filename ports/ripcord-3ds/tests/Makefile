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

INCLUDES  := -I$(PORT_ROOT)/source/crypto -I$(PORT_ROOT)/source/halyard

SOURCES := \
	$(PORT_ROOT)/source/crypto/rc_aes.c \
	$(PORT_ROOT)/source/crypto/rc_sha256.c \
	$(PORT_ROOT)/source/crypto/rc_modes.c \
	$(PORT_ROOT)/source/halyard/halyard_control_crypto.c \
	vector_runner.c

.PHONY: all check build clean constants

all: check

check: $(TARGET) $(VECTORS)
	./$(TARGET) $(VECTORS)

build: $(TARGET)

constants: $(GENERATED)

$(BUILD):
	mkdir -p $(BUILD)

# The interop constants are generated from the one committed JSON bundle rather than copied into this
# port - see tools/gen_constants.py for why that distinction matters legally as well as practically.
$(GENERATED): $(BUNDLE) $(PORT_ROOT)/tools/gen_constants.py | $(BUILD)
	$(PYTHON) $(PORT_ROOT)/tools/gen_constants.py $(BUNDLE) $@

$(TARGET): $(SOURCES) $(GENERATED) | $(BUILD)
	$(CC) $(CFLAGS) $(INCLUDES) -o $@ $(SOURCES) $(GENERATED)

# Not a rule that generates it: the emitter is a .NET project and quietly shelling out to `dotnet` from a
# C makefile would be a surprising amount of machinery to hide behind `make`. Say what to run instead.
$(VECTORS):
	@echo "missing $(VECTORS)"
	@echo "generate it from the repository root with:"
	@echo "    dotnet run --project tools/Ripcord.ProtocolLab -- vectors"
	@false

clean:
	rm -rf $(BUILD)
