//! The account route's datagram control transport (UDP 9303), which is not Takion and shares no bytes with
//! it. Ported from `libripcord/session/halyard_dgram*`, itself ported from
//! `Ripcord.Protocol.Halyard.Common/Control/`; `docs/protocol/ps5-session-transport.md` is the spec.
//!
//! So far this holds the wire layer: the 88-byte prelude, the chunk codec and HTTP completeness. The
//! association and its pump arrive with the 9303 layer.

pub mod wire;
