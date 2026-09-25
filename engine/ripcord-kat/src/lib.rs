//! Runs the `.kat` files `dotnet run --project tools/Ripcord.ProtocolLab -- vectors` generates, unchanged,
//! against `ripcord-proto`. Every answer in a file was computed by the .NET reference; every answer here
//! is computed by the Rust engine. The format is `libripcord/tests/vector_runner.c`'s: flat text, one
//! vector per line, fields split on whitespace, `#` comments, and `-` for an empty hex field.
//!
//! The files are never committed (they are derived from the bundled interop constants; see
//! `libripcord/README.md`), so a clean checkout has none and the tests that read them skip.
//!
//! Unlike the C runners, a line kind this runner does not know is a failure rather than a skip: a new
//! kind from the generator should not pass here unchecked.
#![forbid(unsafe_code)]

use std::fmt;
use std::path::{Path, PathBuf};

use ripcord_proto::stream::{key_schedule, packet_crypto};

#[derive(Default)]
pub struct Report {
    pub passed: usize,
    pub failures: Vec<String>,
}

impl Report {
    pub fn ok(&self) -> bool {
        self.failures.is_empty() && self.passed > 0
    }
}

impl fmt::Display for Report {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        for failure in &self.failures {
            writeln!(f, "FAIL {failure}")?;
        }
        write!(f, "{} passed, {} failed", self.passed, self.failures.len())
    }
}

/// Where the generated vectors live: `RIPCORD_KAT_DIR` if set, else `libripcord/tests/vectors/`,
/// where the generator writes them by default.
pub fn vector_dir() -> PathBuf {
    std::env::var_os("RIPCORD_KAT_DIR")
        .map(PathBuf::from)
        .unwrap_or_else(|| Path::new(env!("CARGO_MANIFEST_DIR")).join("../../libripcord/tests/vectors"))
}

fn hex(field: &str) -> Result<Vec<u8>, String> {
    if field == "-" {
        return Ok(Vec::new());
    }
    if !field.len().is_multiple_of(2) {
        return Err(format!("odd-length hex {field:?}"));
    }
    (0..field.len())
        .step_by(2)
        .map(|i| u8::from_str_radix(&field[i..i + 2], 16).map_err(|_| format!("bad hex {field:?}")))
        .collect()
}

fn hex16(field: &str) -> Result<[u8; 16], String> {
    hex(field)?.try_into().map_err(|_| format!("{field:?} is not 16 bytes"))
}

fn to_hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}

fn number<T: std::str::FromStr>(field: &str) -> Result<T, String> {
    field.parse().map_err(|_| format!("bad number {field:?}"))
}

struct Line<'a> {
    kind: &'a str,
    number: usize,
    fields: Vec<&'a str>,
}

impl Line<'_> {
    fn field(&self, i: usize) -> Result<&str, String> {
        self.fields.get(i).copied().ok_or_else(|| format!("missing field {i}"))
    }
}

fn check(report: &mut Report, line: &Line<'_>, label: &str, actual: &[u8], expect_hex: &str) {
    let got = to_hex(actual);
    if got == expect_hex {
        report.passed += 1;
    } else {
        report
            .failures
            .push(format!("{} line {}: {label}\n  got  {got}\n  want {expect_hex}", line.kind, line.number));
    }
}

fn run_stream_line(report: &mut Report, line: &Line<'_>) -> Result<(), String> {
    match line.kind {
        "gmac" => {
            let tag =
                packet_crypto::gmac(&hex16(line.field(1)?)?, &hex16(line.field(2)?)?, &hex(line.field(3)?)?);
            check(report, line, "tag", &tag, line.field(4)?);
        }
        "streamkdf" => {
            let keys = key_schedule::derive_direction(
                &hex(line.field(1)?)?,
                &hex16(line.field(2)?)?,
                number(line.field(3)?)?,
            );
            check(report, line, "aesKey", &keys.aes_key, line.field(4)?);
            check(report, line, "baseIv", &keys.base_iv, line.field(5)?);
        }
        "packetnonce" => {
            let c = packet_crypto::PacketCrypto::new(&hex16(line.field(1)?)?, &hex16(line.field(2)?)?);
            let key_pos: u64 = number(line.field(3)?)?;
            check(report, line, "gmacNonce", &c.gmac_nonce(key_pos), line.field(4)?);
            check(report, line, "ctrNonce", &c.ctr_nonce(key_pos), line.field(5)?);
            check(report, line, "gmacKey", &c.gmac_key(key_pos), line.field(6)?);
        }
        "packettag" => {
            let mut c = packet_crypto::PacketCrypto::new(&hex16(line.field(1)?)?, &hex16(line.field(2)?)?);
            let key_pos: u64 = number(line.field(3)?)?;
            let zero_key_pos = number::<u8>(line.field(4)?)? != 0;
            let tag_offset: usize = number(line.field(5)?)?;
            let tag = c
                .compute_tag(key_pos, &hex(line.field(6)?)?, tag_offset, zero_key_pos)
                .ok_or("compute_tag refused the packet")?;
            check(report, line, "tag", &tag, line.field(7)?);
        }
        other => return Err(format!("no runner for line kind {other:?}")),
    }
    Ok(())
}

/// Runs every line of a stream-plane file (`stream-crypto.kat`).
pub fn run_stream_crypto(text: &str) -> Report {
    let mut report = Report::default();
    for (i, raw) in text.lines().enumerate() {
        let fields: Vec<&str> = raw.split_whitespace().collect();
        let Some(&kind) = fields.first() else { continue };
        if kind.starts_with('#') || kind == "version" {
            continue;
        }
        let line = Line { kind, number: i + 1, fields };
        if let Err(e) = run_stream_line(&mut report, &line) {
            report.failures.push(format!("{} line {}: {e}", line.kind, line.number));
        }
    }
    report
}
