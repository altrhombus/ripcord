# ripcord ports - host-side known-answer tests for the portable protocol core.
#
# Builds for THIS machine, not for any console: plain C99, no devkitPro, no vitasdk, no libctru, no
# handheld. The core in ../ is portable C and there is no reason to need hardware to find out it is
# wrong. These tests belong to libripcord rather than to any one port because what they check - the
# crypto, the framing, the FEC, the parsers - is the code every port shares.
#
#     make            build and run every runner
#     make build      build only
#     make compile    compile EVERY portable .c on the host, including the ones no runner links
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

CORE      := ..
REPO_ROOT := ../..

BUNDLE    := $(REPO_ROOT)/src/Ripcord.Protocol.Halyard/Data/halyard-v1-constants.json
VECTORS   := vectors/control-crypto.kat
STREAM_VECTORS := vectors/stream-crypto.kat
SESSION_VECTORS := vectors/session-crypto.kat
PROTO_VECTORS := vectors/control-proto.kat

# Phase 6a: the ECDH backend. Unlike everything else here, this one is NOT self-contained - see
# rc_ecdh.h for why the core delegates elliptic-curve arithmetic instead of implementing it.
# Two ways to get mbedtls on the host, and the local build is preferred because it makes the suite's
# central promise - "any machine with a C compiler and nothing else installed" - actually true.
# ecdh_test used to skip on any box without libmbedtls-dev, which quietly meant the P-256/P-521
# agreement and the derived stream keys went unchecked on exactly the machines least likely to notice.
#
#   ECDH_BACKEND=local   (default) build the ECP/MPI layer from a pinned, hash-verified release into
#                        build/mbedtls, using the same script and the same 2.28.8 pin as the Vita port
#   ECDH_BACKEND=mbedtls link the system libmbedcrypto instead (faster if you already have it)
#   ECDH_BACKEND=none    skip the ECDH cases entirely; ecdh_test reports a skip and exits 0
# The mbedtls rule below is the first explicit target in this file, and make takes the first target as
# the default goal. Pin it, or `make` with no arguments builds the ECDH backend and stops.
.DEFAULT_GOAL := check

ECDH_BACKEND ?= local
# Deferred (=) not immediate (:=): BUILD is defined further down this file, and an immediate expansion
# here silently yields "/mbedtls/lib/libmbedcrypto.a" at the filesystem root.
MBEDTLS_PREFIX = $(BUILD)/mbedtls

ifeq ($(ECDH_BACKEND),local)
ECDH_CFLAGS = -DRC_CRYPTO_MBEDTLS -isystem $(MBEDTLS_PREFIX)/include \
               -DMBEDTLS_CONFIG_FILE='<ripcord-mbedtls-config.h>'
ECDH_LIBS   = $(MBEDTLS_PREFIX)/lib/libmbedcrypto.a
ECDH_DEP    = $(MBEDTLS_PREFIX)/lib/libmbedcrypto.a
else ifeq ($(ECDH_BACKEND),mbedtls)
ECDH_CFLAGS := -DRC_CRYPTO_MBEDTLS
ECDH_LIBS   := -lmbedcrypto
ECDH_DEP    :=
else
ECDH_CFLAGS :=
ECDH_LIBS   :=
ECDH_DEP    :=
endif


BUILD     := build
GENERATED := $(BUILD)/halyard_v1_constants.g.c
TARGET    := $(BUILD)/vector_runner
DISCOVERY_TARGET := $(BUILD)/discovery_test
SESSION_TARGET := $(BUILD)/session_test
TAKION_TARGET := $(BUILD)/takion_test
STREAM_CRYPTO_TARGET := $(BUILD)/stream_crypto_test
FEC_TARGET := $(BUILD)/fec_test
STREAM_HEADER_TARGET := $(BUILD)/stream_header_test
STREAM_DEMUX_TARGET := $(BUILD)/stream_demux_test
ECDH_TARGET := $(BUILD)/ecdh_test
CONTROL_PROTO_TARGET := $(BUILD)/control_proto_test
INPUT_TARGET := $(BUILD)/input_test
REGISTRATION_TARGET := $(BUILD)/registration_test
REGIST_FLOW_TARGET := $(BUILD)/regist_flow_test
MENU_TARGET := $(BUILD)/menu_test
ACCOUNT_ID_TARGET := $(BUILD)/account_id_test
PAIRING_SET_TARGET := $(BUILD)/pairing_set_test
STUN_TARGET := $(BUILD)/stun_test
DGRAM_TARGET := $(BUILD)/dgram_test
WAN_TARGET := $(BUILD)/wan_test
DGRAM_VECTORS := vectors/dgram-transport.kat
CLIENT_TARGET := $(BUILD)/client_test
CONTROL_PIPE_TARGET := $(BUILD)/control_pipe_test
RENDEZVOUS_VECTORS := vectors/rendezvous-control.kat
RENDEZVOUS_TARGET := $(BUILD)/rendezvous_test
REGISTRATION_VECTORS := vectors/registration-crypto.kat
ACCOUNT_PAIRING_TARGET := $(BUILD)/account_pairing_test
ACCOUNT_PAIRING_VECTORS := vectors/account-pairing.kat

