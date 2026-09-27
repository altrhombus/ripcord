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
use ripcord_proto::dgram::assoc::{self, Association};
use ripcord_proto::halyard::{self, account_seed, control, registration};
use ripcord_proto::sess::{launch_spec, regist, requests};
use ripcord_proto::stream::{key_schedule, packet_crypto};
use ripcord_proto::takion::control as takion_control;

/// The vector files this engine has runners for, in the order `ripcord-kat` runs them.
pub const FILES: &[&str] = &[
    "stream-crypto.kat",
    "control-crypto.kat",
    "registration-crypto.kat",
    "account-pairing.kat",
    "session-crypto.kat",
    "control-proto.kat",
    "rendezvous-control.kat",
    "dgram-transport.kat",
];

/// Line kinds that belong to a layer not yet ported, and which layer. Remove an entry in the change that
/// ports its layer.
pub const DEFERRED: &[(&str, &str)] = &[];

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

fn run_line(report: &mut Report, line: &Line<'_>, ecdh: &dyn Ecdh) -> Result<(), String> {
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
            let key = control::context_key(number(f(1)?)?, number(f(2)?)?).ok_or("family tables absent")?;
            check(report, line, "contextKey", key, f(3)?);
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
            let sealed = account_seed::seal(is_ps5, &d1, &d2, &seed).ok_or("family tables absent")?;
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
            let public =
                ecdh.public_key(curve(f(1)?)?, &hex(f(2)?)?).ok_or("a scalar .NET accepted was refused")?;
            check(report, line, "publicKey", &public, f(3)?);
        }
        "ecdhshared" => {
            let shared = ecdh
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
                ecdh,
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

        // ---- control-proto.kat ----
        // "-" in a required field is present-but-empty; in an optional one it is absent.
        "sessionreq" => {
            let (session_key, launch_spec, encrypted_key) = (hex(f(2)?)?, hex(f(3)?)?, hex(f(4)?)?);
            let (public_key, signature) = (hex(f(5)?)?, hex(f(6)?)?);
            let request = takion_control::SessionRequest {
                client_version: number(f(1)?)?,
                session_key: &session_key,
                launch_spec: &launch_spec,
                encrypted_key: &encrypted_key,
                ecdh_public_key: (f(5)? != "-").then_some(&public_key[..]),
                ecdh_signature: (f(6)? != "-").then_some(&signature[..]),
            };
            let encoded = request.build();
            check(report, line, "encoded", &encoded, f(7)?);
            let peeked = takion_control::peek_type(&encoded).map_or("none".into(), |t| t.to_string());
            check_text(report, line, "peeked type", &peeked, &takion_control::SESSION_REQUEST.to_string());
        }
        "sessionreply" => {
            let encoded = hex(f(1)?)?;
            let reply = takion_control::parse_session_reply(&encoded).ok_or("parse failed")?;
            let flag = |b: bool| u8::from(b).to_string();
            check_text(report, line, "serverVersion", &reply.server_version.to_string(), f(2)?);
            check_text(report, line, "token", &reply.token.to_string(), f(3)?);
            check_text(report, line, "encryptedKeyAccepted", &flag(reply.encrypted_key_accepted), f(4)?);
            check_text(report, line, "versionAccepted", &flag(reply.version_accepted), f(5)?);
            let opt = |v: Option<&[u8]>| v.map_or("-".to_owned(), to_hex);
            let key = if reply.session_key.is_empty() { "-".to_owned() } else { to_hex(reply.session_key) };
            check_text(report, line, "sessionKey", &key, f(6)?);
            check_text(report, line, "serverVersionString", &opt(reply.server_version_string), f(7)?);
            check_text(report, line, "ecdhPublicKey", &opt(reply.ecdh_public_key), f(8)?);
            check_text(report, line, "ecdhSignature", &opt(reply.ecdh_signature), f(9)?);
            check_text(report, line, "has_ecdh", &reply.has_ecdh().to_string(), &(f(8)? != "-").to_string());
            // No proper prefix of a reply may parse as a complete one.
            let truncated =
                (1..encoded.len()).find(|&n| takion_control::parse_session_reply(&encoded[..n]).is_some());
            check_text(report, line, "no truncation accepted", &format!("{truncated:?}"), "None");
        }

        "launchspec" => {
            let params = launch_spec::Params {
                width: number(f(1)?)?,
                height: number(f(2)?)?,
                fps: number(f(3)?)?,
                bitrate_kbps: number(f(4)?)?,
                mtu: number(f(5)?)?,
                rtt_ms: number(f(6)?)?,
                hevc: number::<u8>(f(7)?)? != 0,
                hdr: number::<u8>(f(8)?)? != 0,
            };
            check(report, line, "json", launch_spec::build(&params, &hex16(f(9)?)?).as_bytes(), f(10)?);
        }

        // ---- account-pairing.kat: the whole account-route exchange ----
        "accountrgst" => {
            let is_ps5 = number::<u8>(f(1)?)? != 0;
            let context: [u8; 0x1e0] = hex(f(3)?)?.try_into().map_err(|_| "context is not 0x1e0 bytes")?;
            let (material, seed) = (hex16(f(4)?)?, hex16(f(5)?)?);
            let (exchange, request) =
                regist::Exchange::account(is_ps5, &seed, f(2)?, "192.0.2.10", &context, &material)
                    .map_err(|e| format!("exchange refused: {e:?}"))?;
            check(report, line, "transport key", &exchange.transport_key(), f(6)?);
            let body = hex(f(7)?)?;
            let tail = request.get(request.len().saturating_sub(body.len())..).unwrap_or_default();
            check(report, line, "body", tail, f(7)?);
            // The console's reply, opened with the same key, gives the reference's record.
            let mut response = b"HTTP/1.1 200 OK\r\n\r\n".to_vec();
            response.extend(hex(f(9)?)?);
            let record = exchange.open(&response).map_err(|e| format!("reply refused: {e:?}"))?;
            check(report, line, "registration key", &record.registration_key, f(10)?);
            check(report, line, "companion", &record.companion, f(11)?);
            check_text(report, line, "key type", &record.key_type.to_string(), f(12)?);
        }

        other => return Err(format!("no runner for line kind {other:?}")),
    }
    Ok(())
}

