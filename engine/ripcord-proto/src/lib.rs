//! The Halyard protocol as pure state machines.
//!
//! **Sans-IO**: nothing in this crate owns a socket, reads a clock or starts a thread. A host hands it
//! bytes and the current time, and gets back bytes to send, frames and events. See
//! `docs/engine-plan.md` for why that shape was chosen on its own merits.
//!
//! Ported from `libripcord` (this project's own C core), which is itself a port of the .NET reference
//! in `src/Ripcord.Protocol.Halyard*`. The .NET side stays the reference for derivations, and
//! `ProtocolLab vectors` generates the known-answer files `ripcord-kat` checks this crate against.
#![forbid(unsafe_code)]

pub mod base64;
pub mod crypto;
pub mod dgram;
pub mod discovery;
pub mod halyard;
pub mod sess;
pub mod stream;
pub mod takion;
#[cfg(any(test, feature = "scripted-console"))]
pub mod testing;
