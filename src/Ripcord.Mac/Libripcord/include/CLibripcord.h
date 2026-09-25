/*
 * ripcord for Mac - the parts of libripcord Swift reaches, in one header.
 *
 * The paths climb out of the Mac tree on purpose: the core is not copied here, it is compiled from
 * where it lives (the Libripcord target in Ripcord.xcodeproj), and this header is only the list of what
 * RipcordKit calls. Add to it when RipcordKit needs more, not before.
 */
#ifndef CLIBRIPCORD_H
#define CLIBRIPCORD_H

/* The Libripcord target compiles the core with this defined (Config/Libripcord.xcconfig). Swift sees the
 * core through this header instead, so it is stated here as well, or the backend hooks in rc_ecdh.h that
 * CryptoKitECDH.swift reports through would be invisible to it. */
#ifndef RC_ECDH_EXTERNAL_BACKEND
#define RC_ECDH_EXTERNAL_BACKEND 1
#endif

#include "../../../../libripcord/platform/rc_platform.h"
#include "../../../../libripcord/util/rc_random.h"
#include "../../../../libripcord/crypto/rc_ecdh.h"
#include "../../../../libripcord/discovery/halyard_discovery.h"
#include "../../../../libripcord/discovery/halyard_wake.h"
#include "../../../../libripcord/halyard/halyard_v1.h"
#include "../../../../libripcord/stream/stream_packet_crypto.h"
#include "../../../../libripcord/input/halyard_input.h"
#include "../../../../libripcord/session/halyard_account_id.h"
#include "../../../../libripcord/session/halyard_pairing_file.h"
#include "../../../../libripcord/session/halyard_regist_flow.h"
#include "../../../../libripcord/client/halyard_client.h"

/* The rendezvous route, for RipcordKit/Cloud/Rendezvous/LibripcordTransports.swift and ConsoleSession: the
 * account seed (data1/data2 out, customData1 back), /sess/rgst over the 9303 association, the association
 * itself as a standalone leg (account pairing has no halyard_client), the candidate rules and STUN. */
#include "../../../../libripcord/net/rc_stun.h"
#include "../../../../libripcord/halyard/halyard_account_seed.h"
#include "../../../../libripcord/session/halyard_account_regist_flow.h"
#include "../../../../libripcord/session/halyard_dgram_session.h"
#include "../../../../libripcord/session/halyard_wan_candidates.h"

#endif /* CLIBRIPCORD_H */