/// Runs every line of a vector file, with key agreement on the RustCrypto backend.
pub fn run(text: &str) -> Report {
    run_with(text, &RustCryptoEcdh)
}

/// Runs every line of a vector file with key agreement on `ecdh`: how each platform's backend is checked
/// against the .NET vectors on its own platform (docs/engine-plan.md, "Dependencies").
pub fn run_with(text: &str, ecdh: &dyn Ecdh) -> Report {
    let mut report = Report::default();
    let mut case: Option<RendezvousCase> = None;
    let mut association: Option<Association> = None;
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
        // rendezvous-control.kat: a case line, then the init and ctrl requests the .NET session sent for it.
        // dgram-transport.kat: an assoc line starts a fresh association, and each step replays one call.
        if matches!(kind, "assoc" | "step") {
            if let Err(e) = run_transcript(&mut report, &line, &mut association) {
                report.failures.push(format!("{} line {}: {e}", line.kind, line.number));
            }
            continue;
        }
        if matches!(kind, "case" | "init" | "ctrl") {
            if let Err(e) = run_rendezvous(&mut report, &line, &mut case) {
                report.failures.push(format!("{} line {}: {e}", line.kind, line.number));
            }
            continue;
        }
        // A PS4 line where this build has no PS4 tables cannot be checked; ps4tables says whether that is
        // expected, and fails if the generator had tables this build lacks.
        if unavailable_ps4_line(&line) {
            continue;
        }
        if let Err(e) = run_line(&mut report, &line, ecdh) {
            report.failures.push(format!("{} line {}: {e}", line.kind, line.number));
        }
    }
    report
}

/// One `case` of rendezvous-control.kat: a pairing and a nonce, whose /sess/init and /sess/ctrl the .NET
/// session sent over the rendezvous route (padded Host with port 9303, "Rp-Version" on init, ConPath 3).
struct RendezvousCase {
    is_ps5: bool,
    addressing: requests::Addressing,
    registration_key: Vec<u8>,
    companion: [u8; 16],
    device_id: Vec<u8>,
    nonce: [u8; 16],
    os_major: i32,
    os_minor: i32,
    bitrate: i32,
}

