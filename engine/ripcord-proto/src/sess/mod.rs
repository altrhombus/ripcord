//! The /sess control plane: /sess/rgst (PIN and account registration), /sess/init, /sess/ctrl with its
//! encrypted fields, the launch spec, and the binary control channel's frames. Ported from
//! `libripcord/session/` and cross-checked against the .NET reference, which wins where they differ
//! unless C is strictly safer (`engine/README.md`). Messages only; the stateful exchanges are sans-IO
//! machines above these.

pub mod account_id;
pub mod ctrl;
pub mod fields;
pub mod http;
pub mod launch_spec;
pub mod regist;
pub mod requests;
pub mod session;
