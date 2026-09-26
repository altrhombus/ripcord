//! The account route's datagram control transport (UDP 9303), which is not Takion and shares no bytes with
//! it. Ported from `libripcord/session/halyard_dgram*`, itself ported from
//! `Ripcord.Protocol.Halyard.Common/Control/`; `docs/protocol/ps5-session-transport.md` is the spec.
//!
//! The wire layer (the 88-byte prelude, the chunk codec, HTTP completeness) and the association.

pub mod assoc;
pub mod channel;
pub mod rendezvous;
pub mod wire;