fn run_rendezvous(
    report: &mut Report,
    line: &Line<'_>,
    case: &mut Option<RendezvousCase>,
) -> Result<(), String> {
    let f = |i| line.field(i);
    match line.kind {
        "case" => {
            let address: std::net::Ipv4Addr = f(2)?.parse().map_err(|_| format!("bad host {:?}", f(2)))?;
            *case = Some(RendezvousCase {
                is_ps5: f(1)? == "ps5",
                addressing: requests::Addressing::new(
                    address.octets(),
                    9303,
                    requests::ConnectionPath::Rendezvous,
                ),
                registration_key: hex(f(3)?)?,
                companion: hex16(f(4)?)?,
                device_id: hex(f(5)?)?,
                nonce: hex16(f(6)?)?,
                os_major: number(f(7)?)?,
                os_minor: number(f(8)?)?,
                bitrate: number(f(9)?)?,
            });
        }
        "init" => {
            let c = case.as_ref().ok_or("init before any case")?;
            check(
                report,
                line,
                "/sess/init",
                &requests::init(c.is_ps5, &c.addressing, &c.registration_key),
                f(1)?,
            );
        }
        _ => {
            let c = case.take().ok_or("ctrl before any case")?;
            let reply = format!(
                "HTTP/1.1 200 OK\r\nRP-Nonce: {}\r\nContent-Length: 0\r\n\r\n",
                base64::encode(&c.nonce)
            );
            let reply = ripcord_proto::sess::http::Response::parse(reply.as_bytes())
                .ok_or("the synthetic reply did not parse")?;
            let field = requests::open_init(c.is_ps5, &reply, &c.companion).map_err(|e| format!("{e:?}"))?;
            let values = requests::CtrlFields {
                registration_key: &c.registration_key,
                device_id: &c.device_id,
                os_major: c.os_major,
                os_minor: c.os_minor,
                start_bitrate_kbps: c.bitrate,
                streaming_type: 0,
            };
            check(
                report,
                line,
                "/sess/ctrl",
                &requests::ctrl(c.is_ps5, &c.addressing, &field, &values),
                f(1)?,
            );
        }
    }
    Ok(())
}

/// The counting random source the .NET transcripts were generated with: successive bytes from 0x40.
fn counting_random() -> ripcord_proto::RandomSource {
    let mut next = 0x40u8;
    Box::new(move |out: &mut [u8]| {
        for b in out {
            *b = next;
            next = next.wrapping_add(1);
        }
    })
}

fn phase_name(p: assoc::Phase) -> &'static str {
    match p {
        assoc::Phase::Idle => "idle",
        assoc::Phase::Handshaking => "handshaking",
        assoc::Phase::Established => "established",
        assoc::Phase::Connected => "connected",
        assoc::Phase::Closed => "closed",
    }
}

fn event_name(e: &assoc::Event) -> String {
    match e {
        assoc::Event::PreludeEstablished => "established".into(),
        assoc::Event::ConnectionOpened { by_peer } => format!("opened:{}", u8::from(*by_peer)),
        assoc::Event::DataReceived { data, .. } => format!("data:{}", to_hex(data)),
        assoc::Event::PeerClosed => "closed".into(),
        assoc::Event::Unhandled { .. } => "unhandled".into(),
    }
}

fn run_transcript(
    report: &mut Report,
    line: &Line<'_>,
    association: &mut Option<Association>,
) -> Result<(), String> {
    let f = |i| line.field(i);
    if line.kind == "assoc" {
        let id = |i| -> Result<[u8; 20], String> {
            hex(f(i)?)?.try_into().map_err(|_| "an id is not 20 bytes".to_owned())
        };
        let address: [u8; 4] = hex(f(4)?)?.try_into().map_err(|_| "the address is not 4 bytes")?;
        *association = Some(Association::new(counting_random(), id(2)?, id(3)?, address, number(f(5)?)?));
        return Ok(());
    }
    let a = association.as_mut().ok_or("a step before any assoc")?;
    match f(1)? {
        "open" => a.open(),
        "retry" => a.retry(),
        "openconn" => {
            let words = match number::<u8>(f(2)?)? {
                1 => assoc::Addressing::PeerOnly,
                2 => assoc::Addressing::SinglePort,
                _ => assoc::Addressing::PortPair,
            };
            a.open_connection(words);
        }
        "reopen" => a.reopen_connection(),
        "closeconn" => a.close_connection(),
        "send" => {
            let _ = a.send(&hex(f(2)?)?);
        }
        "recv" => a.on_datagram(&hex(f(2)?)?),
        "clear" => a.clear_inbound(),
        other => return Err(format!("unknown op {other:?}")),
    }
    check_text(report, line, "phase", phase_name(a.phase()), f(3)?);
    let sends: Vec<String> = std::iter::from_fn(|| a.poll_transmit()).map(|d| to_hex(&d)).collect();
    check_text(report, line, "sends", &if sends.is_empty() { "-".into() } else { sends.join(",") }, f(4)?);
    let events: Vec<String> = std::iter::from_fn(|| a.poll_event()).map(|e| event_name(&e)).collect();
    check_text(report, line, "events", &if events.is_empty() { "-".into() } else { events.join(",") }, f(5)?);
    Ok(())
}
