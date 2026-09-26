//! Runs the `.kat` files `dotnet run --project tools/Ripcord.ProtocolLab -- vectors` generates, unchanged,
//! against `ripcord-proto`. Every answer in a file was computed by the .NET reference; every answer here
//! is computed by the Rust engine. The format is `libripcord/tests/vector_runner.c`'s: flat text, one
//! vector per line, fields split on whitespace, `#` comments, and `-` for an empty hex field.
//!
//! The files are never committed (they are derived from the bundled interop constants; see
//! `libripcord/README.md`), so a clean checkout has none and the tests that read them skip.
//!
//! Unlike the C runners, a line kind this runner does not know is a failure rather than a skip: a new kind
//! from the generator should not pass here unchecked. A kind whose layer has not been ported yet is listed
//! in [`DEFERRED`] with the reason, counted, and reported, so the gap is visible in every run.
#![forbid(unsafe_code)]

use std::collections::BTreeMap;
use std::fmt;
use std::path::{Path, PathBuf};

use ripcord_proto::base64;
use ripcord_proto::crypto::ecdh::{self, Curve, Ecdh, RustCryptoEcdh};
use ripcord_proto::crypto::modes;
use ripcord_proto::halyard::{self, account_seed, control, registration};
use ripcord_proto::stream::{key_schedule, packet_crypto};

/// The vector files this engine has runners for, in the order `ripcord-kat` runs them.
pub const FILES: &[&str] = &[
    "stream-crypto.kat",
    "control-crypto.kat",
    "registration-crypto.kat",
    "account-pairing.kat",
    "session-crypto.kat",
];

/// Line kinds that belong to a layer not yet ported, and which layer. Remove an entry in the change that
/// ports its layer.
pub const DEFERRED: &[(&str, &str)] = &[(
    "accountrgst",
    "the /sess/rgst message layer, which also brings the Rust engine's copy of Client-Type",
)];

#[derive(Default)]
pub struct Report {
    pub passed: usize,
    pub failures: Vec<String>,
    pub deferred: BTreeMap<&'static str, usize>,
}

impl Report {
    pub fn ok(&self) -> bool {
        self.failures.is_empty() && self.passed > 0
    }

    pub fn absorb(&mut self, other: Report) {
        self.passed += other.passed;
        self.failures.extend(other.failures);
        for (k, n) in other.deferred {
            *self.deferred.entry(k).or_default() += n;
        }
    }
}

