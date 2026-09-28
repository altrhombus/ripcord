//! Takion, the SCTP-over-UDP transport. The name is the vendor's own codename, retained as protocol
//! terminology (CLAUDE.md, "Naming"). Ported from `libripcord/takion/`, cross-checked against the .NET
//! reference in `src/Ripcord.Protocol.Halyard.Takion/`, which wins where the two differ.

pub mod chunks;
pub mod connection;
pub mod control;
pub mod negotiator;
pub mod sealer;
pub mod senkusha;
