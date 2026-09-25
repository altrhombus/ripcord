//! `ripcord-kat [dir]`: runs every vector file this engine has a runner for, and exits nonzero on any
//! failure or if nothing was checked.

use std::path::PathBuf;
use std::process::ExitCode;

fn main() -> ExitCode {
    let dir = std::env::args_os().nth(1).map(PathBuf::from).unwrap_or_else(ripcord_kat::vector_dir);
    let path = dir.join("stream-crypto.kat");
    let text = match std::fs::read_to_string(&path) {
        Ok(text) => text,
        Err(e) => {
            eprintln!("could not read {}: {e}", path.display());
            eprintln!("generate the vectors with: dotnet run --project tools/Ripcord.ProtocolLab -- vectors");
            return ExitCode::FAILURE;
        }
    };
    let report = ripcord_kat::run_stream_crypto(&text);
    println!("{}: {report}", path.display());
    if report.ok() { ExitCode::SUCCESS } else { ExitCode::FAILURE }
}