# A SECOND generated constants file, with --registration. The rest of this suite deliberately builds
# without the registration tables - that is the shape ripcord-3ds links - and checking the registration
# arithmetic needs them, so the two coexist rather than one being turned on for everybody.
REGISTRATION_GENERATED := $(BUILD)/halyard_v1_constants_reg.g.c

INCLUDES  := -I$(CORE)/crypto -I$(CORE)/halyard -I$(CORE)/platform

SOURCES := \
	$(CORE)/crypto/rc_aes.c \
	$(CORE)/crypto/rc_sha256.c \
	$(CORE)/crypto/rc_modes.c \
	$(CORE)/halyard/halyard_control_crypto.c \
	vector_runner.c

# Phase 3: the SRCH discovery parser. No vector file and no .NET codegen step - the wire format is plain
# ASCII transcribed straight from docs/protocol/ps5-local-discovery.md, so discovery_test.c's own literal
# strings ARE the vectors. See its header for why that is a legitimate substitute here and would not be
# for the crypto above.
DISCOVERY_SOURCES := \
	$(CORE)/util/rc_text.c \
	$(CORE)/discovery/halyard_discovery.c \
	$(CORE)/discovery/halyard_wake.c \
	discovery_test.c

# Phase 4: the /sess/init -> /sess/ctrl exchange, minus the crypto itself (already checked above via
# vector_runner) - see session_test.c's header for exactly what this does and does not re-verify.
SESSION_SOURCES := \
	$(CORE)/crypto/rc_aes.c \
	$(CORE)/crypto/rc_sha256.c \
	$(CORE)/crypto/rc_modes.c \
	$(CORE)/halyard/halyard_control_crypto.c \
	$(CORE)/util/rc_base64.c \
	$(CORE)/util/rc_text.c \
	$(CORE)/session/halyard_control_arm.c \
	$(CORE)/session/halyard_ctrl_message.c \
	$(CORE)/session/halyard_sess_fields.c \
	$(CORE)/session/halyard_sess_request.c \
	session_test.c

# Phase 5: the Takion transport (handshake, DATA/SACK, reassembly). No vector file and no .NET codegen
# step - the known-answer vectors are full captured packets lifted from the .NET side's own test suite,
# transcribed as literal hex strings in takion_test.c itself. Deliberately has no dependency on
# source/crypto or source/halyard: Phase 5 is pure transport, with GMAC sealing (which does need the
# stream crypto, a separate cipher from the control-plane one above) deferred to a later phase.
TAKION_SOURCES := \
	$(CORE)/takion/takion_message.c \
	$(CORE)/takion/takion_handshake.c \
	$(CORE)/takion/takion_data_chunk.c \
	$(CORE)/takion/takion_sack_chunk.c \
	$(CORE)/takion/takion_reassembler.c \
	takion_test.c

# Stream-plane crypto (GMAC via GCM, the stream KDF, per-packet nonce/tag): the crypto layer beneath the
# "stream framing, demux, FEC" backlog item. Cross-checked against tests/vectors/stream-crypto.kat
# (generated by the same `vectors` command as above, via AesGcmCore/HalyardStreamKeySchedule/
# HalyardPacketCrypto directly) rather than transcribed NIST vectors - see stream_crypto_test.c's header.
STREAM_CRYPTO_SOURCES := \
	$(CORE)/crypto/rc_aes.c \
	$(CORE)/crypto/rc_sha256.c \
	$(CORE)/crypto/rc_modes.c \
	$(CORE)/crypto/rc_gcm.c \
	$(CORE)/stream/stream_key_schedule.c \
	$(CORE)/stream/stream_packet_crypto.c \
	$(CORE)/takion/takion_control_sealer.c \
	stream_crypto_test.c

# Video FEC (GF(2^8) field + systematic Cauchy Reed-Solomon). No .NET-generated vector file - the ground
# truth here is the console's own dumped inverse table, transcribed as a literal in fec_test.c itself (see
# its header for why that is legitimate ground truth and not a self-consistency shortcut). No dependency on
# source/crypto or source/stream's crypto files: FEC is pure GF(2^8) linear algebra, independent of the
# packet crypto layered around it on the wire.
FEC_SOURCES := \
	$(CORE)/stream/fec_galois.c \
	$(CORE)/stream/fec_reed_solomon.c \
	fec_test.c

# The 18-byte A/V packet header (spec sec6.1 / HalyardStreamHeader.cs). No captured vector and no direct
# .NET unit test either (the .NET side only exercises it indirectly via the demuxer) - build/parse
# round-trip is the right substitute here, same reasoning as the Takion continuation-fragment tests.
STREAM_HEADER_SOURCES := \
	$(CORE)/stream/stream_header.c \
	stream_header_test.c

