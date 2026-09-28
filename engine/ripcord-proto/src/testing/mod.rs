//! Test support: scripted peers that stand in for a console. Compiled for this crate's own tests and,
//! behind the `scripted-console` feature, for other crates' (`ripcord-ffi` exports it to the host test
//! suites under its `test-support` feature). Never part of a shipping engine.

pub mod scripted_console;
pub mod scripted_lan_console;
