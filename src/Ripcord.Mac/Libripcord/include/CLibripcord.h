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

#endif /* CLIBRIPCORD_H */
