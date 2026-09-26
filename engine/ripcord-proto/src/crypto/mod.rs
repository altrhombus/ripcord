//! Primitives the protocol layers build on: the keystream cipher modes the control plane needs, and the
//! key-agreement seam. Symmetric primitives are RustCrypto's; key agreement is a trait the host backs
//! with its platform's implementation (docs/engine-plan.md, "Dependencies").

pub mod ecdh;
pub mod modes;
