//! The generated vectors, when this checkout has them. A clean checkout does not (they are never
//! committed), so these skip with a message rather than fail: the same rule as the .NET suite's
//! capture-backed tests.

#[test]
fn stream_crypto() {
    let path = ripcord_kat::vector_dir().join("stream-crypto.kat");
    let Ok(text) = std::fs::read_to_string(&path) else {
        eprintln!(
            "SKIP: {} not present; run `dotnet run --project tools/Ripcord.ProtocolLab -- vectors`",
            path.display()
        );
        return;
    };
    let report = ripcord_kat::run_stream_crypto(&text);
    assert!(report.ok(), "{report}");
    eprintln!("stream-crypto.kat: {report}");
}

#[test]
fn an_unknown_line_kind_fails() {
    let report = ripcord_kat::run_stream_crypto("version 1\nnewkind 00 11\n");
    assert!(!report.ok());
}