# The demux (framing + FEC recovery + video/audio/control routing). The test itself only ever exercises
# the passthrough crypto seam (stream_demux_passthrough_crypto - see stream_demux_test.c's header for why:
# no capture in the dirty room pairs A/V traffic with usable stream keys, same gap fec_test.c documents),
# but stream_demux.c also defines the real-crypto wiring (stream_demux_packet_crypto) unconditionally, so
# the link still needs stream_packet_crypto and its own dependencies even though this test never calls it.
STREAM_DEMUX_SOURCES := \
	$(CORE)/crypto/rc_aes.c \
	$(CORE)/crypto/rc_sha256.c \
	$(CORE)/crypto/rc_modes.c \
	$(CORE)/crypto/rc_gcm.c \
	$(CORE)/stream/stream_packet_crypto.c \
	$(CORE)/stream/stream_header.c \
	$(CORE)/stream/fec_galois.c \
	$(CORE)/stream/fec_reed_solomon.c \
	$(CORE)/stream/stream_demux.c \
	stream_demux_test.c

# Phase 6a: the stream key agreement (ECDH -> shared X -> per-direction keys). The only test target that
# links a third-party library; see ECDH_BACKEND above and rc_ecdh.h for the argument. rc_sha256.c comes
# along for the ecdhsig vectors (HMAC over the public key) and the stream KDF, neither of which involves
# elliptic curves at all.
ECDH_SOURCES := \
	$(CORE)/crypto/rc_sha256.c \
	$(CORE)/crypto/rc_ecdh.c \
	$(CORE)/stream/stream_key_schedule.c \
	$(CORE)/takion/takion_control_proto.c \
	$(CORE)/takion/takion_session_negotiator.c \
	ecdh_test.c

# Phase 6a: the SESSION_REQUEST/SESSION_REPLY protobuf envelope. No crypto and no EC backend - the
# vectors carry public keys and signatures as literal bytes, because what is under test here is the
# encoding, not what the values mean.
CONTROL_PROTO_SOURCES := \
	$(CORE)/util/rc_base64.c \
	$(CORE)/session/halyard_launch_spec.c \
	$(CORE)/takion/takion_control_proto.c \
	$(CORE)/takion/senkusha_echo.c \
	control_proto_test.c

# Phase 6g: the feedback packet writer. No crypto - what is under test is the byte map from spec 6.3.
INPUT_SOURCES := \
	$(CORE)/input/halyard_input.c \
	input_test.c

REGISTRATION_SOURCES := \
	$(CORE)/halyard/halyard_registration.c \
	$(CORE)/session/halyard_regist_message.c \
	$(CORE)/util/rc_base64.c \
	$(CORE)/util/rc_hex.c \
	registration_test.c

.PHONY: all check build clean constants

all: check

check: $(TARGET) $(VECTORS) $(DISCOVERY_TARGET) $(SESSION_TARGET) $(TAKION_TARGET) $(STREAM_CRYPTO_TARGET) $(STREAM_VECTORS) $(FEC_TARGET) $(STREAM_HEADER_TARGET) $(STREAM_DEMUX_TARGET) $(ECDH_TARGET) $(SESSION_VECTORS) $(CONTROL_PROTO_TARGET) $(PROTO_VECTORS) $(INPUT_TARGET) \
         $(REGISTRATION_TARGET) $(REGISTRATION_VECTORS) $(REGIST_FLOW_TARGET) $(MENU_TARGET) $(ACCOUNT_ID_TARGET) $(PAIRING_SET_TARGET) $(STUN_TARGET) \
         $(ACCOUNT_PAIRING_TARGET) $(ACCOUNT_PAIRING_VECTORS) \
         $(DGRAM_TARGET) $(DGRAM_VECTORS) $(WAN_TARGET) $(CLIENT_TARGET) $(CONTROL_PIPE_TARGET) $(RENDEZVOUS_VECTORS) \
         $(RENDEZVOUS_TARGET)
	./$(TARGET) $(VECTORS)
	./$(DISCOVERY_TARGET)
	./$(SESSION_TARGET)
	./$(TAKION_TARGET)
	./$(STREAM_CRYPTO_TARGET) $(STREAM_VECTORS)
	./$(FEC_TARGET)
	./$(STREAM_HEADER_TARGET)
	./$(STREAM_DEMUX_TARGET)
	./$(ECDH_TARGET) $(SESSION_VECTORS)
	./$(CONTROL_PROTO_TARGET) $(PROTO_VECTORS)
	./$(INPUT_TARGET)
	./$(REGISTRATION_TARGET) $(REGISTRATION_VECTORS)
	./$(REGIST_FLOW_TARGET) $(REGIST_FLOW_TARGET)
	./$(MENU_TARGET)
	./$(ACCOUNT_ID_TARGET)
	./$(PAIRING_SET_TARGET) $(BUILD)
	./$(STUN_TARGET)
	./$(ACCOUNT_PAIRING_TARGET) $(ACCOUNT_PAIRING_VECTORS)
	./$(DGRAM_TARGET) $(DGRAM_VECTORS)
	./$(WAN_TARGET)
	./$(CLIENT_TARGET)
	./$(CONTROL_PIPE_TARGET) $(RENDEZVOUS_VECTORS)
	./$(RENDEZVOUS_TARGET)

build: $(TARGET) $(DISCOVERY_TARGET) $(SESSION_TARGET) $(TAKION_TARGET) $(STREAM_CRYPTO_TARGET) $(FEC_TARGET) $(STREAM_HEADER_TARGET) $(STREAM_DEMUX_TARGET) $(ECDH_TARGET) $(CONTROL_PROTO_TARGET)