impl fmt::Display for Report {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        for failure in &self.failures {
            writeln!(f, "FAIL {failure}")?;
        }
        write!(f, "{} passed, {} failed", self.passed, self.failures.len())?;
        for (kind, n) in &self.deferred {
            let why = DEFERRED.iter().find(|(k, _)| k == kind).map_or("", |(_, w)| w);
            write!(f, "\n  deferred: {n} {kind} line(s), awaiting {why}")?;
        }
        Ok(())
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

fn curve(field: &str) -> Result<Curve, String> {
    match field {
        "p256" => Ok(Curve::P256),
        "p521" => Ok(Curve::P521),
        other => Err(format!("unknown curve {other:?}")),
    }
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
    check_text(report, line, label, &to_hex(actual), expect_hex);
}

fn check_text(report: &mut Report, line: &Line<'_>, label: &str, got: &str, want: &str) {
    if got == want {
        report.passed += 1;
    } else {
        report
            .failures
            .push(format!("{} line {}: {label}\n  got  {got}\n  want {want}", line.kind, line.number));
    }
}

/// For a kind whose first field names the console family: whether this line is a PS4 one this build
/// cannot check because it has no PS4 tables. Other kinds carry no family field and are never skipped.
fn unavailable_ps4_line(line: &Line<'_>) -> bool {
    let is_ps4 = line.fields.get(1).is_some_and(|f| *f == "0" || *f == "ps4");
    match line.kind {
        "kdf" => is_ps4 && !halyard::has_ps4_tables(),
        "registration" | "accountwrap" | "seed" => is_ps4 && !halyard::has_ps4_registration(),
        _ => false,
    }
}

fn run_line(report: &mut Report, line: &Line<'_>) -> Result<(), String> {
    let f = |i| line.field(i);
    match line.kind {
        // The generator had the PS4 tables; so must this build, or its PS4 lines would silently skip.
        "ps4tables" => {
            let want = number::<u8>(f(1)?)? != 0;
            check_text(
                report,
                line,
                "PS4 tables present",
                &halyard::has_ps4_tables().to_string(),
                &want.to_string(),
            );
        }

        // ---- stream-crypto.kat ----
        "gmac" => {
            let tag = packet_crypto::gmac(&hex16(f(1)?)?, &hex16(f(2)?)?, &hex(f(3)?)?);
            check(report, line, "tag", &tag, f(4)?);
        }
        "streamkdf" => {
            let keys = key_schedule::derive_direction(&hex(f(1)?)?, &hex16(f(2)?)?, number(f(3)?)?);
            check(report, line, "aesKey", &keys.aes_key, f(4)?);
            check(report, line, "baseIv", &keys.base_iv, f(5)?);
        }
        "packetnonce" => {
            let c = packet_crypto::PacketCrypto::new(&hex16(f(1)?)?, &hex16(f(2)?)?);
            let key_pos: u64 = number(f(3)?)?;
            check(report, line, "gmacNonce", &c.gmac_nonce(key_pos), f(4)?);
            check(report, line, "ctrNonce", &c.ctr_nonce(key_pos), f(5)?);
            check(report, line, "gmacKey", &c.gmac_key(key_pos), f(6)?);
        }
        "packettag" => {
            let mut c = packet_crypto::PacketCrypto::new(&hex16(f(1)?)?, &hex16(f(2)?)?);
            let tag = c
                .compute_tag(number(f(3)?)?, &hex(f(6)?)?, number(f(5)?)?, number::<u8>(f(4)?)? != 0)
                .ok_or("compute_tag refused the packet")?;
            check(report, line, "tag", &tag, f(7)?);
        }

        // ---- control-crypto.kat ----
        "kdf" => {
            let selector =
                if f(1)? == "ps4" { halyard::VERSION_SELECTOR_PS4 } else { halyard::VERSION_SELECTOR_PS5 };
            let keys =
                control::kdf(&hex16(f(2)?)?, &hex16(f(3)?)?, selector).ok_or("family tables absent")?;
            // Key and material as one answer, so a swap shows as a swap rather than two unrelated failures.
            let got = [keys.key, keys.material].concat();
            check(report, line, "key || material", &got, &format!("{}{}", f(4)?, f(5)?));
        }
        "ctxkey" => {
            check(report, line, "contextKey", control::context_key(number(f(1)?)?, number(f(2)?)?), f(3)?);
        }
        "iv" => {
            let iv = control::field_iv(&hex16(f(1)?)?, &hex16(f(2)?)?, number(f(3)?)?);
            check(report, line, "iv", &iv, f(4)?);
        }
        "cfbenc" | "cfbdec" | "ofb" => {
            let (key, iv) = (hex16(f(1)?)?, hex16(f(2)?)?);
            let mut data = hex(f(3)?)?;
            match line.kind {
                "cfbenc" => modes::cfb128(&key, &iv, &mut data, true),
                "cfbdec" => modes::cfb128(&key, &iv, &mut data, false),
                _ => modes::ofb(&key, &iv, &mut data),
            }
            check(report, line, "output", &data, f(4)?);
        }
        "field" => {
            let field =
                control::ControlField::new(&hex16(f(1)?)?, &hex16(f(2)?)?, number(f(3)?)?, number(f(4)?)?)
                    .ok_or("family tables absent")?;
            let counter = number(f(5)?)?;
            let plaintext = hex(f(6)?)?;
            let mut data = plaintext.clone();
            field.encrypt(counter, &mut data);
            check_text(report, line, "ciphertext (base64)", &base64::encode(&data), f(7)?);
            // Round trip: decrypt has its own feedback rule, which an encrypt-only check would miss.
            field.decrypt(counter, &mut data);
            check(report, line, "decrypted", &data, f(6)?);
        }
        "streaminfo" => {
            let field =
                control::ControlField::new(&hex16(f(1)?)?, &hex16(f(2)?)?, number(f(3)?)?, number(f(4)?)?)
                    .ok_or("family tables absent")?;
            let mut data = hex(f(6)?)?;
            field.streaminfo_crypt(number(f(5)?)?, &mut data);
            check(report, line, "output", &data, f(7)?);
        }

        // ---- registration-crypto.kat ----
        "registration" => {
            let is_ps5 = number::<u8>(f(1)?)? != 0;
            let context = hex(f(2)?)?;
            let material = hex16(f(4)?)?;
            let key =
                registration::derive_key(is_ps5, &context, number(f(3)?)?).ok_or("derive_key refused")?;
            check(report, line, "transport key", &key, f(5)?);
            let wrapped = registration::wrap_material(is_ps5, &material, &context).ok_or("wrap refused")?;
            check(report, line, "wrapped material", &wrapped, f(6)?);
            // Wrap and unwrap are written separately; matching the reference's wrap says nothing about unwrap.
            let back = registration::unwrap_material(is_ps5, &wrapped, &context).ok_or("unwrap refused")?;
            check(report, line, "unwrapped", &back, f(4)?);
            let mut scattered = context.clone();
            let gathered = registration::scatter(&wrapped, &mut scattered)
                .then(|| registration::gather(&scattered))
                .flatten()
                .ok_or("scatter/gather refused")?;
            check(report, line, "gather(scatter(wrapped))", &gathered, &to_hex(&wrapped));
        }

        // ---- account-pairing.kat ----
        "seed" => {
            let is_ps5 = number::<u8>(f(1)?)? != 0;
            let (d1, d2, seed) = (hex16(f(2)?)?, hex16(f(3)?)?, hex16(f(4)?)?);
            let ciphertext = hex(f(5)?)?;
            // The console's ciphertext can run past the seed; the first 16 bytes are the sealed seed.
            let sealed = account_seed::seal(is_ps5, &d1, &d2, &seed);
            check(
                report,
                line,
                "sealed",
                &sealed,
                &to_hex(ciphertext.get(..16).ok_or("ciphertext under 16 bytes")?),
            );
            check_text(report, line, "customData1", &account_seed::encode_custom_data1(&ciphertext), f(6)?);
            let recovered = account_seed::recover_custom_data1(is_ps5, &d1, &d2, f(6)?.as_bytes())
                .ok_or("recover refused")?;
            check(report, line, "recovered seed", &recovered, f(4)?);
        }
        "accountwrap" => {
            let is_ps5 = number::<u8>(f(1)?)? != 0;
            let (context, material) = (hex(f(2)?)?, hex16(f(3)?)?);
            let wrapped =
                registration::wrap_account_material(is_ps5, &material, &context).ok_or("wrap refused")?;
            check(report, line, "wrapped", &wrapped, f(4)?);
            let back =
                registration::unwrap_account_material(is_ps5, &wrapped, &context).ok_or("unwrap refused")?;
            check(report, line, "unwrapped", &back, f(3)?);
        }

        // ---- session-crypto.kat ----
        "ecdhpub" => {
            let public = RustCryptoEcdh
                .public_key(curve(f(1)?)?, &hex(f(2)?)?)
                .ok_or("a scalar .NET accepted was refused")?;
            check(report, line, "publicKey", &public, f(3)?);
        }
        "ecdhshared" => {
            let shared = RustCryptoEcdh
                .shared_secret(curve(f(1)?)?, &hex(f(2)?)?, &hex(f(3)?)?)
                .ok_or("shared_secret refused")?;
            check(report, line, "shared", &shared, f(4)?);
        }
        "ecdhsig" => {
            check(
                report,
                line,
                "signature",
                &ecdh::public_key_signature(&hex16(f(1)?)?, &hex(f(2)?)?),
                f(3)?,
            );
        }
        "streamkeys" => {
            let keys = ecdh::stream_keys(
                &RustCryptoEcdh,
                curve(f(1)?)?,
                &hex(f(2)?)?,
                &hex(f(3)?)?,
                &hex16(f(4)?)?,
                number(f(5)?)?,
            )
            .ok_or("key agreement refused")?;
            check(report, line, "aesKey", &keys.aes_key, f(6)?);
            check(report, line, "baseIv", &keys.base_iv, f(7)?);
        }

        other => return Err(format!("no runner for line kind {other:?}")),
    }
    Ok(())
}

/// Runs every line of a vector file.
pub fn run(text: &str) -> Report {
    let mut report = Report::default();
    for (i, raw) in text.lines().enumerate() {
        let fields: Vec<&str> = raw.split_whitespace().collect();
        let Some(&kind) = fields.first() else { continue };
        if kind.starts_with('#') || kind == "version" {
            continue;
        }
        if let Some((k, _)) = DEFERRED.iter().find(|(k, _)| *k == kind) {
            *report.deferred.entry(k).or_default() += 1;
            continue;
        }
        let line = Line { kind, number: i + 1, fields };
        // A PS4 line where this build has no PS4 tables cannot be checked; ps4tables says whether that is
        // expected, and fails if the generator had tables this build lacks.
        if unavailable_ps4_line(&line) {
            continue;
        }
        if let Err(e) = run_line(&mut report, &line) {
            report.failures.push(format!("{} line {}: {e}", line.kind, line.number));
        }
    }
    report
}
