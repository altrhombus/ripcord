//! The generated vectors, when this checkout has them. A clean checkout does not (they are never
//! committed), so a missing file skips with a message rather than fails: the same rule as the .NET
//! suite's capture-backed tests.

fn run_file(name: &str) {
    let path = ripcord_kat::vector_dir().join(name);
    let Ok(text) = std::fs::read_to_string(&path) else {
        eprintln!(
            "SKIP: {} not present; run `dotnet run --project tools/Ripcord.ProtocolLab -- vectors`",
            path.display()
        );
        return;
    };
    let report = ripcord_kat::run(&text);
    assert!(report.ok(), "{name}: {report}");
    eprintln!("{name}: {report}");
}

#[test]
fn stream_crypto() {
    run_file("stream-crypto.kat");
}

#[test]
fn control_crypto() {
    run_file("control-crypto.kat");
}

#[test]
fn registration_crypto() {
    run_file("registration-crypto.kat");
}

#[test]
fn account_pairing() {
    run_file("account-pairing.kat");
}

#[test]
fn session_crypto() {
    run_file("session-crypto.kat");
}

#[test]
fn control_proto() {
    run_file("control-proto.kat");
}

#[test]
fn rendezvous_control() {
    run_file("rendezvous-control.kat");
}

#[test]
fn dgram_transport() {
    run_file("dgram-transport.kat");
}

#[test]
fn every_file_has_a_test() {
    assert_eq!(ripcord_kat::FILES.len(), 8, "a file added to FILES needs a test above");
}

#[test]
fn an_unknown_line_kind_fails() {
    let report = ripcord_kat::run("version 1\nnewkind 00 11\n");
    assert!(!report.ok());
}