constants: $(GENERATED)

# Each recipe below creates its own output directory directly rather than depending on a separate
# directory target: with two build products both sitting under build/, an order-only prerequisite on a
# shared "build" target makes GNU Make's dependency graph see a cycle (a real one hit while adding the
# second target here) and it silently drops the mkdir side effect instead of erroring, so the link step
# fails with "No such file or directory" for a directory that was never created. `@mkdir -p $(@D)` has no
# such graph to get confused about.

# The interop constants are generated from the one committed JSON bundle rather than copied into this
# port - see tools/gen_constants.py for why that distinction matters legally as well as practically.
# Built here rather than taken from the system so the suite needs no package installed - see the
# ECDH_BACKEND block above. Must appear AFTER `BUILD :=`: make expands a rule's TARGET as it reads the
# rule, so stating it earlier silently declares /mbedtls/lib/libmbedcrypto.a at the filesystem root
# while the prerequisite resolves to build/mbedtls/..., and the two never meet.
$(MBEDTLS_PREFIX)/lib/libmbedcrypto.a: $(CORE)/tools/build-mbedtls.sh $(CORE)/tools/ripcord-mbedtls-config.h
	PREFIX=$(abspath $(MBEDTLS_PREFIX)) CROSS= sh $(CORE)/tools/build-mbedtls.sh

$(GENERATED): $(BUNDLE) $(CORE)/tools/gen_constants.py
	@mkdir -p $(@D)
	$(PYTHON) $(CORE)/tools/gen_constants.py --for 'libripcord/tests' $(BUNDLE) $@

$(TARGET): $(SOURCES) $(GENERATED)
	@mkdir -p $(@D)
	$(CC) $(CFLAGS) $(INCLUDES) -o $@ $(SOURCES) $(GENERATED)

$(DISCOVERY_TARGET): $(DISCOVERY_SOURCES)
	@mkdir -p $(@D)
	$(CC) $(CFLAGS) -o $@ $(DISCOVERY_SOURCES)

$(SESSION_TARGET): $(SESSION_SOURCES) $(GENERATED)
	@mkdir -p $(@D)
	$(CC) $(CFLAGS) $(INCLUDES) -o $@ $(SESSION_SOURCES) $(GENERATED)

$(TAKION_TARGET): $(TAKION_SOURCES)
	@mkdir -p $(@D)
	$(CC) $(CFLAGS) -o $@ $(TAKION_SOURCES)

$(STREAM_CRYPTO_TARGET): $(STREAM_CRYPTO_SOURCES)
	@mkdir -p $(@D)
	$(CC) $(CFLAGS) $(INCLUDES) -o $@ $(STREAM_CRYPTO_SOURCES)

$(FEC_TARGET): $(FEC_SOURCES)
	@mkdir -p $(@D)
	$(CC) $(CFLAGS) -o $@ $(FEC_SOURCES)

$(STREAM_HEADER_TARGET): $(STREAM_HEADER_SOURCES)
	@mkdir -p $(@D)
	$(CC) $(CFLAGS) -o $@ $(STREAM_HEADER_SOURCES)

$(STREAM_DEMUX_TARGET): $(STREAM_DEMUX_SOURCES)
	@mkdir -p $(@D)
	$(CC) $(CFLAGS) -o $@ $(STREAM_DEMUX_SOURCES)

$(ECDH_TARGET): $(ECDH_SOURCES) $(ECDH_DEP)
	@mkdir -p $(@D)
	$(CC) $(CFLAGS) $(ECDH_CFLAGS) -o $@ $(ECDH_SOURCES) $(ECDH_LIBS)

$(CONTROL_PROTO_TARGET): $(CONTROL_PROTO_SOURCES)
	@mkdir -p $(@D)
	$(CC) $(CFLAGS) -o $@ $(CONTROL_PROTO_SOURCES)

$(INPUT_TARGET): $(INPUT_SOURCES)
	@mkdir -p $(@D)
	$(CC) $(CFLAGS) -o $@ $(INPUT_SOURCES)

$(REGISTRATION_GENERATED): $(BUNDLE) $(CORE)/tools/gen_constants.py
	@mkdir -p $(@D)
	$(PYTHON) $(CORE)/tools/gen_constants.py --registration --for 'libripcord/tests' $(BUNDLE) $@

REGIST_FLOW_SOURCES := \
	$(CORE)/halyard/halyard_registration.c \
	$(CORE)/halyard/halyard_control_crypto.c \
	$(CORE)/crypto/rc_aes.c \
	$(CORE)/crypto/rc_sha256.c \
	$(CORE)/crypto/rc_modes.c \
	$(CORE)/session/halyard_regist_message.c \
	$(CORE)/session/halyard_regist_flow.c \
	$(CORE)/session/halyard_control_arm.c \
	$(CORE)/net/rc_tcp.c \
	$(CORE)/util/rc_base64.c \
	$(CORE)/util/rc_hex.c \
	rc_platform_host.c \
	regist_flow_test.c

