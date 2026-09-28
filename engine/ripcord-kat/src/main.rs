//! `ripcord-kat [dir]`: runs every vector file this engine has a runner for, and exits nonzero on any
//! failure, on a missing file, or if nothing was checked.

use std::path::PathBuf;
use std::process::ExitCode;

fn main() -> ExitCode {
    let dir = std::env::args_os().nth(1).map(PathBuf::from).unwrap_or_else(ripcord_kat::vector_dir);
    let mut ok = true;
    for name in ripcord_kat::FILES {
        let path = dir.join(name);
        match std::fs::read_to_string(&path) {
            Ok(text) => {
                let report = ripcord_kat::run(&text);
                ok &= report.ok();
                println!("{name}: {report}");
            }
            Err(e) => {
                ok = false;
                eprintln!("could not read {}: {e}", path.display());
            }
        }
    }
    if !ok {
        eprintln!("(generate the vectors with: dotnet run --project tools/Ripcord.ProtocolLab -- vectors)");
    }
    if ok { ExitCode::SUCCESS } else { ExitCode::FAILURE }
}