$(REGIST_FLOW_TARGET): $(REGIST_FLOW_SOURCES) $(REGISTRATION_GENERATED)
	@mkdir -p $(@D)
	$(CC) $(CFLAGS) -D_GNU_SOURCE $(INCLUDES) -I$(CORE)/session -I$(CORE)/util -I$(CORE)/net \
	    -o $@ $(REGIST_FLOW_SOURCES) $(REGISTRATION_GENERATED) -lpthread

# The menu model. No vectors, no bundle, no sockets - it is arithmetic over a fixed array, which is
# exactly why it is here rather than inside a port where it could only be checked on a television.
MENU_SOURCES := \
	$(CORE)/ui/rc_menu.c \
	menu_test.c

$(MENU_TARGET): $(MENU_SOURCES)
	@mkdir -p $(@D)
	$(CC) $(CFLAGS) -o $@ $(MENU_SOURCES)

# Reading an account id somebody typed. Self-contained for the same reason the menu is: what it decides
# is arithmetic over a string, and the cost of it being wrong is a registration nobody can diagnose.
ACCOUNT_ID_SOURCES := \
	$(CORE)/session/halyard_account_id.c \
	account_id_test.c

$(ACCOUNT_ID_TARGET): $(ACCOUNT_ID_SOURCES)
	@mkdir -p $(@D)
	$(CC) $(CFLAGS) -o $@ $(ACCOUNT_ID_SOURCES)

# The paired-console list. Unlike the two above this one touches the filesystem, because what it has to
# prove is about a FILE - that one written before sections existed still loads, and that adding a console
# keeps the others. It is given a scratch directory to work in rather than writing beside the sources.
PAIRING_SET_SOURCES := \
	$(CORE)/session/halyard_pairing_file.c \
	$(CORE)/util/rc_hex.c \
	$(CORE)/util/rc_log.c \
	$(CORE)/util/rc_program_dir.c \
	pairing_set_test.c

$(PAIRING_SET_TARGET): $(PAIRING_SET_SOURCES)
	@mkdir -p $(@D)
	$(CC) $(CFLAGS) -I$(CORE)/util -o $@ $(PAIRING_SET_SOURCES)

# Reflexive-address discovery (STUN), both layers. The message layer is checked against the .NET suite's
# own vectors; the socket layer runs on loopback against a fake server in a second thread, which is why
# this one links the host seam and pthreads. No vectors, no bundle: STUN is public RFC material.
STUN_SOURCES := \
	$(CORE)/net/rc_stun.c \
	$(CORE)/net/rc_stun_client.c \
	$(CORE)/net/rc_tcp.c \
	rc_platform_host.c \
	stun_test.c

$(STUN_TARGET): $(STUN_SOURCES) $(CORE)/net/rc_stun.h
	@mkdir -p $(@D)
	$(CC) $(CFLAGS) -D_DEFAULT_SOURCE -I$(CORE)/platform -o $@ $(STUN_SOURCES) -lpthread

# The account route's datagram control transport (UDP 9303): framing, the association, and the pump.
# Checked against vectors/dgram-transport.kat - transcripts of the .NET association itself, generated by
# the same `vectors` command - and against the .NET suites' own synthetic cases. The pump runs against a
# scripted console in-process, plus one loopback socket.
DGRAM_SOURCES := \
	$(CORE)/session/halyard_dgram_wire.c \
	$(CORE)/session/halyard_dgram_assoc.c \
	$(CORE)/session/halyard_dgram_channel.c \
	$(CORE)/net/rc_tcp.c \
	rc_platform_host.c \
	dgram_test.c

$(DGRAM_TARGET): $(DGRAM_SOURCES) $(CORE)/session/halyard_dgram.h $(CORE)/session/halyard_dgram_channel.h
	@mkdir -p $(@D)
	$(CC) $(CFLAGS) -D_DEFAULT_SOURCE -I$(CORE)/platform -o $@ $(DGRAM_SOURCES)

# Candidate construction/selection for an internet connect, and the STUN NAT classification. The fake
# STUN servers run in threads on loopback, as stun_test's do.
WAN_SOURCES := \
	$(CORE)/session/halyard_wan_candidates.c \
	$(CORE)/net/rc_stun.c \
	$(CORE)/net/rc_stun_client.c \
	$(CORE)/net/rc_stun_mapping.c \
	$(CORE)/net/rc_tcp.c \
	rc_platform_host.c \
	wan_test.c

$(WAN_TARGET): $(WAN_SOURCES) $(CORE)/session/halyard_wan_candidates.h $(CORE)/net/rc_stun.h
	@mkdir -p $(@D)
	$(CC) $(CFLAGS) -D_DEFAULT_SOURCE -I$(CORE)/platform -o $@ $(WAN_SOURCES) -lpthread

$(DGRAM_VECTORS):
	@echo "missing $(DGRAM_VECTORS)"
	@echo "generate it from the repository root with:"
	@echo "    dotnet run --project tools/Ripcord.ProtocolLab -- vectors"
	@false

# The connect sequence (client/). Links most of the core, because that is what the sequence strings
# together, but runs none of it against a console: see client_test.c for what it can check here. rc_ecdh.c
# is built WITHOUT a backend - nothing here agrees a key - so the suite still needs no package.
CLIENT_SOURCES := \
	$(CORE)/client/halyard_client.c \
	$(CORE)/client/halyard_client_policy.c \
	$(CORE)/crypto/rc_aes.c \
	$(CORE)/crypto/rc_sha256.c \
	$(CORE)/crypto/rc_modes.c \
	$(CORE)/crypto/rc_gcm.c \
	$(CORE)/crypto/rc_ecdh.c \
	$(CORE)/halyard/halyard_control_crypto.c \
	$(CORE)/session/halyard_control_arm.c \
	$(CORE)/session/halyard_control_probe.c \
	$(CORE)/session/halyard_control_session.c \
	$(CORE)/session/halyard_ctrl_message.c \
	$(CORE)/session/halyard_dgram_wire.c \
	$(CORE)/session/halyard_dgram_assoc.c \
	$(CORE)/session/halyard_dgram_channel.c \
	$(CORE)/session/halyard_dgram_session.c \
	$(CORE)/session/halyard_wan_candidates.c \
	$(CORE)/session/halyard_launch_spec.c \
	$(CORE)/session/halyard_sess_fields.c \
	$(CORE)/session/halyard_sess_request.c \
	$(CORE)/net/rc_stun.c \
	$(CORE)/net/rc_stun_client.c \
	$(CORE)/net/rc_stun_mapping.c \
	$(CORE)/takion/takion_message.c \
	$(CORE)/takion/takion_handshake.c \
	$(CORE)/takion/takion_data_chunk.c \
	$(CORE)/takion/takion_sack_chunk.c \
	$(CORE)/takion/takion_reassembler.c \
	$(CORE)/takion/takion_reliable_channel.c \
	$(CORE)/takion/takion_control_proto.c \
	$(CORE)/takion/takion_control_sealer.c \
	$(CORE)/takion/takion_session_negotiator.c \
	$(CORE)/stream/stream_demux.c \
	$(CORE)/stream/stream_header.c \
	$(CORE)/stream/stream_packet_crypto.c \
	$(CORE)/stream/stream_key_schedule.c \
	$(CORE)/stream/fec_galois.c \
	$(CORE)/stream/fec_reed_solomon.c \
	$(CORE)/input/halyard_input.c \
	$(CORE)/net/rc_tcp.c \
	$(CORE)/net/rc_udp.c \
	$(CORE)/util/rc_base64.c \
	$(CORE)/util/rc_hex.c \
	$(CORE)/util/rc_text.c \
	$(CORE)/util/rc_log.c \
	$(CORE)/util/rc_program_dir.c \
	rc_platform_host.c \
	client_test.c

$(CLIENT_TARGET): $(CLIENT_SOURCES) $(GENERATED) $(wildcard $(CORE)/client/*.h)
	@mkdir -p $(@D)
	$(CC) $(CFLAGS) -D_DEFAULT_SOURCE $(INCLUDES) -I$(CORE)/session -I$(CORE)/util -I$(CORE)/net \
	    -o $@ $(CLIENT_SOURCES) $(GENERATED)

# The control session's byte-pipe seam, on both pipes: TCP against a loopback console in a thread (the
# ARM probe is stubbed at link time - halyard_control_probe.c is deliberately not linked - so nothing
# leaves loopback), and the datagram pipe and the registration adapter against a scripted console
# in-process. The registration build of the constants, because the adapter test runs the whole rgst flow.
CONTROL_PIPE_SOURCES := \
	$(CORE)/crypto/rc_aes.c \
	$(CORE)/crypto/rc_sha256.c \
	$(CORE)/crypto/rc_modes.c \
	$(CORE)/halyard/halyard_control_crypto.c \
	$(CORE)/halyard/halyard_registration.c \
	$(CORE)/session/halyard_account_regist.c \
	$(CORE)/session/halyard_account_regist_flow.c \
	$(CORE)/session/halyard_regist_message.c \
	$(CORE)/session/halyard_control_arm.c \
	$(CORE)/session/halyard_control_session.c \
	$(CORE)/session/halyard_ctrl_message.c \
	$(CORE)/session/halyard_dgram_wire.c \
	$(CORE)/session/halyard_dgram_assoc.c \
	$(CORE)/session/halyard_dgram_channel.c \
	$(CORE)/session/halyard_dgram_session.c \
	$(CORE)/session/halyard_wan_candidates.c \
	$(CORE)/session/halyard_sess_fields.c \
	$(CORE)/session/halyard_sess_request.c \
	$(CORE)/net/rc_stun.c \
	$(CORE)/net/rc_stun_client.c \
	$(CORE)/net/rc_stun_mapping.c \
	$(CORE)/net/rc_tcp.c \
	$(CORE)/net/rc_udp.c \
	$(CORE)/util/rc_base64.c \
	$(CORE)/util/rc_hex.c \
	$(CORE)/util/rc_text.c \
	$(CORE)/util/rc_log.c \
	$(CORE)/util/rc_program_dir.c \
	rc_platform_host.c \
	control_pipe_test.c

$(CONTROL_PIPE_TARGET): $(CONTROL_PIPE_SOURCES) $(REGISTRATION_GENERATED) fake_dgram_console.h
	@mkdir -p $(@D)
	$(CC) $(CFLAGS) -D_DEFAULT_SOURCE $(INCLUDES) -o $@ $(CONTROL_PIPE_SOURCES) $(REGISTRATION_GENERATED) -lpthread

# The rendezvous route through halyard_client, as far as it runs without a console: a fake console in a
# thread on 127.0.0.1 answers both legs, so the stage order, the media callback, the prelude, the probe
# report and the peer filter can be watched. Every socket is bound to loopback and every datagram goes
# to loopback.
RENDEZVOUS_SOURCES := $(filter-out client_test.c $(CORE)/session/halyard_control_probe.c,$(CLIENT_SOURCES)) \
	rendezvous_test.c

$(RENDEZVOUS_TARGET): $(RENDEZVOUS_SOURCES) $(GENERATED) $(wildcard $(CORE)/client/*.h) fake_dgram_console.h
	@mkdir -p $(@D)
	$(CC) $(CFLAGS) -D_DEFAULT_SOURCE $(INCLUDES) -I$(CORE)/session -I$(CORE)/util -I$(CORE)/net \
	    -o $@ $(RENDEZVOUS_SOURCES) $(GENERATED) -lpthread

$(REGISTRATION_TARGET): $(REGISTRATION_SOURCES) $(REGISTRATION_GENERATED)
	@mkdir -p $(@D)
	$(CC) $(CFLAGS) $(INCLUDES) -I$(CORE)/session -I$(CORE)/util -o $@ $(REGISTRATION_SOURCES) \
	    $(REGISTRATION_GENERATED)

# Account (no-PIN) pairing: seed recovery, the account wrap and key, and /sess/rgst request and reply,
# checked against the .NET side's vectors, then the flow against a fake console behind the transport
# callback. No sockets - the 9303 association is the caller's - so no host seam and no pthreads.
ACCOUNT_PAIRING_SOURCES := \
	$(CORE)/halyard/halyard_registration.c \
	$(CORE)/halyard/halyard_account_seed.c \
	$(CORE)/halyard/halyard_control_crypto.c \
	$(CORE)/crypto/rc_aes.c \
	$(CORE)/crypto/rc_sha256.c \
	$(CORE)/crypto/rc_modes.c \
	$(CORE)/session/halyard_regist_message.c \
	$(CORE)/session/halyard_account_regist.c \
	$(CORE)/session/halyard_account_regist_flow.c \
	$(CORE)/session/halyard_dgram_wire.c \
	$(CORE)/util/rc_base64.c \
	$(CORE)/util/rc_hex.c \
	account_pairing_test.c

$(ACCOUNT_PAIRING_TARGET): $(ACCOUNT_PAIRING_SOURCES) $(REGISTRATION_GENERATED)
	@mkdir -p $(@D)
	$(CC) $(CFLAGS) $(INCLUDES) -o $@ $(ACCOUNT_PAIRING_SOURCES) $(REGISTRATION_GENERATED)

$(RENDEZVOUS_VECTORS):
	@echo "missing $(RENDEZVOUS_VECTORS)"
	@echo "generate it from the repository root with:"
	@echo "    dotnet run --project tools/Ripcord.ProtocolLab -- vectors"
	@false

$(ACCOUNT_PAIRING_VECTORS):
	@echo "missing $(ACCOUNT_PAIRING_VECTORS)"
	@echo "generate it from the repository root with:"
	@echo "    dotnet run --project tools/Ripcord.ProtocolLab -- vectors"
	@false

# Not a rule that generates it: the emitter is a .NET project and quietly shelling out to `dotnet` from a
# C makefile would be a surprising amount of machinery to hide behind `make`. Say what to run instead.
$(VECTORS):
	@echo "missing $(VECTORS)"
	@echo "generate it from the repository root with:"
	@echo "    dotnet run --project tools/Ripcord.ProtocolLab -- vectors"
	@false

$(STREAM_VECTORS):
	@echo "missing $(STREAM_VECTORS)"
	@echo "generate it from the repository root with:"
	@echo "    dotnet run --project tools/Ripcord.ProtocolLab -- vectors"
	@false

$(SESSION_VECTORS):
	@echo "missing $(SESSION_VECTORS)"
	@echo "generate it from the repository root with:"
	@echo "    dotnet run --project tools/Ripcord.ProtocolLab -- vectors"
	@false

$(PROTO_VECTORS):
	@echo "missing $(PROTO_VECTORS)"
	@echo "generate it from the repository root with:"
	@echo "    dotnet run --project tools/Ripcord.ProtocolLab -- vectors"
	@false

clean:
	rm -rf $(BUILD)

# Compile every portable translation unit on the host, including the ones no test runner links.
#
# The runners deliberately stop at the socket boundary, which used to leave a handful of files -
# net/rc_tcp.c, session/halyard_control_session.c, takion/takion_reliable_channel.c, and the plumbing in
# util/ - compiled by the cross-compiler and by nothing else. That is precisely the set where a
# platform assumption hides, and it was invisible to `make` until the core was pulled out of the 3DS
# tree and this target was written.
#
# _DEFAULT_SOURCE is needed for inet_aton(), which is a BSD extension rather than C99 and is NOT
# guaranteed to exist on every target - vitasdk's documented address helper is sceNetInetPton. Two call
# sites use it (rc_tcp.c, halyard_control_session.c) and they are a known portability question, not a
# settled one.
COMPILE_ALL := $(shell find $(CORE) -name '*.c' -not -path '*/tests/*' | sort)

.PHONY: compile
compile:
	@mkdir -p $(BUILD)/compile
	@for src in $(COMPILE_ALL); do \
		printf '  CC %s\n' "$$src"; \
		$(CC) $(CFLAGS) -D_DEFAULT_SOURCE $(INCLUDES) -c $$src -o $(BUILD)/compile/`basename $$src .c`.o || exit 1; \
	done
	@echo "the entire portable core compiles on the host"

# Fuzzing the parsers that face the network. See fuzz/ for the harnesses and what each one reaches.
#
# WHY THIS EXISTS. The core parses bytes from the LAN and from a console, much of it before anything is
# authenticated: discovery replies from whatever answers a broadcast, the Takion handshake, SACK, DATA
# and reassembly, and stream headers. It is C, so a length it trusts is a memory-safety bug rather than
# an exception. The macOS client links this core directly, which is what made that worth guarding
# rather than assuming, and the console ports benefit from the same guard.
#
# TWO MODES, because Apple's clang ships the sanitizers but not libFuzzer:
#
#   make fuzz-replay   any compiler. Every harness under ASan+UBSan, driven by fuzz/replay_main.c with
#                      FUZZ_RANDOM deterministic inputs. Proves the harnesses are sound on every host,
#                      and replays a crash file:  build/fuzz-replay/fuzz_takion crash-<hash>
#   make fuzz-run      a clang with libFuzzer (Linux clang, or Homebrew's llvm on a Mac). Coverage-guided,
#                      FUZZ_SECONDS per harness, keeping each harness's corpus in build/fuzz/corpus/.
#
# The whole core is archived and each harness links against the archive, so a harness pulls in only
# the objects it references, exactly as a port does.
FUZZ_HARNESSES := discovery takion control stream stun account dgram wan rendezvous
FUZZ_CC        ?= clang
FUZZ_SECONDS   ?= 60
FUZZ_RANDOM    ?= 20000
FUZZ_SAN       := -O1 -g -fsanitize=address,undefined -fno-sanitize-recover=all -fno-omit-frame-pointer
FUZZ_WARN      := -std=c99 -Wall -Wextra -Werror -Wshadow -Wconversion -Wsign-conversion
# The registration build of the constants, so the account harness can link the registration code it
# reaches. A superset of the control-only file: every other harness sees the same control constants.
FUZZ_CORE_SRCS  = $(COMPILE_ALL) $(REGISTRATION_GENERATED) rc_platform_host.c

# $(1) = build directory, $(2) = compiler, $(3) = extra flags for the core objects
define fuzz_build_core
	@mkdir -p $(1)/obj
	@for src in $(FUZZ_CORE_SRCS); do \
		$(2) $(FUZZ_WARN) $(FUZZ_SAN) $(3) -D_DEFAULT_SOURCE $(INCLUDES) -c $$src \
			-o $(1)/obj/`basename $$src .c`.o || exit 1; \
	done
	@rm -f $(1)/libripcord-fuzz.a
	@ar rcs $(1)/libripcord-fuzz.a $(1)/obj/*.o
endef

.PHONY: fuzz-replay fuzz-run

fuzz-replay: $(REGISTRATION_GENERATED)
	$(call fuzz_build_core,$(BUILD)/fuzz-replay,$(CC),)
	@for h in $(FUZZ_HARNESSES); do \
		$(CC) $(FUZZ_WARN) $(FUZZ_SAN) -D_DEFAULT_SOURCE $(INCLUDES) -o $(BUILD)/fuzz-replay/fuzz_$$h \
			fuzz/fuzz_$$h.c fuzz/replay_main.c $(BUILD)/fuzz-replay/libripcord-fuzz.a || exit 1; \
		./$(BUILD)/fuzz-replay/fuzz_$$h -random=$(FUZZ_RANDOM) || exit 1; \
	done

# The core is instrumented for coverage (-fsanitize=fuzzer-no-link) but only the harness links
# libFuzzer's main, so the archive stays usable by either kind of link.
fuzz-run: $(REGISTRATION_GENERATED)
	$(call fuzz_build_core,$(BUILD)/fuzz,$(FUZZ_CC),-fsanitize=fuzzer-no-link)
	@for h in $(FUZZ_HARNESSES); do \
		mkdir -p $(BUILD)/fuzz/corpus/$$h; \
		$(FUZZ_CC) $(FUZZ_WARN) $(FUZZ_SAN) -fsanitize=fuzzer -D_DEFAULT_SOURCE $(INCLUDES) \
			-o $(BUILD)/fuzz/fuzz_$$h fuzz/fuzz_$$h.c $(BUILD)/fuzz/libripcord-fuzz.a || exit 1; \
		( cd $(BUILD)/fuzz && ./fuzz_$$h -max_total_time=$(FUZZ_SECONDS) -print_final_stats=1 corpus/$$h ) || exit 1; \
	done
